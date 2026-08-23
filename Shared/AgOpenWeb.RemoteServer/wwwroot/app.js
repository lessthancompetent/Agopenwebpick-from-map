// Renderer — consumes plain Scene + Tick objects via the transport interface.
// It has NO knowledge of SignalR or the wire format (see transport.js); swapping
// the transport at Phase 2 leaves this file untouched. Canvas2D for now; the
// CanvasKit swap (and client-side dead-reckoning) slot in here without touching
// the transport seam.

const ckcv = document.getElementById('ck'); // CanvasKit (Skia) — the sole renderer (matches native)

// Logical (CSS-pixel) canvas size. The backing store is scaled by the device
// pixel ratio so vectors render at native resolution on hi-DPI screens (tablets,
// retina) — otherwise thin strokes look faint and shimmer when panning. All draw
// code works in these logical coordinates; the Skia canvas applies scale(dpr) per frame.
let vw = innerWidth, vh = innerHeight, dpr = 1;
// Skia/CanvasKit renderer state — declared before resize() (which calls
// recreateSkSurface) to avoid a temporal-dead-zone ReferenceError.
let CK = null, skSurface = null, skTri = null, SKP = null;
let grCtx = null; // WebGL GrDirectContext — hoisted so the coverage render target shares it
function resize() {
  // Render the map surface at CSS resolution by default: it's lines and fills,
  // and supersampling it 1.5-3x melts weak iGPUs (FZ-G1's HD 5500 dropped to a
  // jerky crawl at DPR 1.5 = 2.25x the pixels). DOM text/UI keeps full-DPR
  // crispness regardless — this only softens the map slightly on HiDPI.
  // Override per device via localStorage.mapDpr (e.g. "1.5") if wanted.
  dpr = Math.min(window.devicePixelRatio || 1, parseFloat(localStorage.mapDpr || '1') || 1);
  vw = innerWidth; vh = innerHeight;
  ckcv.width = Math.round(vw * dpr);
  ckcv.height = Math.round(vh * dpr);
  ckcv.style.width = vw + 'px';
  ckcv.style.height = vh + 'px';
  recreateSkSurface(); // the GL surface is tied to the canvas size
}
addEventListener('resize', resize); resize();

// ---- model (fed by the transport) ----
let scene = null;      // SceneDto
let tick = null;       // TickDto (latest — for sections/HUD)
let lastTick = null;   // newest authoritative pose (HUD readouts + guards)
// Ring of recent authoritative poses (oldest→newest). The render INTERPOLATES between
// the two that bracket the playback head. A MULTI-pose buffer (not just prev+last) is
// what lets RENDER_DELAY exceed the ~100 ms pose interval without the playhead falling
// off the old end → that off-the-end clamp was the 10 Hz "judder".
const poseBuf = [];
const POSE_BUF_MAX = 12; // ~1.2 s @ 10 Hz — plenty of bracket for the delay + WiFi jitter
// Render slightly in the PAST so every frame interpolates between two REAL poses instead of
// extrapolating/predicting past the latest. This is what kills the per-tick re-anchor snap
// (the whole-world jump at speed) AND the straight-line turn deviation — both are
// extrapolation artifacts. Cost: a small constant display lag (~RENDER_DELAY × speed). Must
// exceed the pose interval (~100 ms @ 10 Hz) plus arrival jitter so we rarely run out of buffer.
// Buffer depth behind the newest pose. Kept SMALL (low display lag) so the dead-reckoned
// vehicle sprite stays aligned with the host-accumulated coverage — a large delay made the
// tractor visibly trail its own coverage. WiFi inter-arrival spikes that would otherwise
// drain the buffer are bridged by EXTRAPOLATION (sample(), below) rather than a freeze, so
// we get smoothness without the lag. ~130 ms covers the pose interval + interpolation room.
const RENDER_DELAY = 130; // ms
// Max coverage cells rasterized per frame. A full-coverage snapshot (sent by the host on
// grid bounds-expansion / reconnect / field reload) can be hundreds of thousands of cells;
// draining it all in one frame is a multi-hundred-ms main-thread block that froze the map
// then jumped the tractor forward (issue #34). We instead drain up to this many cells per
// frame and resume the rest next frame (progress stored on batch.off), so a big snapshot
// paints over a handful of frames with no visible hitch. ~40k rects ≈ 1-2 ms/frame.
const COV_DRAIN_BUDGET = 40000;
// Max time we'll extrapolate past the newest pose when the buffer underruns (WiFi spike /
// dropped pose) before settling to a hold — bridges spikes without flying off on a real
// dropout. At field speed 200 ms is well under a pose-pair's worth of travel.
const EXTRAP_CAP_MS = 200;
// Smoothed client↔host clock offset: host_time ≈ client_time − clockOffset. Estimated from the
// host monotonic timestamp on each Tick (EMA, so one jittery arrival can't shift the timeline).
let clockOffset = null;
let connState = 'connecting…';
let ckStatus = 'loading…'; // CanvasKit init status (renderer migration prep)
let statusBar = null;  // top status-bar readouts (fix/age/sats/units/modules)
let config = null;     // config read-frame (vehicle config, …) for the left-nav panels
let configDirty = false; // a new config frame arrived → settings panels re-read it
let mapIsDay = false;  // day/night theme (mirrors config.display.isDayMode) — drives the
                       // CSS palette ([data-theme]) and the Skia map clear/grid colours
let profiles = null;   // Vehicle & Tool picker hub: profile lists + active pair
let profilesDirty = false;
let ntripProfiles = null; // Network IO: saved NTRIP profiles + associable fields
let ntripDirty = false;
let fieldOps = null;   // Field Operations: field list + jobs + suggestions + import files
let fieldOpsDirty = false;
let agShare = null;    // AgShare: settings + cloud action status/results
let agShareDirty = false;
let appInfo = null;    // File/App menu: version, languages, directories, hotkeys, logs
let appInfoDirty = false;
let fieldTools = null; // Field Tools read-frame: import-track field list
let fieldToolsDirty = false;
let recPath = null;    // Recorded Path read-frame: rec files + record/play state
let recPathTab = 0;    // client-side tab: 0 Record, 1 Playback
let recSelFile = null; // client-side selected .rec file
let boundary = null;   // Boundary read-frame: menu list + drive-around recording state
let wizard = null;     // Steer Wizard frame (host-driven); null when not open
let wizardDirty = false;
let fps = 0;           // smoothed client render rate (for the GPS-detail card)
let _fpsFrames = 0, _fpsT0 = 0;
let _diagT0 = 0;       // throttle (~4 Hz) for the marker-gated diag line's DOM writes
let linkRttMs = null;  // EMA of server↔client round-trip (ms), from diag.ping/PONG
// Remote actuation authority (Phase 2 safety layer): our connection id (from the
// Hello frame), the latest broadcast control state, and whether we hold control.
let myClientId = null;
let lastControl = { held: false, holderId: '', holderName: '' };
let iHoldControl = false;

// ---- coverage offscreen (Phase 2): cells painted into a cell-grid canvas,
//      blitted to world space each frame. Snapshot on connect, deltas after. ----
let cov = null;        // { cellSize, originE, originN, width, height, canvas, cctx }
let covCells = 0;
let coverageEdges = null;   // crisp worked-area perimeter polylines (server-fed, field-local m)
let covEdgeRgb = 0x98fb98;  // last coverage colour seen → the perimeter stroke matches the fill
const EDGE_OFF = new URLSearchParams(location.search).has('noedge'); // ?noedge → A/B the edge off

// ---- ground texture: the tiled field backdrop. Two variants — a dark (night) and a
//      light (day) version of the same texture — picked by mapIsDay so the field reads
//      against the matching theme. Drawn as a repeating shader in world space, gated on
//      Display.FieldTextureVisible. Each decoded to an SkImage on first use. ----
const groundImgNight = new Image(), groundImgDay = new Image();
let groundReadyNight = false, groundReadyDay = false;
let skGroundNight = null, skGroundDay = null;
groundImgNight.onload = () => { groundReadyNight = true; };
groundImgDay.onload = () => { groundReadyDay = true; };
groundImgNight.src = '/icons/GroundTextureDark.png';
groundImgDay.src = '/icons/GroundTextureLight.png';

// ---- tractor sprite (TractorAoG.png) — drawn world-sized on the ground, replacing the
//      fallback triangle. Sized from the vehicle track-width/wheelbase like native. ----
const tractorImg = new Image();
let tractorReady = false, skTractor = null;
tractorImg.onload = () => { tractorReady = true; };
tractorImg.src = '/icons/TractorAoG.png';
// Steerable front-wheel sprite (single wheel, drawn at both front-axle ends, rotated by
// the live wheel angle) — matches native SkiaMapControl's FrontWheels.png.
const frontWheelImg = new Image();
let frontWheelReady = false, skFrontWheel = null;
frontWheelImg.onload = () => { frontWheelReady = true; };
frontWheelImg.src = '/icons/FrontWheels.png';

// ---- background imagery: extent from the Scene, PNG fetched over HTTP. ----
let imageryRect = null;  // { minE, minN, maxE, maxN, version }
let imageryImg = null;   // loaded <img> once ready
let imageryVer = null;   // version currently loaded (cache-bust on change)

// ---- field geo origin (lat/lon at E=0,N=0) — for the satellite-tile underlay. ----
let originLat = 0, originLon = 0;
let satEnabled = false;  // true while drawing a boundary on satellite imagery

// ---- client-owned camera (never crosses the wire) ----
let pxPerM = 4.0;
let camE = 0, camN = 0;
// Camera follow mode, matching native SkiaMapControl: 0 NorthUp (lock to vehicle),
// 1 HeadingUp (lock + rotate map to heading), 2 Free (manual pan holds), 3 Map
// (map-centric auto-pan with a safe zone). Default Map, like native.
let cameraMode = 3;
let mapRotation = 0;          // radians the map is rotated (−heading in HeadingUp)
let _cosRR = 1, _sinRR = 0;   // cos/sin of the screen rotation (−mapRotation), per frame
const AUTO_PAN_SAFE = 0.65, AUTO_PAN_SMOOTH = 0.15; // match native tuning
// Map-rotation easing (HeadingUp). ADAPTIVE: heavily damp SMALL heading errors so the
// whole heading-up map doesn't wobble with autosteer line-holding dither, but stay
// responsive for LARGE errors (real headland turns). alpha ramps MIN→MAX as the heading
// error grows to ROT_SMOOTH_FULL rad. (A single fixed value either wobbles on the line or
// lags the turns.)
const ROT_SMOOTH_MIN = 0.035; // ~0.5 s settle — kills line-holding dither (~0.2-0.3°)
const ROT_SMOOTH_MAX = 0.35;  // crisp on real turns (≈ the old fixed 0.3)
const ROT_SMOOTH_FULL = 0.12; // rad (~7°) error at which we're fully responsive
function isFollowMode() { return cameraMode !== 2; }
// 3D perspective tilt (Skia only — Canvas2D can't do true perspective). pitch 0 =
// top-down (identical to the ortho path); up to MAX_PITCH tilts toward the horizon.
// perspM is the world→CSS-px matrix for the current frame (null when top-down).
let pitch = 0;          // radians
let perspM = null;      // 16-elem M44 (world→CSS px) or null
let perspMInv = null;   // cached 4×4 inverse of perspM (CSS px→world ray); null until built
const DEFAULT_PITCH = Math.PI / 3;        // 60° — the one-key tilt
const MAX_PITCH = 65 * Math.PI / 180;     // v1 cap: keeps the local field in front
const PITCH_STEP = 5 * Math.PI / 180;
// Persist the 3D tilt + zoom across reloads (issue #35: tilting to 3D was lost on
// restart). The camera is client-owned live, but its last tilt+zoom are stored
// HOST-side (appstate.json, via the view.save command) and replayed to every client
// in the connection seed (onViewPrefs). This restores identically on any client —
// browser or WebView — independent of browser localStorage behaviour.
let _savedPitch = pitch, _savedZoom = pxPerM, _viewSaveT = 0;
function applyViewPrefs(p, z) {
  if (typeof p === 'number' && isFinite(p)) pitch = Math.max(0, Math.min(MAX_PITCH, p));
  if (typeof z === 'number' && isFinite(z)) pxPerM = Math.min(200, Math.max(0.2, z));
  _savedPitch = pitch; _savedZoom = pxPerM; // hydrated value == saved, so no echo-back save
}
function maybeSaveView() {
  if (pitch === _savedPitch && pxPerM === _savedZoom) return;
  const now = performance.now();
  if (now - _viewSaveT < 800) return;        // debounce: at most ~1.25 writes/s while adjusting
  _viewSaveT = now; _savedPitch = pitch; _savedZoom = pxPerM;
  transport.send('view.save|' + pitch + '|' + pxPerM); // host writes it to appstate.json
}
const PERSP_FOV = 0.7;                     // rad, matches native SkiaMapControl

// ---- transport wiring (the only coupling point) ----
const transport = RemoteTransport.create({
  onScene(s) {
    scene = s;
    originLat = s.originLat; originLon = s.originLon; // for the satellite underlay geo math
    if (isFollowMode() && (!tick || !tick.pose) && s.boundaries.length && s.boundaries[0].length) {
      const r = s.boundaries[0];
      camE = r.reduce((a, p) => a + p.e, 0) / r.length;
      camN = r.reduce((a, p) => a + p.n, 0) / r.length;
    }
    // Background imagery: (re)load the PNG only when the version changes.
    if (s.imagery) {
      imageryRect = s.imagery;
      if (s.imagery.version !== imageryVer) {
        imageryVer = s.imagery.version;
        const img = new Image();
        img.onload = () => { imageryImg = img; };
        img.src = '/backpic.png?v=' + s.imagery.version;
      }
    } else {
      imageryRect = null; imageryImg = null; imageryVer = null;
    }
    // Keep the Tracks manager list live (activate/add/delete/hide change the Scene).
    if (document.getElementById('dlg-tracks').classList.contains('open')) renderTracksList();
    // Keep the Route Planner's reference-track picker in step with the field's
    // tracks (a freshly plotted AB appears without reopening the panel).
    if (document.getElementById('routeplan').classList.contains('open')) rpRender();
    if (document.getElementById('fieldbuilder').classList.contains('open') && fbTab === 'tracks'
        && !document.querySelector('#fb-tracklist .flg-nameedit')) renderFbTracks();
    if (document.getElementById('fieldbuilder').classList.contains('open') && fbTab === 'headland'
        && document.getElementById('fb-addhl').hidden
        && !document.querySelector('#fb-hllist .flg-nameedit')) renderFbHeadland();
    if (document.getElementById('fieldbuilder').classList.contains('open') && fbTab === 'tram') {
      if (document.getElementById('fb-tramedit').hidden) renderFbTram(); else populateTramEdit();
    }
    // Refresh the flag list on add/delete/rename/recolour, unless the user is mid-edit.
    if (document.getElementById('dlg-flags').classList.contains('open')
        && !document.querySelector('.flg-nameedit') && !document.querySelector('.flg-colorpick'))
      renderFlagList();
  },
  onTick(t) {
    tick = t;
    if (t.pose) {
      lastTick = {
        e: t.pose.e, n: t.pose.n, heading: t.pose.heading, speed: t.pose.speed,
        tool: t.tool, t: performance.now(), hostT: t.hostMs,
      };
      // Keep the buffer strictly increasing in host time. The 30 Hz render-pull vs 10 Hz
      // broadcast can occasionally resend the same pose (equal hostT) — a duplicate/
      // backward stamp would break the bracket search, so replace rather than append.
      const prev = poseBuf[poseBuf.length - 1];
      if (prev && typeof t.hostMs === 'number' && typeof prev.hostT === 'number' && t.hostMs <= prev.hostT)
        poseBuf[poseBuf.length - 1] = lastTick;
      else {
        poseBuf.push(lastTick);
        if (poseBuf.length > POSE_BUF_MAX) poseBuf.shift();
      }
      // Track the client↔host clock offset (EMA). The interp SPAN comes from the
      // jitter-free host timestamps; this offset only aligns the playback timeline, so a
      // single late/early arrival shifts it by ≤5% instead of warping the whole frame.
      if (typeof t.hostMs === 'number') {
        const raw = lastTick.t - t.hostMs;
        // Very slow EMA (0.01): the client↔host clock barely drifts (ppm), so adapting
        // slowly keeps the playhead velocity steady — a faster EMA chased arrival jitter and
        // left residual position stutter (offline sim: 0.02→12%, 0.01→6% spread). The
        // RENDER_DELAY buffer absorbs the slower settle. Errs high (more lag) = edge-safe.
        clockOffset = (clockOffset === null) ? raw : clockOffset + 0.01 * (raw - clockOffset);
      }
    }
    pushChartData(t);
  },
  onCoverageInit(init) {
    const cs = init.cellSize;
    // Grid GROWTH at the same cell size (host bounds-expansion, reset=false): the added ground is
    // empty, so the host resends nothing. Keep the coverage we already have, re-anchor it into the
    // larger grid, and carry on with incremental deltas — no rescan, no repaint, no flicker. A
    // reset init (new field / reload / cell-size change) always falls through to a clean rebuild.
    if (!init.reset && cov && Math.abs(cov.cellSize - cs) < 1e-9 &&
        (init.width !== cov.width || init.height !== cov.height ||
         Math.abs(init.originE - cov.originE) > 1e-9 || Math.abs(init.originN - cov.originN) > 1e-9)) {
      reanchorCoverage(init);
      return;
    }
    // Fresh field / resolution (cell-size) change: tear down and rebuild. A full snapshot follows.
    if (cov) {
      if (cov.skImg) cov.skImg.delete();
      if (cov.surface) cov.surface.delete();
      if (cov.covPaint) cov.covPaint.delete();
    }
    cov = makeCovGrid(init);
    covCells = 0;
  },
  onCoverageCells(msg) {
    if (!cov || !msg.cells) return;
    // Queue with arrival time; the actual rasterization is delayed by RENDER_DELAY in
    // drawCoverageSk (both render paths) so freshly-laid coverage appears on the SAME
    // playback timeline as the render-delayed tool/pose. Drawing it on arrival put the
    // real-time coverage front AHEAD of the delayed tool sprite, so coverage poked out in
    // front of the tool at speed (gap = speed × RENDER_DELAY; hidden under the tool below
    // ~5 km/h, visible above). arrival + RENDER_DELAY matches the host-timeline pose
    // playback exactly (same socket/latency), so the front now tracks the tool at any speed.
    cov.pending.push({ cells: msg.cells, t: performance.now() });
  },
  onCoverageEdge(polylines) { coverageEdges = polylines; }, // crisp worked-area perimeter (~2 Hz)
  onStatusBar(s) {
    statusBar = s; if (typeof applySimBarVisible === 'function') applySimBarVisible(); syncUnsavedCov();
    // Network IO panel renders from this frame — keep it live while open
    // (it used to render once at open and freeze).
    const nio = document.getElementById('networkio');
    if (nio && nio.classList.contains('open') && typeof renderNetworkIo === 'function') renderNetworkIo();
  },
  onConfig(c) { config = c; configDirty = true; applyTheme(c && c.display && c.display.isDayMode); },
  onProfiles(p) { profiles = p; profilesDirty = true; },
  onNtripProfiles(p) { ntripProfiles = p; ntripDirty = true; },
  onFieldOps(f) { fieldOps = f; fieldOpsDirty = true; },
  onAgShare(a) { agShare = a; agShareDirty = true; },
  onAppInfo(a) { appInfo = a; appInfoDirty = true; },
  onFieldTools(f) { fieldTools = f; fieldToolsDirty = true; if (document.getElementById('importtracks').classList.contains('open')) renderImportTracks(); },
  onRecordedPath(r) { recPath = r; if (document.getElementById('recpath').classList.contains('open')) renderRecPath(); },
  onBoundary(b) {
    boundary = b;
    if (document.getElementById('boundarymenu').classList.contains('open')) renderBoundaryMenu();
    if (document.getElementById('boundaryplayer').classList.contains('open')) renderBoundaryPlayer();
  },
  onWizard(w) { wizard = w; wizardDirty = true; },
  onHello(id) { myClientId = id; updateControlUi(); claimSeatIfFree(); applyMobileQualityCap(); },
  onControlState(s) { lastControl = s; updateControlUi(); claimSeatIfFree(); },
  onSound(id) { Sounds.play(id); },
  // Host VM StatusMessage (refusals + feedback) → transient toast. This is why a refused
  // press used to look like a dead button: the reason never left the host.
  onHint(text) { flashHint(text, 4000); },
  // Round-trip link probe reply: token is the performance.now() we sent in diag.ping, so
  // RTT = now − token measures the pure server↔client link (one client clock, no skew).
  onPong(token) {
    const rtt = performance.now() - parseFloat(token);
    if (rtt >= 0 && rtt < 60000) linkRttMs = (linkRttMs === null) ? rtt : linkRttMs + 0.3 * (rtt - linkRttMs);
  },
  onStatus(s) { connState = s; renderRole(); claimSeatIfFree(); },
  // Persisted web-camera view (issue #35): restore last tilt+zoom from the host seed.
  onViewPrefs(pitch, zoom) { applyViewPrefs(pitch, zoom); },
});

// ---- Alert sounds ----------------------------------------------------------
// The host (which may be a headless box with no speaker) decides WHAT is audible
// — it already applied each sound's config toggle — and pushes a SoundEffect id.
// We just play the matching .wav here, on the operator's device. Effect ids match
// the C# SoundEffect enum order. Browsers block audio until the page has had a user
// gesture, so we prime the elements on the first pointer/key event; until then an
// alert that fires before any interaction is silently dropped (unavoidable on web).
const Sounds = (() => {
  // index === (int)SoundEffect; Alarm10 covers BoundaryAlarm + UTurnTooClose.
  const FILES = [
    'Alarm10',    // 0 BoundaryAlarm
    'Alarm10',    // 1 UTurnTooClose
    'SteerOn',    // 2 AutoSteerOn
    'SteerOff',   // 3 AutoSteerOff
    'HydUp',      // 4 HydraulicLiftUp
    'HydDown',    // 5 HydraulicLiftDown
    'rtk_lost',   // 6 RtkLost
    'rtk_back',   // 7 RtkRecovered
    'SectionOn',  // 8 SectionOn
    'SectionOff', // 9 SectionOff
    'Headland',   // 10 Headland
  ];
  const cache = new Map();   // name -> HTMLAudioElement (preloaded)
  let unlocked = false;

  function el(name) {
    let a = cache.get(name);
    if (!a) { a = new Audio('/sounds/' + name + '.wav'); a.preload = 'auto'; cache.set(name, a); }
    return a;
  }
  // Preload every distinct file so the first real alert isn't delayed by a fetch.
  for (const n of new Set(FILES)) el(n);

  function unlock() {
    if (unlocked) return;
    unlocked = true;
    // Nudge each element into a "played once" state so later programmatic play()
    // (not tied to a gesture) is allowed by the autoplay policy. Do it MUTED: the pause
    // lands asynchronously in .then(), so an unmuted prime plays a burst of every alert
    // audibly on the first UI click — notably the Hyd up/down 3-pt-hitch sounds. Muted
    // playback still primes the element (and is always autoplay-allowed). Real plays clone
    // the element, so they're unaffected by this temporary mute; restore it afterward anyway.
    for (const a of cache.values()) {
      a.muted = true;
      a.play().then(() => { a.pause(); a.currentTime = 0; a.muted = false; }).catch(() => { a.muted = false; });
    }
    window.removeEventListener('pointerdown', unlock);
    window.removeEventListener('keydown', unlock);
  }
  window.addEventListener('pointerdown', unlock, { passive: true });
  window.addEventListener('keydown', unlock);

  return {
    play(id) {
      const name = FILES[id];
      if (!name) return;
      // Clone the preloaded element so rapid repeats (e.g. section on/off bursts)
      // overlap instead of cutting each other off; the clone shares the cached file.
      try { el(name).cloneNode().play().catch(() => {}); } catch { /* not ready */ }
    },
  };
})();

// Mobile auto-quality. Phones/tablets get GPU-overloaded at Ultra resolution (large
// coverage + imagery textures), so on connect a mobile client asks the host to CAP the
// display quality at Medium (multiplier 2.5). The host command only ever coarsens and is
// idempotent, so it never raises a manually-chosen lower quality and never fights a manual
// change made afterwards (we send it ONCE per session). Desktop browsers are left untouched.
// iPadOS reports a desktop ("Macintosh") UA, so also treat a touch-capable "Macintosh".
const IS_MOBILE = /Mobi|Android|iPhone|iPad|iPod/i.test(navigator.userAgent)
  || (navigator.maxTouchPoints > 1 && /Macintosh/.test(navigator.userAgent));
let mobileQualityCapped = false;
function applyMobileQualityCap() {
  if (mobileQualityCapped || !IS_MOBILE) return;
  mobileQualityCapped = true;
  transport.send('display.capResolution|2.5'); // 2.5 = Medium
}

transport.start();

// ---- CanvasKit (Skia) renderer — built alongside Canvas2D for A/B (toggle K).
//      Phase A: vector layers at parity; coverage/imagery/tool/lightbar next.
//      (State declared near the top so resize() can call recreateSkSurface.) ----
function recreateSkSurface() {
  if (!CK) return;
  if (skSurface) { skSurface.delete(); skSurface = null; }
  // Reuse the GrDirectContext across resizes. Resizing the canvas does NOT lose its WebGL
  // context, and rebuilding grCtx would orphan every GPU-resident SkImage created on the old
  // context — the coverage render target (MakeRenderTarget(grCtx, …)) plus the ground/tractor/
  // imagery sprites — so those layers vanish after a resize (coverage being the obvious one).
  // Only the on-screen surface (render target sized to the canvas) needs rebuilding.
  // Non-color-managed surface (null colorSpace) so Skia blends semi-transparent fills
  // (section footprint, lightbar) in encoded sRGB space — matching the 2D canvas. The default
  // MakeWebGLCanvasSurface attaches an sRGB color space and blends in LINEAR space, which
  // lightens/washes out transparent colors. Build via the explicit GL path; fall back to the
  // color-managed helper if needed.
  if (!grCtx) {
    const ctxHandle = CK.GetWebGLContext(ckcv);
    if (ctxHandle) grCtx = CK.MakeWebGLContext(ctxHandle);
  }
  if (grCtx) skSurface = CK.MakeOnScreenGLSurface(grCtx, ckcv.width, ckcv.height, null);
  if (!skSurface) skSurface = CK.MakeWebGLCanvasSurface(ckcv);
}
// Hex (#rrggbb) or rgb(a)(...) → CanvasKit color (CK.Color: r,g,b 0-255, a 0-1).
function ckColor(s) {
  if (s[0] === '#') return CK.Color(parseInt(s.slice(1, 3), 16), parseInt(s.slice(3, 5), 16), parseInt(s.slice(5, 7), 16), 1);
  const m = s.match(/\(([^)]+)\)/)[1].split(',').map(Number);
  return CK.Color(m[0], m[1], m[2], m.length > 3 ? m[3] : 1);
}
// Day/Night theme. CSS chrome flips via [data-theme]; the Skia map (clear + grid) is
// recoloured here. Grid greys read on dark; dark-blue greys read on the light day field.
const MAP_CLEAR = { night: '#0f1115', day: '#d7dee7' };
const GRID_MINOR = { night: 'rgba(180,180,180,0.314)', day: 'rgba(80,92,110,0.33)' };
const GRID_MAJOR = { night: 'rgba(200,200,200,0.47)', day: 'rgba(60,72,90,0.5)' };
function applyMapTheme() {
  if (!CK || !SKP) return; // paints not built yet — buildSkPaints() re-applies on init
  const k = mapIsDay ? 'day' : 'night';
  SKP.gridMinor.setColor(ckColor(GRID_MINOR[k]));
  SKP.gridMajor.setColor(ckColor(GRID_MAJOR[k]));
}
function applyTheme(isDay) {
  mapIsDay = !!isDay;
  document.documentElement.setAttribute('data-theme', mapIsDay ? 'day' : 'night');
  applyMapTheme();
}
function buildSkPaints() {
  const mk = (color, w, dash) => {
    const p = new CK.Paint();
    p.setStyle(CK.PaintStyle.Stroke);
    p.setColor(ckColor(color));
    p.setStrokeWidth(w);
    p.setAntiAlias(true);
    p.setStrokeJoin(CK.StrokeJoin.Round);
    p.setStrokeCap(CK.StrokeCap.Round);
    if (dash) p.setPathEffect(CK.PathEffect.MakeDash(dash, 0));
    return p;
  };
  const fill = (color) => { const p = new CK.Paint(); p.setStyle(CK.PaintStyle.Fill); p.setColor(ckColor(color)); p.setAntiAlias(true); return p; };
  SKP = {
    // Colours mirror native SkiaMapControl paints; widths are set per frame in
    // updateLineWidths() (world metres × pxPerM, like native's world-space strokes).
    boundary: mk('rgba(242,112,89,0.8)', 5), boundaryInner: mk('rgb(245,245,77)', 5), headland: mk('rgb(251,235,107)', 4),
    track: mk('#ffd24a', 4), reference: mk('rgb(180,100,255)', 5, [9, 7]),
    guidance: mk('rgb(252,86,186)', 5), uturn: mk('rgb(77,242,77)', 5), next: mk('rgb(0,200,200)', 4),
    // Extra guidelines (adjacent passes) — native _extraGuidePaint green(51,153,50,153)
    // over a black shadow, both 0.3 m. Widths set per frame in updateLineWidths.
    extraGuide: mk('rgba(51,153,50,0.6)', 1), extraGuideShadow: mk('rgba(0,0,0,0.5)', 1),
    // Match native (night-mode) grid: grey, ~0.31/0.47 alpha, major ~2× thickness.
    gridMinor: mk('rgba(180,180,180,0.314)', 1), gridMajor: mk('rgba(200,200,200,0.47)', 2),
    axisX: mk('rgba(204,51,51,0.275)', 1.5), axisY: mk('rgba(51,204,51,0.275)', 1.5),
    vehicle: fill('#39FF6A'),
    flagFill: fill('#FF0000'), flagOutline: mk('#101010', 1.5), // colour set per flag
    // Field Builder headland-editor offset lines: yellow = contributes to the closed
    // headland loop, red = doesn't (yet). Width set per frame in drawHeadlandSegEditLinesSk.
    hlEdit: mk('rgba(245,235,90,0.95)', 3), hlEditOff: mk('rgba(232,86,74,0.95)', 3),
    // Tram lines (wheel tracks) — orange, set per frame in drawTramLinesSk.
    tram: mk('rgba(255,140,60,0.9)', 2),
    // Route planner preview — worked swaths (green), U-turns (amber), headland laps
    // (cyan), transport/approach moves (grey dashed). Widths set in updateLineWidths.
    routeSwath: mk('rgba(90,210,100,0.95)', 1), routeTurn: mk('rgba(255,176,64,0.95)', 1),
    routeHeadland: mk('rgba(90,190,235,0.95)', 1), routeApproach: mk('rgba(190,190,190,0.7)', 1, [8, 6]),
    // Field-split lines (operator-drawn region dividers) — dashed magenta.
    routeSplit: mk('rgba(235,90,205,0.95)', 2, [10, 7]),
    // Angle reference line (live pass-direction preview while dialling) — dashed yellow.
    angleRef: mk('rgba(255,225,80,0.85)', 1.5, [14, 8]),
  };
  // Section footprint bars: one stroke paint per ColorCode (butt cap so adjacent
  // sections abut without rounded overhang), matching the 2D SECTION_COLORS.
  SKP.section = SECTION_COLORS.map((c) => {
    const p = new CK.Paint();
    p.setStyle(CK.PaintStyle.Stroke);
    p.setColor(ckColor(c));
    p.setStrokeWidth(7);
    p.setStrokeCap(CK.StrokeCap.Butt);
    p.setAntiAlias(true);
    return p;
  });
  // Section footprint as native: filled 2 m-deep rects per ColorCode + a thin black
  // outline (DrawToolSk DrawRect fill + _sectionOutlinePaint).
  SKP.sectionFill = SECTION_COLORS.map((c) => fill(c));
  SKP.sectionOutline = mk('#000000', 1.2);
  SKP.sectionOutline.setStrokeCap(CK.StrokeCap.Butt);
  SKP.sectionOutline.setStrokeJoin(CK.StrokeJoin.Miter);
  // Lightbar LED fill — one reusable paint, recoloured per cell (crisp rects, no AA).
  SKP.lbFill = new CK.Paint();
  SKP.lbFill.setStyle(CK.PaintStyle.Fill);
  SKP.lbFill.setAntiAlias(false);
  // Vehicle triangle in marker-local px (drawn under canvas translate+rotate).
  skTri = CK.Path.MakeFromSVGString('M 0 -14 L 9 11 L -9 11 Z');
  applyMapTheme(); // sync grid colours if the theme arrived before paints were built
}
if (typeof CanvasKitInit === 'function') {
  CanvasKitInit({ locateFile: f => '/vendor/' + f }).then(ck => {
    CK = ck;
    buildSkPaints();
    recreateSkSurface();
    ckStatus = skSurface ? 'ready (Skia WebGL)' : 'no WebGL surface';
  }).catch(e => { ckStatus = 'load error: ' + e.message; });
} else {
  ckStatus = 'loader missing';
}

// ---- camera controls ----
addEventListener('wheel', e => {
  e.preventDefault();
  pxPerM *= e.deltaY < 0 ? 1.1 : 0.9;
  pxPerM = Math.min(200, Math.max(0.2, pxPerM));
}, { passive: false });

// Block BROWSER zoom so only the map zooms. iOS Safari IGNORES the viewport's
// user-scalable=no, so a two-finger pinch (esp. one starting over a panel, whose
// touch-action:manipulation still permits page pinch) magnifies the whole page —
// chrome and all — off-screen ("erratic zoom"). Preventing Safari's gesture* events
// stops that; trackpad/ctrl+wheel page-zoom is already covered by the wheel handler's
// preventDefault above. Multi-touch on the map canvas is covered by touch-action:none.
for (const t of ['gesturestart', 'gesturechange', 'gestureend'])
  addEventListener(t, e => e.preventDefault(), { passive: false });
// Belt-and-suspenders for touch browsers without gesture* (e.g. some Android): a
// 2+-finger move is a pinch — swallow it so the page can't zoom/pan. Single-finger
// touches pass through to the map pan / panel scroll untouched.
addEventListener('touchmove', e => { if (e.touches.length > 1) e.preventDefault(); }, { passive: false });
// iOS double-tap-to-zoom is NOT a gesture* event and slips past per-element touch-action when
// rapid taps land between/across nav buttons → it magnifies the whole page (chrome off-screen)
// and thrashes layout (render stutter / audio glitch / LINK spike are that main-thread jank).
// Swallow the 2nd tap's default within 300 ms; the UI runs on pointerdown, so button presses
// still register.
let _lastTapEnd = 0;
addEventListener('touchend', e => {
  const now = Date.now();
  if (now - _lastTapEnd <= 300) e.preventDefault();
  _lastTapEnd = now;
}, { passive: false });

// Pan + tap. A gesture that moves past TAP_SLOP px pans (and drops to Free mode); one
// that stays put is a TAP. Overlays (panels/toolbars) stopPropagation their pointerdown,
// so window-level pointerdown only fires for gestures that began on the map — gestureOnMap
// guards the pointerup tap against panel taps that bubble their pointerup.
const TAP_SLOP = 5; // px — below this, a press-release is a tap, not a pan
let dragging = false, moved = false, gestureOnMap = false, lastX = 0, lastY = 0, downX = 0, downY = 0;
addEventListener('pointerdown', e => {
  // Stage-4 edit: grab a handle if the press starts on one (else fall through to pan).
  if (editSession) {
    const hi = editHitTest(e.clientX, e.clientY);
    if (hi >= 0) { editDragIdx = hi; gestureOnMap = false; dragging = false; return; }
  }
  gestureOnMap = true; dragging = true; moved = false;
  downX = lastX = e.clientX; downY = lastY = e.clientY;
});
addEventListener('pointerup', e => {
  if (editDragIdx >= 0) { editDragIdx = -1; return; } // finished dragging an edit handle
  dragging = false;
  // A clean tap while a map-tap capture mode is armed → unproject and dispatch.
  if (gestureOnMap && !moved && mapTap) {
    const w = s2w(e.clientX, e.clientY);
    if (w) mapTap.onTap(w.e, w.n);
  }
  gestureOnMap = false;
});
addEventListener('pointermove', e => {
  // #40: the crosshair letter is a MOUSE-hover affordance — only for a real pointer. On
  // touch/pen there's no hover to ride, and a tap's micro-move would drop the "big A" under
  // the finger/button and clash with the placed-dot label; touch relies on the dot letters
  // + the "Tap Point A/B" banner instead.
  if (mapTap && (abFlow === 'straight' || abFlow === 'curve') && e.pointerType === 'mouse') updateAbCursorLabel(e.clientX, e.clientY);
  if (editDragIdx >= 0 && editSession) {       // drag the grabbed handle
    const w = s2w(e.clientX, e.clientY);
    if (w) editSession.points[editDragIdx] = editSession.kind === 'headland' ? hlSnap(w.e, w.n) : { e: w.e, n: w.n };
    return;
  }
  if (!dragging) return;
  if (!moved) {
    // Stay still until the slop is exceeded so a tap doesn't pan or drop follow mode.
    if (Math.hypot(e.clientX - downX, e.clientY - downY) <= TAP_SLOP) return;
    moved = true; cameraMode = 2; lastX = e.clientX; lastY = e.clientY; // reset origin, no jump
    return;
  }
  // Pan in the rotated frame: invert the screen rotation so the grabbed point
  // tracks the cursor. Reduces to the plain north-up pan at rotation 0.
  const dsx = e.clientX - lastX, dsy = e.clientY - lastY;
  camE += (-_cosRR * dsx + _sinRR * dsy) / pxPerM;
  camN += (_sinRR * dsx + _cosRR * dsy) / pxPerM;
  lastX = e.clientX; lastY = e.clientY;
});
// ---- map-tap capture mode (Phase MT) — the reusable on-map point-picking primitive ----
// A feature arms capture with startMapTap({hint, onTap}); the next clean tap unprojects via
// s2w and calls onTap(e,n) (field metres). One mode at a time; the map stays pan/zoomable
// (only a tap captures) so the operator can frame the view first. Esc / endMapTap() exits.
let mapTap = null;
function startMapTap(cfg) {
  mapTap = cfg;
  document.body.classList.add('maptap'); // crosshair cursor (CSS)
  const h = document.getElementById('maptap-hint');
  h.textContent = cfg.hint || 'Tap the map'; h.classList.add('show');
  // Touch cancel: only for bare flows (no draw toolbar) — flag placement. The AB/sat/headland
  // flows show the toolbar whose own Cancel handles them, so don't double it up.
  const tbUp = document.getElementById('draw-toolbar').classList.contains('show');
  document.getElementById('maptap-cancel').classList.toggle('show', !tbUp);
}
function endMapTap() {
  mapTap = null;
  pickOutlines = null; pickNamed = null; // stop drawing / hit-testing pick outlines once a pick/cancel happens
  document.body.classList.remove('maptap');
  document.getElementById('maptap-hint').classList.remove('show');
  document.getElementById('maptap-cancel').classList.remove('show'); // hide the touch-cancel pill
  if (abLabelEl) abLabelEl.classList.remove('show'); // #40 hide the A/B crosshair letter
  if (abDotLabelsEl) for (const s of abDotLabelsEl.children) s.style.display = 'none'; // + the dot letters
}
// Pick-from-map (ported from AgValoniaGPS-RoutePlanner): arm a tap that SELECTS the
// field at the tapped point inside the Fields and Jobs panel, so the operator can
// then confirm/choose the job (defaulting to the last one) before it opens. The
// field is identified client-side by point-in-polygon against the mapped outlines —
// the actual open later happens by field name (Resume/Start), so the ~10 m outline
// decimation never affects which field really opens.
let pickOutlines = null; // [[{e,n},…],…] outline rings, drawn while picking
let pickNamed = null;    // [{name, ring:[{e,n}…]},…] same rings keyed by field name (hit-test)
function pickFieldOnMap() {
  // Pull every mapped field's outline (projected into the current map plane) so the
  // paddocks are visible to tap — no satellite background required.
  fetch('/api/nearbyfields').then(r => r.json()).then(d => {
    pickNamed = (d || []).map(f => ({ name: f.name, ring: f.ring.map(p => ({ e: p[0], n: p[1] })) }));
    pickOutlines = pickNamed.map(f => f.ring);
  }).catch(() => { pickOutlines = null; pickNamed = null; });
  startMapTap({
    hint: 'Tap a field to choose its job',
    // One-shot: identify the tapped field, select it in Fields and Jobs, then disarm
    // (the shared handler doesn't auto-end — multi-point features re-tap).
    onTap: (e, n) => {
      const hit = (pickNamed || []).find(f => ptInRing(e, n, f.ring));
      endMapTap();
      if (hit) {
        fjSelField = hit.name;
        const jobs = fjJobsArr();                       // most-recent first
        fjSelJob = jobs.length ? jobs[0].taskName : null; // default to last job
      }
      openFieldsAndJobs(); // re-open the panel with the picked field + last job selected
    },
  });
}
// Ray-cast point-in-polygon over a ring of {e,n} points (current map plane, metres).
function ptInRing(e, n, ring) {
  let inside = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const xi = ring[i].e, yi = ring[i].n, xj = ring[j].e, yj = ring[j].n;
    if (((yi > n) !== (yj > n)) && (e < (xj - xi) * (n - yi) / (yj - yi) + xi)) inside = !inside;
  }
  return inside;
}
// Draw the pick-from-map field outlines (current-plane rings) while picking.
function drawPickOutlinesSk(canvas) {
  if (!pickOutlines) return;
  for (const ring of pickOutlines) if (ring.length >= 2) strokePtsSk(canvas, ring, true, SKP.boundary);
}
window.pickFieldOnMap = pickFieldOnMap;

// General aerial/satellite background toggle (reuses the Bing underlay that the
// "draw boundary on map" mode already draws — drawSatelliteSk gates on satEnabled).
function toggleSatBackground() {
  satEnabled = !satEnabled;
  const b = document.getElementById('sa-sat');
  if (b) b.classList.toggle('active', satEnabled);
}
window.toggleSatBackground = toggleSatBackground;

// ---- Terrain (recorded elevation shading) ---------------------------------
// The host samples RTK altitude into a 5 m per-field grid while driving and
// serves it at /api/elevation. While the Screen & Alerts → Map Background
// "Terrain" toggle is ON we refetch every 5 s, paint one canvas pixel per cell
// through a blue→green→yellow→red ramp (alpha 0.35), and blit it world-anchored
// UNDER the coverage/route layers (same drawImageWorldSk path as coverage).
// OFF = no fetches, nothing drawn.
let terrainOn = false, terrain = null, terrainTimer = null;
function terrainRamp(t) { // 0..1 (low..high) → blue → green → yellow → red
  const stops = [[0x30, 0x60, 0xff], [0x22, 0xb1, 0x4c], [0xff, 0xd2, 0x1f], [0xe0, 0x3a, 0x20]];
  const x = Math.max(0, Math.min(1, t)) * (stops.length - 1);
  const i = Math.min(stops.length - 2, Math.floor(x)), f = x - i;
  const a = stops[i], b = stops[i + 1];
  return [Math.round(a[0] + (b[0] - a[0]) * f),
          Math.round(a[1] + (b[1] - a[1]) * f),
          Math.round(a[2] + (b[2] - a[2]) * f)];
}
function terrainDrop() {
  if (terrain && terrain.skImg) terrain.skImg.delete();
  terrain = null;
}
// Payload: {cell, min, max, cells:[[eCentre, nCentre, alt]…]} in field-local metres.
function terrainBuild(d) {
  const cs = d.cell, cells = d.cells;
  let minCx = Infinity, maxCx = -Infinity, minCy = Infinity, maxCy = -Infinity;
  for (const c of cells) {
    const cx = Math.round((c[0] - cs / 2) / cs), cy = Math.round((c[1] - cs / 2) / cs);
    if (cx < minCx) minCx = cx; if (cx > maxCx) maxCx = cx;
    if (cy < minCy) minCy = cy; if (cy > maxCy) maxCy = cy;
  }
  const w = maxCx - minCx + 1, h = maxCy - minCy + 1;
  if (!(w > 0) || !(h > 0) || w * h > 16e6) { terrainDrop(); return; }
  const cv = document.createElement('canvas'); cv.width = w; cv.height = h;
  const ctx = cv.getContext('2d');
  const span = Math.max(0.01, d.max - d.min);
  for (const c of cells) {
    const cx = Math.round((c[0] - cs / 2) / cs), cy = Math.round((c[1] - cs / 2) / cs);
    const rgb = terrainRamp((c[2] - d.min) / span);
    ctx.fillStyle = 'rgba(' + rgb[0] + ',' + rgb[1] + ',' + rgb[2] + ',0.35)';
    ctx.fillRect(cx - minCx, h - 1 - (cy - minCy), 1, 1); // y-flip: high northing at top
  }
  terrainDrop();
  terrain = { canvas: cv, skImg: null,
    minE: minCx * cs, minN: minCy * cs, maxE: (maxCx + 1) * cs, maxN: (maxCy + 1) * cs,
    min: d.min, max: d.max };
}
function terrainLegend() {
  const el = document.getElementById('terrain-legend');
  el.classList.toggle('on', terrainOn && !!terrain);
  if (terrain) {
    document.getElementById('tl-min').textContent = terrain.min.toFixed(1) + ' m';
    document.getElementById('tl-max').textContent = terrain.max.toFixed(1) + ' m';
  }
}
function terrainFetch() {
  fetch('/api/elevation').then(r => r.json()).then(d => {
    if (!terrainOn) return; // toggled off while the fetch was in flight
    if (d && d.cells && d.cells.length) terrainBuild(d); else terrainDrop();
    terrainLegend();
  }).catch(() => {});
}
function toggleTerrain() {
  terrainOn = !terrainOn;
  const b = document.getElementById('sa-terrain');
  if (b) b.classList.toggle('active', terrainOn);
  if (terrainOn) {
    terrainFetch();
    terrainTimer = setInterval(terrainFetch, 5000);
  } else {
    if (terrainTimer) { clearInterval(terrainTimer); terrainTimer = null; }
    terrainDrop();
    terrainLegend();
  }
}
function drawTerrainSk(canvas) {
  if (!terrainOn || !terrain) return;
  if (!terrain.skImg) terrain.skImg = CK.MakeImageFromCanvasImageSource(terrain.canvas);
  if (!terrain.skImg) return;
  // Nearest keeps the 5 m cells as crisp squares (the recorded resolution).
  drawImageWorldSk(canvas, terrain.skImg, terrain.minE, terrain.minN, terrain.maxE, terrain.maxN,
    CK.FilterMode.Nearest, CK.MipmapMode.None);
}
window.toggleTerrain = toggleTerrain;

// ---- Route Planner (Layer 3) ----------------------------------------------
// The control state mirrors the headless PlanRoute(pattern, headlandPasses, skip,
// block, angleDeg) args. Plan Route posts route.plan|… then fetches /api/routeplan
// (a list of typed segment polylines) and draws them client-side — same lightweight
// pattern as pick-from-map, no binary scene-protocol layer.
let routePlan = null; // { layers:[{name, segments:[{type,pts:[{e,n}…]}…]}…], meta:{…} } or null
let rpLayerVis = {};  // layer name -> false when hidden (anything else = visible)
let rpBlocks = [];    // split blocks: [{label,'e','n',angle?}] from /api/routeblocks
let rpSelBlock = null; // selected block label (Apply path targets it)
let rpPattern = 0, rpHeadland = 0, rpSkip = 0, rpBlock = 3, rpAngle = 0, rpCornerFill = false;
// Headland style knobs (Headland tab) — the operator's choice per tool/field/job,
// remembered per device so the mower keeps "spiral out + last + back-cut".
let rpHlStyle = +(localStorage.rpHlStyle || 0) || 0;   // 0 laps, 1 spiral in, 2 spiral out, 3 none
let rpHlFirst = localStorage.rpHlOrder !== '0';        // true = laps before the interior fill
let rpBackCut = localStorage.rpBackCut === '1';        // extra opposite-hand fence lap at the end
// Cross-drill knobs: row-unit spacing (cm; second headland set offset half a row)
// and the woven W drive order (V-turns instead of two sequential coverages).
let rpRowSp = +(localStorage.rpRowSp || 0) || 0;
let rpWeave = localStorage.rpWeave === '1';
// Slope-corrected spacing from the Terrain map — on by default (no-op until
// the field has terrain data).
let rpSlope = localStorage.rpSlope !== '0';
let rpObsW = 4, rpObsL = 4, rpObsType = 'HOLE';
const RP_PAINT = { Swath: 'routeSwath', Turn: 'routeTurn', Headland: 'routeHeadland', Approach: 'routeApproach' };
function drawRoutePlanSk(canvas) {
  if (!routePlan || !routePlan.layers) return;
  for (const layer of routePlan.layers) {
    if (rpLayerVis[layer.name] === false) continue; // toggled off in the Show-paths row
    for (const seg of layer.segments) {
      if (!seg.pts || seg.pts.length < 2) continue;
      strokePtsSk(canvas, seg.pts, false, SKP[RP_PAINT[seg.type] || 'routeSwath']);
    }
  }
}
// Live pass-direction preview: while the Route Planner is open, a dashed yellow
// line through the selected block (or field centre) shows where passes will run
// for the current Angle setting — updates instantly as the stepper is pressed.
function fieldDefaultHeadingJS() {
  const b = scene && scene.boundaries && scene.boundaries[0];
  if (!b || b.length < 3) return 0;
  let best = 0, bl = -1;
  for (let i = 0; i < b.length; i++) {
    const p = b[i], q = b[(i + 1) % b.length];
    const dE = q.e - p.e, dN = q.n - p.n, l = dE * dE + dN * dN;
    if (l > bl) { bl = l; best = Math.atan2(dE, dN); }
  }
  return best;
}
function drawAngleRefSk(canvas) {
  if (!document.getElementById('routeplan').classList.contains('open')) return;
  const b = scene && scene.boundaries && scene.boundaries[0];
  if (!b || b.length < 3) return;
  let minE = 1e18, maxE = -1e18, minN = 1e18, maxN = -1e18;
  for (const p of b) { minE = Math.min(minE, p.e); maxE = Math.max(maxE, p.e); minN = Math.min(minN, p.n); maxN = Math.max(maxN, p.n); }
  const sel = rpSelBlock && rpBlocks.find(x => x.label === rpSelBlock);
  const cx = sel ? sel.e : (minE + maxE) / 2, cy = sel ? sel.n : (minN + maxN) / 2;
  const h = fieldDefaultHeadingJS() + rpAngle * Math.PI / 180;
  const L = Math.hypot(maxE - minE, maxN - minN) / 2 + 30;
  strokePtsSk(canvas, [
    { e: cx - Math.sin(h) * L, n: cy - Math.cos(h) * L },
    { e: cx + Math.sin(h) * L, n: cy + Math.cos(h) * L },
  ], false, SKP.angleRef);
}
function rpRender() {
  rpFillSpeeds();
  for (const b of document.querySelectorAll('#routeplan .rp-pat'))
    b.classList.toggle('on', +b.dataset.pat === rpPattern);
  document.getElementById('rp-hl').textContent = rpHeadland === 0 ? 'Auto' : rpHeadland;
  document.getElementById('rp-skip').textContent = rpSkip;
  document.getElementById('rp-block').textContent = rpBlock;
  document.getElementById('rp-angle').textContent = rpAngle;
  document.getElementById('rp-cornerfill').classList.toggle('on', rpCornerFill);
  // Reference-track picker: the field's hand-built tracks (planned "Route …"
  // paths excluded — planning along a plan is circular). Selection mirrors the
  // active track; changing it activates that track server-side.
  const sel = document.getElementById('rp-tracksel');
  const tl = (scene && scene.trackList) || [];
  const opts = [];
  for (const t of tl)
    if (!(t.name || '').startsWith('Route ')) opts.push(t);
  const sig = opts.map(t => t.index + ':' + t.name + (t.active ? '*' : '')).join('|');
  if (sel.dataset.sig !== sig) {
    sel.dataset.sig = sig;
    sel.innerHTML = '';
    const none = document.createElement('option');
    none.value = '-1'; none.textContent = opts.length ? '— pick a track —' : 'No tracks yet';
    sel.appendChild(none);
    for (const t of opts) {
      const o = document.createElement('option');
      o.value = t.index; o.textContent = t.name + (t.type ? ' (' + t.type + ')' : '');
      if (t.active) o.selected = true;
      sel.appendChild(o);
    }
  }
  // Headland tab: style / order / back-cut selections.
  for (const b of document.querySelectorAll('#routeplan [data-hlstyle]'))
    b.classList.toggle('on', +b.dataset.hlstyle === rpHlStyle);
  for (const b of document.querySelectorAll('#routeplan [data-hlorder]'))
    b.classList.toggle('on', (b.dataset.hlorder === '1') === rpHlFirst);
  document.getElementById('rp-backcut').classList.toggle('on', rpBackCut);
  const hlHints = [
    'Separate laps, outer to inner, then the interior fill.',
    'One continuous spiral inward — each lap closes, then a smooth lane change onto the next.',
    'Spiral driven inside-out, finishing on the fence lap — best with "Headland last" to end at the gate.',
    'No laps in this route — the headland is left for a separate pass (turn room is still kept).',
  ];
  document.getElementById('rp-hlhint').textContent =
    rpPattern === 3 ? 'The whole-field Spiral pattern has no separate headland.' : hlHints[rpHlStyle] || '';
  // Show only the controls relevant to the selected pattern (0 Auto,1 Skip,2 Cross,3 Spiral,4 Block).
  const show = (id, on) => { document.getElementById(id).style.display = on ? '' : 'none'; };
  show('rp-hl-row', rpPattern !== 3);          // spiral has no headland laps
  show('rp-hlstyle-label', rpPattern !== 3);
  show('rp-hlstyle-row', rpPattern !== 3);
  show('rp-hlorder-label', rpPattern !== 3);
  show('rp-hlorder-row', rpPattern !== 3);
  show('rp-backcut-row', rpPattern !== 3);
  show('rp-skip-row', rpPattern === 1);        // Skip
  show('rp-block-row', rpPattern === 4);       // Block
  show('rp-rowsp-row', rpPattern === 2);       // Cross: double headland offset
  show('rp-weave-row', rpPattern === 2);       // Cross: woven drive order
  document.getElementById('rp-rowsp').textContent = rpRowSp ? rpRowSp : 'Off';
  document.getElementById('rp-weave').classList.toggle('on', rpWeave);
  document.getElementById('rp-slope').classList.toggle('on', rpSlope);
  show('rp-angle-row', rpPattern !== 3);       // every pattern but spiral
  show('rp-cornerfill-row', rpPattern === 3);  // Spiral
  document.getElementById('rp-obsw').textContent = rpObsW;
  document.getElementById('rp-obsl').textContent = rpObsL;
  // Pole is a point obstacle (Width = diameter); Length doesn't apply.
  document.getElementById('rp-obsl-row').style.display = rpObsType === 'POLE' ? 'none' : '';
  for (const b of document.querySelectorAll('[data-obstype]')) b.classList.toggle('on', b.dataset.obstype === rpObsType);
  // Proximity alarm mirrors persisted config (config.display.*).
  const oaOn = !!(config && config.display && config.display.obstacleAlarmEnabled);
  const oaDist = (config && config.display && config.display.obstacleAlarmDistanceM) || 10;
  const oaBtn = document.getElementById('rp-obsalarm');
  oaBtn.classList.toggle('on', oaOn);
  oaBtn.textContent = 'Proximity alarm: ' + (oaOn ? 'on' : 'off');
  document.getElementById('rp-obsalarmdist').textContent = oaDist;
  document.getElementById('rp-obsalarm-dist-row').style.display = oaOn ? '' : 'none';
}
// Arm a map tap that drops a hard obstacle at the tapped point. A HOSE orients along the
// current vehicle heading; POLE/HOLE are axis-aligned (heading 0).
function placeObstacle() {
  startMapTap({
    hint: 'Tap to drop a ' + rpObsType.toLowerCase(),
    onTap: (e, n) => {
      const hdg = rpObsType === 'HOSE' && lastTick ? lastTick.heading : 0;
      transport.send('obstacle.place|' + e + ',' + n + ',' + rpObsW + ',' + rpObsL + ',' + hdg + ',' + rpObsType);
      endMapTap();
    },
  });
}
// Arm a map tap that deletes the obstacle (inner boundary) tapped, or the nearest one.
function deleteObstacle() {
  startMapTap({
    hint: 'Tap the obstacle to delete',
    onTap: (e, n) => { transport.send('obstacle.delete|' + e + ',' + n); endMapTap(); },
  });
}
// ---- Field splitting: operator-drawn region dividers -----------------------
// Two taps define a split line; the backend extends it to the boundary and
// (on Plan Route) works each region with its own straight-pass direction.
// Persisted per field on the backend; this mirror is refreshed from /api/routeplan.
let rpSplits = []; // [[e1,n1,e2,n2], …]
function rpSplitCountRender() {
  const el = document.getElementById('rp-splitcount');
  if (el) el.textContent = rpSplits.length ? ' (' + rpSplits.length + ')' : '';
}
function rpSplitsRefresh() {
  fetch('/api/routeplan').then(r => r.json()).then(d => {
    rpSplits = (d && d.splits) || [];
    rpSplitCountRender();
  }).catch(() => {});
  rpBlocksRefresh();
}
function drawSplitLine() {
  let first = null;
  startMapTap({
    hint: 'Split: tap the FIRST point of the divider',
    onTap: (e, n) => {
      if (!first) {
        first = [e, n];
        document.getElementById('maptap-hint').textContent = 'Split: tap the SECOND point';
        return;
      }
      transport.send('route.splitAdd|' + first[0].toFixed(2) + ',' + first[1].toFixed(2) + ',' + e.toFixed(2) + ',' + n.toFixed(2));
      endMapTap();
      setTimeout(rpSplitsRefresh, 300);
    },
  });
}
function clearSplitLines() {
  transport.send('route.splitClear');
  rpSplits = []; rpBlocks = []; rpSelBlock = null;
  rpSplitCountRender(); rpBlocksRender();
  setTimeout(rpBlocksRefresh, 400); // re-sync once the backend has cleared
}
// ---- Named blocks (A, B, C…) ----------------------------------------------
// Splits carve the field into labelled blocks. The panel lists them as buttons
// (tap to select — the label highlights on the map), "Apply path" plans ONLY
// the selected block with the current Angle (0 = auto), stored per block so a
// full re-plan keeps each block's chosen direction. Each block's path is a
// layer ("A", "B", …) that replans without touching the others.
function rpBlocksRefresh() {
  fetch('/api/routeblocks').then(r => r.json()).then(d => {
    rpBlocks = (d && d.blocks) || [];
    if (rpSelBlock && !rpBlocks.some(b => b.label === rpSelBlock)) rpSelBlock = null;
    rpBlocksRender();
  }).catch(() => {});
}
function rpBlocksRender() {
  const row = document.getElementById('rp-blocks');
  if (!row) return;
  row.innerHTML = '';
  const show = rpBlocks.length > 0;
  document.getElementById('rp-blocks-label').style.display = show ? '' : 'none';
  row.style.display = show ? '' : 'none';
  document.getElementById('rp-applyblock-row').style.display = show ? '' : 'none';
  row.style.gridTemplateColumns = 'repeat(' + Math.min(Math.max(rpBlocks.length, 1), 6) + ',1fr)';
  for (const b of rpBlocks) {
    const btn = document.createElement('button');
    btn.className = 'rp-pat' + (rpSelBlock === b.label ? ' on' : '');
    btn.textContent = b.label + (b.angle != null ? ' ' + b.angle + '°' : '');
    btn.title = b.angle != null ? ('Block ' + b.label + ' — manual angle ' + b.angle + '°') : ('Block ' + b.label + ' — auto heading');
    btn.addEventListener('pointerdown', e => {
      e.stopPropagation();
      rpSelBlock = rpSelBlock === b.label ? null : b.label;
      rpBlocksRender();
    });
    row.appendChild(btn);
  }
  const ab = document.getElementById('rp-applyblock');
  ab.textContent = rpSelBlock ? ('Apply path to block ' + rpSelBlock) : 'Select a block above';
  ab.disabled = !rpSelBlock;
}
function applyBlockPath() {
  if (!rpSelBlock) return;
  transport.send('route.planBlock|' + rpSelBlock + ',' + rpHeadland + ',' + rpAngle);
  document.getElementById('rp-stats').textContent = 'Planning block ' + rpSelBlock + '…';
  // The layer swaps in place on the backend; refresh the preview a few times so
  // the result lands even on a slow (obstacle-heavy) plan.
  for (const ms of [400, 1200, 3000]) setTimeout(refreshRoutePlan, ms);
  setTimeout(rpBlocksRefresh, 500); // stored angle badge
}
// Fetch /api/routeplan once and adopt it when it carries layers. Returns the
// promise so callers can chain; planRoute polls this until layers appear.
function applyPlanPayload(d) {
  if (!d || !d.layers || !d.layers.length) return false;
  routePlan = {
    layers: d.layers.map(L => ({
      name: L.name,
      segments: L.segments.map(s => ({ type: s.type, ch: s.ch || 0, pts: s.pts.map(p => ({ e: p[0], n: p[1] })) })),
    })),
    meta: d.meta,
  };
  rpSplits = d.splits || []; rpSplitCountRender();
  rpLayersRender();
  const m = d.meta || {};
  const km = ((m.distanceM || 0) / 1000).toFixed(2);
  document.getElementById('rp-stats').textContent =
    `${m.swaths || 0} passes · ${m.turns || 0} turns · ${km} km · ${(m.toolWidthM || 0).toFixed(2)} m tool`;
  return true;
}
function refreshRoutePlan() {
  return fetch('/api/routeplan').then(r => r.json()).then(applyPlanPayload).catch(() => false);
}
// Show-paths row: one toggle per layer so only the path being driven is shown
// (e.g. hide block B while finishing the headland, then swap).
function rpLayersRender() {
  const row = document.getElementById('rp-layers');
  if (!row) return;
  row.innerHTML = '';
  const layers = (routePlan && routePlan.layers) || [];
  document.getElementById('rp-layers-label').style.display = layers.length ? '' : 'none';
  row.style.display = layers.length ? '' : 'none';
  row.style.gridTemplateColumns = 'repeat(' + Math.min(Math.max(layers.length, 1), 6) + ',1fr)';
  for (const L of layers) {
    const btn = document.createElement('button');
    const vis = rpLayerVis[L.name] !== false;
    btn.className = 'rp-pat' + (vis ? ' on' : '');
    btn.textContent = L.name === 'Headland' ? 'Hdl' : L.name;
    btn.title = (vis ? 'Hide' : 'Show') + ' the ' + L.name + ' path';
    btn.addEventListener('pointerdown', e => {
      e.stopPropagation();
      rpLayerVis[L.name] = vis ? false : true;
      rpLayersRender();
    });
    row.appendChild(btn);
  }
}
function drawRouteSplitsSk(canvas) {
  if (!rpSplits.length) return;
  for (const s of rpSplits)
    strokePtsSk(canvas, [{ e: s[0], n: s[1] }, { e: s[2], n: s[3] }], false, SKP.routeSplit);
}
// Block letters (A, B, …) anchored at each block's centroid — DOM spans like the
// AB dot labels (CanvasKit has no text). Selected block glows yellow.
const blockLabelsEl = document.getElementById('block-labels');
function renderBlockLabels() {
  if (!blockLabelsEl || !perspM) return; // matrix only exists inside the render path
  const n = scene && scene.fieldName && scene.fieldName !== 'No Field' ? rpBlocks.length : 0;
  while (blockLabelsEl.children.length < n) blockLabelsEl.appendChild(document.createElement('span'));
  for (let i = 0; i < blockLabelsEl.children.length; i++) {
    const span = blockLabelsEl.children[i];
    if (i >= n || pw(rpBlocks[i].e, rpBlocks[i].n) < 1.0) { span.style.display = 'none'; continue; }
    const b = rpBlocks[i];
    const xy = w2s(b.e, b.n);
    span.textContent = b.label;
    span.style.left = xy[0] + 'px';
    span.style.top = xy[1] + 'px';
    span.style.color = rpSelBlock === b.label ? '#ffe14d' : '#ffffff';
    span.style.display = '';
  }
}
function openRoutePlanner() {
  lnOpen('routeplan', 'ln-fieldtools', rpRender);
  rpSplitsRefresh();
  if (!routePlan) refreshRoutePlan(); // restore the preview after a page reload
  else rpLayersRender();
}
function openObstacles() { lnOpen('obstacles', 'ln-fieldtools', rpRender); }
// Live route ETA: EMA of ACTUAL speed while working (any section on) vs turning/
// transit, seeded from the profile speed model; x remaining plan distance (scaled
// by the area fraction left) + per-turn overhead. Learned live, so the estimate
// tightens as the job progresses.
let emaWorkMps = 0, emaTurnMps = 0;
function etaLearnTick() {
  if (!tick || !lastTick) return;
  const v = lastTick.speed || 0; if (v < 0.3) return;
  const secs = tick.sections; let working = false;
  if (secs) for (let i = 0; i < secs.length; i++) { const c = secs[i]; if (c === 1 || c === 2 || c === 4) { working = true; break; } }
  const a = 0.02;
  if (working) emaWorkMps = emaWorkMps ? emaWorkMps + (v - emaWorkMps) * a : v;
  else emaTurnMps = emaTurnMps ? emaTurnMps + (v - emaTurnMps) * a : v;
}
function routeEtaText() {
  if (!routePlan || !routePlan.meta || !routePlan.meta.workM) return '';
  const m = routePlan.meta;
  const workable = workableAreaSqM(); const worked = (statusBar && statusBar.workedAreaSqM) || 0;
  const leftFrac = workable > 0 ? Math.max(0, Math.min(1, (workable - worked) / workable)) : 1;
  const wSpd = emaWorkMps || (cfgGet('uturn.routeWorkSpeedKmh') || 8) / 3.6;
  const tSpd = emaTurnMps || (cfgGet('uturn.routeTurnSpeedKmh') || 6) / 3.6;
  const ovh = cfgGet('uturn.routeTurnOverheadSec') != null ? cfgGet('uturn.routeTurnOverheadSec') : 4;
  const sec = m.workM * leftFrac / Math.max(0.3, wSpd) + m.turnM * leftFrac / Math.max(0.3, tSpd) + (m.turns || 0) * leftFrac * ovh;
  const min = Math.round(sec / 60);
  const t = min >= 90 ? '~' + (min / 60).toFixed(1) + ' h left' : '~' + min + ' min left';
  return t + (emaWorkMps ? '  (' + (emaWorkMps * 3.6).toFixed(1) + '/' + (emaTurnMps * 3.6 || 0).toFixed(1) + ' km/h)' : '');
}
let rpBlocksField = null; // refetch blocks when the open field changes
setInterval(() => {
  etaLearnTick();
  const el = document.getElementById('rp-eta');
  if (el) el.textContent = routeEtaText();
  const f = scene && scene.fieldName;
  if (f !== rpBlocksField) { rpBlocksField = f; rpSelBlock = null; rpBlocksRefresh(); }
}, 2000);
// Missed-spots overlay: red tint under the coverage layer inside the field.
let showMissed = false;
document.getElementById('rp-missed').addEventListener('pointerdown', e => {
  e.stopPropagation(); showMissed = !showMissed;
  e.currentTarget.classList.toggle('active', showMissed);
});
function drawMissedSk(canvas) {
  if (!showMissed || !scene || !scene.boundaries || !scene.boundaries.length) return;
  const cmds = [];
  for (let bi = 0; bi < scene.boundaries.length; bi++) {
    const ring = scene.boundaries[bi]; if (!ring || ring.length < 3) continue;
    for (let i = 0; i < ring.length; i++) {
      const s = w2s(ring[i].e, ring[i].n);
      cmds.push(i === 0 ? CK.MOVE_VERB : CK.LINE_VERB, s[0], s[1]);
    }
    cmds.push(CK.CLOSE_VERB);
  }
  const path = CK.Path.MakeFromCmds(cmds);
  if (!path) return;
  path.setFillType(CK.FillType.EvenOdd);   // inner rings become holes (not missed area)
  const p = new CK.Paint(); p.setAntiAlias(true); p.setStyle(CK.PaintStyle.Fill);
  p.setColor(CK.Color(200, 40, 40, 0.38));
  canvas.drawPath(path, p);
  path.delete(); p.delete();
}
// Route speed model (per-machine, persists with Save Profile): work/turn km/h + s/turn.
for (const [id, key] of [['rp-wspd','uturn.routeWorkSpeedKmh'],['rp-tspd','uturn.routeTurnSpeedKmh'],['rp-tovh','uturn.routeTurnOverheadSec'],['rp-edgeoff','uturn.routeFirstPassOffsetM']]) {
  const el = document.getElementById(id);
  el.addEventListener('change', () => { const v = parseFloat(el.value); if (Number.isFinite(v)) cfgSend(key, v); });
}
function rpFillSpeeds() {
  for (const [id, key] of [['rp-wspd','uturn.routeWorkSpeedKmh'],['rp-tspd','uturn.routeTurnSpeedKmh'],['rp-tovh','uturn.routeTurnOverheadSec'],['rp-edgeoff','uturn.routeFirstPassOffsetM']]) {
    const el = document.getElementById(id);
    if (document.activeElement === el) continue;
    const v = cfgGet(key);
    if (typeof v === 'number') el.value = Math.round(v * 10) / 10;
  }
}
// ── Touch keypad ────────────────────────────────────────────────────────────
// Numeric entry for a cab tablet with no keyboard. prompt() was unusable there:
// it is modal, needs a hardware keyboard, and blocks every other control until
// dismissed — so one stray tap could strand the whole UI.
// askKeypad({...}, cb) calls cb({value, unit, text}) on OK, or cb(null) on cancel.
let _kpCb = null, _kpDigits = '', _kpUnit = '';
function _kpPaint() {
  document.getElementById('kp-value').textContent = _kpDigits === '' ? '0' : _kpDigits;
  document.getElementById('kp-unitlbl').textContent = _kpUnit || '';
}
function askKeypad(opts, cb) {
  opts = opts || {};
  _kpCb = cb || null;
  _kpDigits = (opts.value != null && opts.value !== 0) ? String(opts.value) : '';
  _kpUnit = opts.unit || (opts.units && opts.units[0]) || '';
  document.getElementById('kp-title').textContent = opts.title || 'Enter value';
  document.getElementById('kp-numlabel').textContent = opts.numLabel || 'Amount';
  const trow = document.getElementById('kp-textrow'), tinput = document.getElementById('kp-text');
  if (opts.textLabel) {
    trow.classList.add('on');
    document.getElementById('kp-textlabel').textContent = opts.textLabel;
    tinput.value = opts.text || '';
  } else {
    trow.classList.remove('on');
    tinput.value = '';
  }
  const uwrap = document.getElementById('kp-units');
  uwrap.innerHTML = '';
  for (const u of (opts.units || [])) {
    const b = document.createElement('button');
    b.className = 'kp-unit' + (u === _kpUnit ? ' sel' : '');
    b.textContent = u;
    b.addEventListener('pointerdown', ev => {
      ev.stopPropagation();
      _kpUnit = u;
      for (const s of uwrap.children) s.classList.toggle('sel', s === b);
      _kpPaint();
    });
    uwrap.appendChild(b);
  }
  _kpPaint();
  openDialog('dlg-keypad');
}
for (const k of document.querySelectorAll('#dlg-keypad .kp-key')) {
  k.addEventListener('pointerdown', e => {
    e.stopPropagation();
    const v = k.dataset.k;
    if (v === 'clear') _kpDigits = '';
    else if (v === 'back') _kpDigits = _kpDigits.slice(0, -1);
    else if (v === '.') { if (!_kpDigits.includes('.')) _kpDigits = (_kpDigits || '0') + '.'; }
    else if (_kpDigits.length < 12) _kpDigits += v;
    _kpPaint();
  });
}
document.getElementById('kp-cancel').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const cb = _kpCb; _kpCb = null; closeDialog();
  if (cb) cb(null);
});
document.getElementById('kp-ok').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const cb = _kpCb; _kpCb = null;
  const out = {
    value: parseFloat(_kpDigits) || 0,
    unit: _kpUnit || '',
    text: document.getElementById('kp-text').value || ''
  };
  closeDialog();
  if (cb) cb(out);
});
// Application records: set the active job's product/rate; export coverage.geojson.
const _sanitise = s => String(s || '').replace(/[|,]/g, ' ').trim();
document.getElementById('ft-setproduct').addEventListener('pointerdown', e => {
  e.stopPropagation();
  askKeypad({
    title: 'Job Product',
    textLabel: 'Product name',
    numLabel: 'Rate per hectare (0 if n/a)',
    units: ['kg/ha', 'L/ha', 'seeds/ha', 't/ha']
  }, r => {
    if (!r) return;
    transport.send('job.setProduct|' + _sanitise(r.text) + ',' + r.value +
                   ',' + (r.value > 0 ? _sanitise(r.unit) : ''));
  });
});
document.getElementById('ft-setapplied').addEventListener('pointerdown', e => {
  e.stopPropagation();
  askKeypad({
    title: 'Applied Total',
    numLabel: 'Total applied this job (0 to clear)',
    units: ['kg', 'L', 't']
  }, r => {
    if (!r) return;
    transport.send('job.setApplied|' + r.value + ',' + (r.value > 0 ? _sanitise(r.unit) : ''));
  });
});
document.getElementById('ft-exportcov').addEventListener('pointerdown', e => {
  e.stopPropagation(); transport.send('job.exportCoverage');
});
// ---- Tank Mix calculator --------------------------------------------------
// All arithmetic client-side and shown WITH its working (rate × area = total)
// so amounts can be sanity-checked at the induction hopper. The mix persists
// with the JOB (mix.save); the chemical list + remembered rates app-wide
// (mix.catalog). Rate bases cover per-hectare and per-100L-water products.
const TM_BASES = ['L/ha', 'mL/ha', 'kg/ha', 'g/ha', 'mL/100L', 'L/100L'];
let tmState = { areaHa: 0, carrier: 150, tank: 0, buffer: 0, chems: [] };
let tmCatalog = [];      // [{name, rate, basis}]
let tmSaveTimer = null;
let showRefills = false;

function tmFieldHa() { return workableAreaSqM() / 10000; }
function tmLeftHa() {
  const worked = (statusBar && statusBar.workedAreaSqM) || 0;
  return Math.max(0, (workableAreaSqM() - worked) / 10000);
}
function tmSave() {
  clearTimeout(tmSaveTimer);
  tmSaveTimer = setTimeout(() => transport.send('mix.save|' + JSON.stringify(tmState)), 800);
}
function tmSaveCatalog() { transport.send('mix.catalog|' + JSON.stringify(tmCatalog)); }
function tmUpsertCatalog(chem) {
  const i = tmCatalog.findIndex(c => c.name === chem.name);
  if (i >= 0) tmCatalog[i] = { name: chem.name, rate: chem.rate, basis: chem.basis };
  else tmCatalog.push({ name: chem.name, rate: chem.rate, basis: chem.basis });
  tmSaveCatalog();
}
// Amount of one chemical for a given WATER volume (L) and AREA (ha), in its
// native unit. Per-ha bases ignore water; per-100L bases ignore area.
function tmAmount(chem, waterL, areaHa) {
  switch (chem.basis) {
    case 'L/ha': case 'mL/ha': case 'kg/ha': case 'g/ha': return chem.rate * areaHa;
    case 'mL/100L': case 'L/100L': return chem.rate * waterL / 100;
    default: return 0;
  }
}
function tmFmt(amount, basis) {
  // Display in the sensible big unit: mL → L past 1000, g → kg past 1000.
  const small = basis.startsWith('mL') ? 'mL' : basis.startsWith('g') ? 'g' : null;
  if (small && amount >= 1000)
    return (amount / 1000).toFixed(amount >= 10000 ? 0 : 2) + (small === 'mL' ? ' L' : ' kg');
  const big = basis.startsWith('L') ? ' L' : basis.startsWith('kg') ? ' kg' : ' ' + small;
  return amount.toFixed(amount >= 100 ? 0 : amount >= 10 ? 1 : 2) + big;
}
function tmRenderChems() {
  const host = document.getElementById('tm-chems');
  host.innerHTML = '';
  tmState.chems.forEach((c, i) => {
    const row = document.createElement('div');
    row.className = 'rp-row';
    const name = document.createElement('span');
    name.className = 'cfg-label'; name.style.flex = '1'; name.textContent = c.name;
    const rate = document.createElement('input');
    rate.className = 'cfg-num'; rate.type = 'number'; rate.step = '0.1'; rate.min = '0';
    rate.style.width = '58px'; rate.value = c.rate;
    rate.addEventListener('change', () => {
      const v = parseFloat(rate.value);
      if (Number.isFinite(v) && v >= 0) { c.rate = v; tmUpsertCatalog(c); tmSave(); tmRenderOut(); }
    });
    const basis = document.createElement('select');
    basis.className = 'cfg-sel'; basis.style.width = '86px';
    for (const b of TM_BASES) {
      const o = document.createElement('option');
      o.value = b; o.textContent = b; if (b === c.basis) o.selected = true;
      basis.appendChild(o);
    }
    basis.addEventListener('change', () => { c.basis = basis.value; tmUpsertCatalog(c); tmSave(); tmRenderOut(); });
    const del = document.createElement('button');
    del.className = 'cfg-act fj-danger'; del.textContent = '✕'; del.title = 'Remove from this mix';
    del.addEventListener('pointerdown', e => {
      e.stopPropagation(); tmState.chems.splice(i, 1); tmSave(); tmRenderChems(); tmRenderOut();
    });
    row.append(name, rate, basis, del);
    host.appendChild(row);
  });
  // Catalogue chips: one tap adds a known chemical at its remembered rate.
  const chips = document.getElementById('tm-catchips');
  chips.innerHTML = '';
  const unused = tmCatalog.filter(c => !tmState.chems.some(m => m.name === c.name));
  chips.style.display = unused.length ? '' : 'none';
  for (const c of unused) {
    const b = document.createElement('button');
    b.className = 'rp-pat';
    b.textContent = c.name;
    b.title = c.rate + ' ' + c.basis + ' — tap to add to the mix';
    b.addEventListener('pointerdown', e => {
      e.stopPropagation();
      tmState.chems.push({ name: c.name, rate: c.rate, basis: c.basis });
      tmSave(); tmRenderChems(); tmRenderOut();
    });
    chips.appendChild(b);
  }
}
function tmRenderOut() {
  const el = document.getElementById('tm-output');
  const A = tmState.areaHa, C = tmState.carrier, T = tmState.tank, B = tmState.buffer;
  if (!(A > 0) || !(C > 0)) { el.textContent = 'Set an area and a carrier rate.'; return; }
  const water = A * C + B;
  const lines = [];
  lines.push(A.toFixed(1) + ' ha × ' + C + ' L/ha' + (B > 0 ? ' + ' + B + ' L buffer' : '')
    + ' = ' + Math.round(water) + ' L water');
  let full = 0, rem = water;
  if (T > 0) {
    full = Math.floor(water / T);
    rem = water - full * T;
    if (rem < 1) { rem = 0; }
    const haTank = T / C;
    const fills = full + (rem > 0 ? 1 : 0);
    lines.push(fills + (fills === 1 ? ' fill' : ' fills') + ' from a ' + T + ' L tank ('
      + haTank.toFixed(1) + ' ha per full tank)');
  }
  for (const c of tmState.chems) {
    lines.push('');
    lines.push(c.name + '  —  ' + c.rate + ' ' + c.basis);
    lines.push('  total: ' + tmFmt(tmAmount(c, water, A), c.basis));
    if (T > 0 && full > 0) {
      const perFullWater = T;
      const perFullArea = T / C;
      lines.push('  per full tank: ' + tmFmt(tmAmount(c, perFullWater, perFullArea), c.basis));
      if (rem > 0)
        lines.push('  last fill (' + Math.round(rem) + ' L): ' + tmFmt(tmAmount(c, rem, rem / C), c.basis));
    }
  }
  if (B > 0 && tmState.chems.some(c => c.basis.endsWith('/ha')))
    lines.push('\nBuffer water carries only the per-100L chemicals — per-ha amounts stay matched to the area.');
  el.textContent = lines.join('\n');
}
function tmRender() {
  document.getElementById('tm-area').value = tmState.areaHa ? tmState.areaHa.toFixed(1) : '';
  document.getElementById('tm-carrier').value = tmState.carrier;
  document.getElementById('tm-tank').value = tmState.tank || '';
  document.getElementById('tm-buffer').value = tmState.buffer || '';
  document.getElementById('tm-refills').classList.toggle('on', showRefills);
  tmRenderChems();
  tmRenderOut();
}
function openTankMix() {
  fetch('/api/tankmix').then(r => r.json()).then(d => {
    tmCatalog = Array.isArray(d.catalog) ? d.catalog : [];
    if (d.mix && typeof d.mix === 'object') tmState = Object.assign(tmState, d.mix);
    if (!(tmState.tank > 0)) {
      // Default the tank from Rate Control's product tank size when available.
      fetch('/api/ratecontrol').then(r => r.json()).then(rc => {
        const p = rc && rc.products && rc.products[0];
        if (p && p.tankSize > 0 && !(tmState.tank > 0)) { tmState.tank = p.tankSize; tmRender(); }
      }).catch(() => {});
    }
    if (!(tmState.areaHa > 0)) tmState.areaHa = tmFieldHa();
    tmRender();
  }).catch(() => tmRender());
}
document.getElementById('ft-tankmix').addEventListener('pointerdown', e => {
  e.stopPropagation(); lnOpen('tankmix', 'ln-fieldtools', openTankMix);
});
document.getElementById('tm-area-field').addEventListener('pointerdown', e => {
  e.stopPropagation(); tmState.areaHa = tmFieldHa(); tmSave(); tmRender();
});
document.getElementById('tm-area-left').addEventListener('pointerdown', e => {
  e.stopPropagation(); tmState.areaHa = tmLeftHa(); tmSave(); tmRender();
});
for (const [id, key] of [['tm-area', 'areaHa'], ['tm-carrier', 'carrier'], ['tm-tank', 'tank'], ['tm-buffer', 'buffer']]) {
  document.getElementById(id).addEventListener('change', e => {
    const v = parseFloat(e.target.value);
    if (Number.isFinite(v) && v >= 0) { tmState[key] = v; tmSave(); tmRenderOut(); }
  });
}
document.getElementById('tm-addchem').addEventListener('pointerdown', e => {
  e.stopPropagation();
  askKeypad({
    title: 'Add chemical',
    textLabel: 'Chemical name',
    numLabel: 'Rate',
    units: TM_BASES,
  }, r => {
    if (!r || !r.text || !(r.value > 0)) return;
    const chem = { name: _sanitise(r.text), rate: r.value, basis: r.unit || 'L/ha' };
    tmState.chems.push(chem);
    tmUpsertCatalog(chem);
    tmSave(); tmRenderChems(); tmRenderOut();
  });
});
document.getElementById('tm-clear').addEventListener('pointerdown', e => {
  e.stopPropagation(); tmState.chems = []; tmSave(); tmRenderChems(); tmRenderOut();
});
document.getElementById('tm-refills').addEventListener('pointerdown', e => {
  e.stopPropagation(); showRefills = !showRefills; tmRender();
});
// Refill points on the planned route: walk the working segments in drive order
// accumulating sprayed volume (length × tool width × carrier); every time it
// crosses a full tank, mark the spot. The map shows where each fill runs dry.
let _tmRefillPaint = null, _tmRefillRing = null;
function drawRefillSk(canvas) {
  if (!showRefills || !routePlan || !routePlan.layers || !routePlan.meta) return;
  const C = tmState.carrier, T = tmState.tank, w = routePlan.meta.toolWidthM;
  if (!(C > 0 && T > 0 && w > 0)) return;
  const areaPerTankM2 = (T / C) * 10000;
  let acc = 0, next = areaPerTankM2;
  const marks = [];
  for (const layer of routePlan.layers) {
    for (const seg of layer.segments) {
      if (seg.type !== 'Swath' && seg.type !== 'Headland') continue;
      const pts = seg.pts;
      for (let i = 1; i < pts.length; i++) {
        const dx = pts[i].e - pts[i - 1].e, dy = pts[i].n - pts[i - 1].n;
        const len = Math.sqrt(dx * dx + dy * dy);
        if (len < 1e-9) continue;
        let segArea = len * w;
        while (acc + segArea >= next) {
          const t = (next - acc) / (len * w);
          marks.push({ e: pts[i - 1].e + dx * t, n: pts[i - 1].n + dy * t });
          next += areaPerTankM2;
        }
        acc += segArea;
      }
    }
  }
  if (!marks.length) return;
  if (!_tmRefillPaint) {
    _tmRefillPaint = new CK.Paint(); _tmRefillPaint.setAntiAlias(true);
    _tmRefillPaint.setStyle(CK.PaintStyle.Fill); _tmRefillPaint.setColor(CK.Color(255, 194, 75, 0.9));
    _tmRefillRing = new CK.Paint(); _tmRefillRing.setAntiAlias(true);
    _tmRefillRing.setStyle(CK.PaintStyle.Stroke); _tmRefillRing.setStrokeWidth(0.8);
    _tmRefillRing.setColor(CK.Color(40, 30, 5, 0.9));
  }
  for (const m of marks) {
    canvas.drawCircle(m.e, m.n, 2.5, _tmRefillPaint);
    canvas.drawCircle(m.e, m.n, 2.5, _tmRefillRing);
  }
}
function planRoute() {
  transport.send('route.plan|' + [rpPattern, rpHeadland, rpSkip, rpBlock, rpAngle, rpCornerFill ? 1 : 0,
    rpHlStyle, rpHlFirst ? 1 : 0, rpBackCut ? 1 : 0, rpRowSp, rpWeave ? 1 : 0, rpSlope ? 1 : 0].join(','));
  document.getElementById('rp-stats').textContent = 'Planning…';
  // The command runs on the backend dispatcher and can take a while on big fields
  // (obstacle-aware Dubins turns). The backend clears the plan first, so poll
  // until layers appear or give up.
  let tries = 0;
  const poll = () => {
    tries++;
    refreshRoutePlan().then(ok => {
      if (ok) { rpBlocksRefresh(); return; }
      if (tries < 28) { setTimeout(poll, 250); return; }   // up to ~7 s
      routePlan = null;
      rpLayersRender();
      document.getElementById('rp-stats').textContent = 'No route (open a field with a boundary).';
    });
  };
  setTimeout(poll, 250);
}
function clearRoute() {
  transport.send('route.clear');
  routePlan = null;
  rpLayersRender();
  document.getElementById('rp-stats').textContent = 'No route planned.';
}

// True when the user is typing into a field — global hotkeys (tilt, sim drive) must not
// fire then (e.g. typing "300" into the boundary offset shouldn't toggle 3D tilt on "3").
function isTyping() {
  const el = document.activeElement;
  return !!el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.isContentEditable);
}
addEventListener('keydown', e => {
  if (e.key === 'Escape' && satBnd) { satCancel(); return; }  // cancel boundary draw-on-map
  if (e.key === 'Escape' && abFlow) { abCancel(); return; }   // cancel AB-line creation (tells host)
  if (e.key === 'Escape' && hlFlow) { endHeadlandDraw(); return; } // cancel headland draw
  if (e.key === 'Escape' && editSession) { endEdit(); return; }    // cancel on-map edit
  if (e.key === 'Escape' && mapTap) { endMapTap(); return; }  // cancel on-map capture
  if (isTyping()) return;
  if (e.key === 'f' || e.key === 'F') cameraMode = 3; // resume map-follow
  // 3D tilt: 3 toggles between top-down and 60°, [ / ] nudge the pitch.
  else if (e.key === '3') pitch = pitch > 0.001 ? 0 : DEFAULT_PITCH;
  else if (e.key === '[') pitch = Math.max(0, pitch - PITCH_STEP);
  else if (e.key === ']') pitch = Math.min(MAX_PITCH, pitch + PITCH_STEP);
});

// ---- sim drive keys (client→host commands) ----
// Arrow keys / space for desktop; the simulator bar (#simbar, below) covers touch.
// The host maps these ids to the matching VM command on its UI thread (allowlist).
const KEY_CMD = {
  ArrowLeft: 'sim.steerLeft', ArrowRight: 'sim.steerRight',
  ArrowUp: 'sim.speedUp', ArrowDown: 'sim.speedDown', ' ': 'sim.stop',
};
addEventListener('keydown', e => {
  if (isTyping()) return;
  const cmd = KEY_CMD[e.key];
  if (cmd) { e.preventDefault(); transport.send(cmd); }
});
// ---- simulator bar (Phase 6) — mirrors the native SimulatorPanel ----
// Sim is Tier-1 (hardware-safe), so the bar's commands aren't gated by control
// authority — anyone driving the sim is fine. Panel *visibility* is client-local
// (the host's IsSimulatorPanelVisible is a native-only concept); enabled/speed/
// steer are host state, mirrored back over the Status frame.
const SIM = {
  bar: document.getElementById('simbar'), launch: document.getElementById('sim-launch'),
  enable: document.getElementById('sim-enable'), tenx: document.getElementById('sim-10x'),
  steer: document.getElementById('sim-steer'), steerVal: document.getElementById('sim-steerval'),
  speedVal: document.getElementById('sim-speedval'), gps: document.getElementById('sim-gps'),
};
let _steerDragging = false;     // suppress state→slider sync while the user drags
// Sim-bar visibility is host-driven (PersistentAppState.SimulatorPanelVisible) so the
// choice persists across app restarts. Shown until the first Status frame arrives.
function applySimBarVisible() {
  const open = !statusBar || statusBar.simPanelVisible !== false;
  SIM.bar.classList.toggle('open', open);
  SIM.launch.classList.toggle('show', !open);
}
applySimBarVisible();
SIM.bar.addEventListener('pointerdown', e => e.stopPropagation()); // don't pan the map
SIM.launch.addEventListener('pointerdown', e => e.stopPropagation());
// Generic argless commands (data-cmd) — fire on pointerdown for snappy feel.
for (const b of document.querySelectorAll('#simbar button[data-cmd]')) {
  b.addEventListener('pointerdown', e => {
    e.preventDefault(); e.stopPropagation();
    transport.send(b.dataset.cmd);
  });
}
// Steer slider → command-with-arg; optimistic local readout while dragging.
SIM.steer.addEventListener('input', () => {
  _steerDragging = true;
  const deg = parseFloat(SIM.steer.value);
  SIM.steerVal.textContent = deg.toFixed(1) + '°';
  transport.send('sim.setSteer|' + deg);
});
SIM.steer.addEventListener('change', () => { _steerDragging = false; });
// GPS button opens the SimCoords dialog (client-local open; OK sends sim.setCoords).
SIM.gps.addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation();
  if (statusBar && statusBar.simEnabled) return; // native guard: disable sim first
  openSimCoords();
});
document.getElementById('sim-close').addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation(); transport.send('sim.togglePanel');
});
SIM.launch.addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation(); transport.send('sim.togglePanel');
});

// ---- dialog host (Phase 6) — the web mirror of DialogOverlayHost ----
// One modal at a time over a dimming backdrop. SimCoords is the first card; later
// phases add more cards into the same host and reuse openDialog/closeDialog.
const dialogHost = document.getElementById('dialoghost');
let _confirmCb = null;
function openDialog(cardId) {
  for (const c of dialogHost.querySelectorAll('.dlg-card')) c.classList.toggle('open', c.id === cardId);
  dialogHost.classList.add('open');
}
function closeDialog() {
  hideKeyboard();
  // _kpCb too: a backdrop light-dismiss must drop the keypad callback, or a
  // later OK could fire the previous caller's handler.
  dialogHost.classList.remove('open'); _confirmCb = null; _kpCb = null;
}
// Lower the Android soft keyboard via the native bridge (window.agnative, wired by the Android
// head to InputMethodManager). No-op on iOS/desktop, where blur() already dismisses.
function nativeKeyboardHide() {
  try { if (window.agnative && window.agnative.hideKeyboard) window.agnative.hideKeyboard(); } catch (_) {}
}
function hideKeyboard() {
  const a = document.activeElement;
  if (a && typeof a.blur === 'function') a.blur(); // iOS WKWebView + desktop dismiss on blur
  nativeKeyboardHide();                            // Android: only InputMethodManager drops it
}
// Global safety net: on Android WebView a field's blur() never lowers the IME, and only the
// GPS dialog routes through closeDialog()/hideKeyboard. So whenever focus leaves ANY text field
// and doesn't land on another one — closing a config/tool dialog, tapping a button, tapping the
// map — drop the keyboard. The next-tick activeElement check (rather than relatedTarget, which
// Android sets unreliably) keeps the keyboard up during field-to-field navigation.
document.addEventListener('focusout', e => {
  if (!(e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement)) return;
  setTimeout(() => {
    const a = document.activeElement;
    const stillEditing = a && (a.tagName === 'INPUT' || a.tagName === 'TEXTAREA' || a.isContentEditable);
    if (!stillEditing) nativeKeyboardHide();
  }, 0);
});
// Shared confirm (unified nav model) — replaces browser confirm() + the native
// ShowConfirmationDialog. Transparent light-dismiss scrim (backdrop tap / Cancel = no
// action); Confirm runs the callback. Hosted in the dialog host (now a transparent leaf).
function showConfirm(title, message, onConfirm) {
  _confirmCb = onConfirm || null;
  document.getElementById('dlgc-title').textContent = title || 'Confirm';
  document.getElementById('dlgc-msg').textContent = message || '';
  openDialog('dlg-confirm');
}
dialogHost.querySelector('.dlg-backdrop').addEventListener('pointerdown', e => {
  e.stopPropagation();
  // Backdrop-dismiss of the unsaved-coverage guard must resolve it (= Cancel), else the
  // host stays blocked with its dialog open and the field never closes.
  if (_ucovOpen) { _ucovOpen = false; transport.send('coverage.cancelClose'); }
  closeDialog();
});
dialogHost.addEventListener('pointerdown', e => e.stopPropagation()); // keep map from panning
const dlgLat = document.getElementById('dlg-lat'), dlgLon = document.getElementById('dlg-lon');
// Uniform numeric input, everywhere. A tablet's decimal keypad (Android Samsung / iOS) has no
// minus key, so signed values couldn't be typed. Every numeric field is instead a type=text
// input with the FULL keyboard (which has "-"), and a filter keeps only digits, one decimal
// point, and a single leading minus. This converts every input[type=number] (config dialogs,
// tool dims, offsets, IP octets, …) and any added later — some config inputs are built
// dynamically — so all fields behave identically. parseFloat/parseInt readers are unaffected.
function filterNumericValue(s) {
  s = s.replace(/[^0-9.\-]/g, '');                                 // digits, dot, minus only
  s = s.replace(/(?!^)-/g, '');                                    // minus only at the start
  const d = s.indexOf('.');
  if (d !== -1) s = s.slice(0, d + 1) + s.slice(d + 1).replace(/\./g, ''); // one dot
  return s;
}
function makeNumericField(el) {
  if (el.dataset.numField) return;
  el.dataset.numField = '1';
  if (el.type === 'number') el.type = 'text';
  el.removeAttribute('inputmode');                                 // full keyboard (no flash, has "-")
  // It's a number field on a full (text) keyboard, so suppress the keyboard's auto-shift/caps,
  // autocorrect, suggestions and spellcheck — otherwise the tablet opens these with caps-lock
  // on (and inconsistently between fields), which is just noise for numeric entry.
  el.setAttribute('autocapitalize', 'none');
  el.setAttribute('autocorrect', 'off');
  el.setAttribute('autocomplete', 'off');
  el.setAttribute('spellcheck', 'false');
  el.classList.add('num-field');
}
function scanNumericFields(root) {
  // Accept the document (nodeType 9) AND elements (nodeType 1); the feature checks below
  // harmlessly skip text nodes the MutationObserver may pass. (An earlier nodeType===1 guard
  // made the initial scanNumericFields(document) a no-op, so static fields like the hitch
  // length kept the decimal keypad while only dynamically-added fields got converted.)
  if (!root) return;
  if (root.matches && root.matches('input[type="number"], .num-field')) makeNumericField(root);
  if (root.querySelectorAll)
    root.querySelectorAll('input[type="number"], .num-field').forEach(makeNumericField);
}
// Filter as the user types — delegated so dynamically-added fields are covered with no re-wiring.
document.addEventListener('input', e => {
  const el = e.target;
  if (!(el instanceof HTMLInputElement) || !el.dataset.numField) return;
  const before = el.value, after = filterNumericValue(before);
  if (after !== before) {
    const pos = Math.max(0, el.selectionStart - (before.length - after.length));
    el.value = after;
    try { el.setSelectionRange(pos, pos); } catch (_) {}
  }
});
scanNumericFields(document);
new MutationObserver(muts => { for (const m of muts) for (const n of m.addedNodes) scanNumericFields(n); })
  .observe(document.documentElement, { childList: true, subtree: true });
function openSimCoords() {
  // Pre-fill with the current vehicle position (from the Status frame), matching
  // the native dialog's GetSimulatorPosition() seed.
  dlgLat.value = statusBar && statusBar.lat != null ? statusBar.lat.toFixed(8) : '';
  dlgLon.value = statusBar && statusBar.lon != null ? statusBar.lon.toFixed(8) : '';
  openDialog('dlg-simcoords');
  dlgLat.focus();
}
document.getElementById('dlg-cancel').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); closeDialog(); });
document.getElementById('dlg-ok').addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation();
  // Commit any in-progress field edit before reading: type=text inputs can hold uncommitted
  // text until blur, and preventDefault above keeps focus on the field.
  if (document.activeElement && document.activeElement.blur) document.activeElement.blur();
  const lat = parseFloat(dlgLat.value), lon = parseFloat(dlgLon.value);
  if (Number.isFinite(lat) && Number.isFinite(lon) && Math.abs(lat) <= 90 && Math.abs(lon) <= 180)
    transport.send('sim.setCoords|' + lat + ',' + lon);
  closeDialog();
});
document.getElementById('dlgc-cancel').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); closeDialog(); });
document.getElementById('dlgc-ok').addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation();
  const cb = _confirmCb; closeDialog(); if (cb) cb();
});

// ---- unsaved-coverage guard (server-driven) ----
// CloseFieldCommand opens DialogType.UnsavedCoverage on the host when coverage was painted
// with no open job. The host has no UI, so we render the prompt here off the Status flag and
// resolve it with the matching host commands (SaveCoverageToJob / DiscardCoverageAndClose /
// CancelUnsavedCoverage). Without this the host opens a dialog nothing shows and the close hangs.
let _ucovOpen = false;
function syncUnsavedCov() {
  const want = !!(statusBar && statusBar.unsavedCoveragePrompt);
  if (want && !_ucovOpen) { _ucovOpen = true; openDialog('dlg-unsavedcov'); }
  else if (!want && _ucovOpen) {
    _ucovOpen = false;
    if (document.getElementById('dlg-unsavedcov').classList.contains('open')) closeDialog();
  }
}
// Resolve optimistically (close now); the host command also flips the Status flag false, so
// the next frame is a no-op rather than a re-open.
function ucovResolve(cmd) { _ucovOpen = false; transport.send(cmd); closeDialog(); }
document.getElementById('ucov-save').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); ucovResolve('coverage.saveJob'); });
document.getElementById('ucov-discard').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); ucovResolve('coverage.discardClose'); });
document.getElementById('ucov-cancel').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); ucovResolve('coverage.cancelClose'); });

// ---- section bar (Phase 7) — per-section colour strip + manual toggle ----
// Read state (per-section ColorCode + master/manual mode) already rides the Tick;
// each tap cycles one section Off->Auto->On (Tier-2 — gated by control authority on
// both ends). Buttons are (re)built when the section count changes; colours update
// per frame. Delegated click handler reads the section index off data-idx.
const sectionBar = document.getElementById('sectionbar');
sectionBar.addEventListener('pointerdown', e => {
  e.stopPropagation(); // don't pan the map
  const btn = e.target.closest('button[data-idx]');
  if (!btn) return;
  if (iHoldControl) transport.send('section.toggle|' + btn.dataset.idx);
  else flashHint('Observer — tap the role badge (top) to take control');
});

// ---- bottom nav (Phase 8) — field-tools toolbar + Flags/AB-line flyouts ----
// Direct-action field tools. Tier-2 ids (data-t2: track.* / headland.* / youturn.skip*)
// fire only while we hold control (the host re-gates); the rest are Tier-1. Toggle
// READ state rides the Tick (tick.tools); AB-dependent buttons key off activeTrackName.
const bottomNav = document.getElementById('bottomnav');
const bnFlags = document.getElementById('bn-flyout-flags');
const bnAb = document.getElementById('bn-flyout-ab');
bottomNav.addEventListener('pointerdown', e => {
  e.stopPropagation(); // don't pan the map
  const btn = e.target.closest('button[data-cmd]');
  if (!btn) return;
  // Gated; the host re-checks. Tell the operator WHY nothing happened instead of a
  // silent no-op — Headland / Section-in-headland / skip-rows all sit here and an
  // Observer tab (seat pinned elsewhere) read as "the button is broken".
  if (btn.hasAttribute('data-t2') && !iHoldControl) { flashHint('Observer — tap the role badge (top) to take control'); return; }
  transport.send(btn.dataset.cmd);
});
// ---- On-screen U-Turn (yellow) / Lateral (cyan) buttons over the map ----
// Bare glyph buttons mirroring native AgOpenGPS; shown per the Screen & Alerts
// "On-Screen Buttons" toggles. U-turn → manual you-turn L/R, lateral → snap the track
// one pass (1 tool width) L/R.
const onScreenBtns = document.getElementById('onscreenbtns');
onScreenBtns.addEventListener('pointerdown', e => {
  const btn = e.target.closest('button[data-cmd]');
  if (!btn) return;
  e.stopPropagation(); // tap the glyph, don't pan the map
  // #50: mid-turn snaps/u-turns are ignored by the pipeline (they'd shift the
  // displayed track while the tractor stays committed to the executing arc, then
  // seek across after). Block them here with a hint instead of silently no-op'ing.
  if (tick && tick.op && tick.op.executing) { flashHint('Finish the U-turn first'); return; }
  transport.send(btn.dataset.cmd);
});
// Fallback data-cmd dispatcher for buttons OUTSIDE the three container dispatchers
// above (#simbar, bottomnav, onscreenbtns). Several panel buttons were authored with
// data-cmd on the assumption it was global — autosteer "Send+Save" (autosteer.sendSave)
// and the offset-fix D-pad (offset.north/south/east/west/zero) — but no dispatcher ever
// reached them, so they did nothing. The containers above handle their own buttons
// (and apply the data-t2 seat gate / mid-turn block), so skip anything inside them to
// avoid double-firing. Tier-2 (data-t2) buttons here are gated the same way the
// bottomnav is, with a hint rather than a silent swallow.
document.addEventListener('pointerdown', e => {
  const btn = e.target.closest('button[data-cmd]');
  if (!btn) return;
  if (btn.closest('#simbar') || btn.closest('#bottomnav') || btn.closest('#onscreenbtns')) return;
  e.stopPropagation();
  if (btn.hasAttribute('data-t2') && !iHoldControl) { flashHint('Observer — tap the role badge (top) to take control'); return; }
  transport.send(btn.dataset.cmd);
});
function applyOnScreenButtons() {
  const d = config && config.display;
  const hasField = !!(scene && scene.hasField); // native gate: IsFieldOpen
  // U-turn (manual you-turn) and Lateral (snap track) only act while AutoSteer is engaged,
  // so hide them otherwise (issue #36). Runs every frame via renderBottomNav, so it tracks
  // engage/disengage live.
  const asActive = !!(tick && tick.op && tick.op.autoSteer);
  document.getElementById('osb-uturn').hidden = !(hasField && asActive && d && d.uTurnButtonVisible);
  document.getElementById('osb-lateral').hidden = !(hasField && asActive && d && d.lateralButtonVisible);
}
function bnToggleFly(fly, btn) {
  const open = !fly.classList.contains('open');
  bnFlags.classList.remove('open'); bnAb.classList.remove('open');
  document.getElementById('bn-flags').classList.remove('menuopen');
  document.getElementById('bn-abmenu').classList.remove('menuopen');
  if (open) { fly.classList.add('open'); btn.classList.add('menuopen'); }
}
document.getElementById('bn-flags').addEventListener('pointerdown', e => { e.stopPropagation(); bnToggleFly(bnFlags, e.currentTarget); });
// "Place Flag on Map" (Phase MT) — arm tap capture; the next map tap drops a flag at the
// tapped field point via flag.placeAt (host PlaceFlagAtWorldPosition). No data-cmd, so the
// bottomNav delegate ignores it. Closes the flyout, then captures one tap.
function armPlaceFlagOnMap() {
  startMapTap({
    hint: 'Tap the map to place a flag',
    onTap: (e2, n2) => { transport.send('flag.placeAt|' + e2.toFixed(3) + ',' + n2.toFixed(3)); endMapTap(); },
  });
}
document.getElementById('bn-flag-onmap').addEventListener('pointerdown', e => {
  e.stopPropagation();
  bnFlags.classList.remove('open');
  document.getElementById('bn-flags').classList.remove('menuopen');
  armPlaceFlagOnMap();
});

// ---- flag list (mirrors native FlagListDialogPanel) ----
// Colour swatch (tap → 10-colour picker), name (tap → inline rename), distance+bearing
// from the vehicle (computed here off the Tick), locate (pan the camera to the flag),
// delete. Footer: Place Here / Place on Map / Delete All / Close. Reads scene.flags
// (index = the projected order, which matches the host's Flags list).
const FLAG_HEX = ['#FF0000', '#00CC00', '#FFCC00', '#2080E0', '#FF8800', '#9933CC', '#00BBCC', '#FF66AA', '#FFFFFF', '#333333'];
const FLAG_NAMES = ['Red', 'Green', 'Yellow', 'Blue', 'Orange', 'Purple', 'Cyan', 'Pink', 'White', 'Black'];
const COMPASS = ['N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW'];
document.getElementById('bn-flag-list').addEventListener('pointerdown', e => {
  e.stopPropagation();
  bnFlags.classList.remove('open');
  document.getElementById('bn-flags').classList.remove('menuopen');
  openFlagList();
});
function openFlagList() { renderFlagList(); openDialog('dlg-flags'); }
function renderFlagList() {
  const list = document.getElementById('flag-list');
  const fl = (scene && scene.flags) || [];
  if (!fl.length) { list.innerHTML = '<div class="trk-empty">No flags placed</div>'; return; }
  list.innerHTML = '';
  fl.forEach((f, i) => {
    let dist = '';
    if (tick && tick.pose) {
      const de = f.e - tick.pose.e, dn = f.n - tick.pose.n;
      const brg = (Math.atan2(de, dn) * 180 / Math.PI + 360) % 360;
      dist = Math.hypot(de, dn).toFixed(0) + ' m ' + COMPASS[Math.round(brg / 45) % 8];
    }
    const row = document.createElement('div');
    row.className = 'flg-row';
    row.innerHTML =
      '<button class="flg-sw"></button>' +
      '<span class="flg-name"></span>' +
      '<span class="flg-dist"></span>' +
      '<button class="flg-loc" title="Locate (pan to flag)">⊕</button>' +
      '<button class="flg-del" title="Delete flag">✕</button>';
    row.querySelector('.flg-sw').style.background = f.color;
    const nameEl = row.querySelector('.flg-name');
    nameEl.textContent = f.name || ('Flag ' + (i + 1));
    row.querySelector('.flg-dist').textContent = dist;
    // Colour swatch → toggle an inline 10-colour picker under the row.
    row.querySelector('.flg-sw').addEventListener('pointerdown', ev => { ev.stopPropagation(); toggleFlagColorPick(row, i); });
    // Name → inline rename.
    nameEl.addEventListener('pointerdown', ev => { ev.stopPropagation(); editFlagName(nameEl, i); });
    // Locate → pan the camera to the flag, close the dialog to reveal the map.
    row.querySelector('.flg-loc').addEventListener('pointerdown', ev => {
      ev.stopPropagation(); camE = f.e; camN = f.n; cameraMode = 2; closeDialog();
    });
    row.querySelector('.flg-del').addEventListener('pointerdown', ev => { ev.stopPropagation(); transport.send('flag.delete|' + i); });
    list.appendChild(row);
  });
}
function toggleFlagColorPick(row, idx) {
  const existing = row.nextSibling;
  if (existing && existing.classList && existing.classList.contains('flg-colorpick')) { existing.remove(); return; }
  for (const c of document.querySelectorAll('.flg-colorpick')) c.remove();
  const pick = document.createElement('div');
  pick.className = 'flg-colorpick';
  FLAG_HEX.forEach((hex, ci) => {
    const b = document.createElement('button');
    b.style.background = hex; b.title = FLAG_NAMES[ci];
    b.addEventListener('pointerdown', ev => { ev.stopPropagation(); transport.send('flag.setColor|' + idx + ',' + FLAG_NAMES[ci]); pick.remove(); });
    pick.appendChild(b);
  });
  row.after(pick);
}
function editFlagName(nameEl, idx) {
  const cur = nameEl.textContent;
  const input = document.createElement('input');
  input.className = 'flg-nameedit'; input.value = cur;
  nameEl.replaceWith(input); input.focus(); input.select();
  const commit = () => { const v = input.value.trim(); if (v && v !== cur) transport.send('flag.rename|' + idx + ',' + v); renderFlagList(); };
  input.addEventListener('keydown', ev => { ev.stopPropagation(); if (ev.key === 'Enter') commit(); else if (ev.key === 'Escape') renderFlagList(); });
  input.addEventListener('blur', commit);
}
document.getElementById('flag-placehere').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); transport.send('flag.placeHere'); });
document.getElementById('flag-placemap').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); closeDialog(); armPlaceFlagOnMap(); });
document.getElementById('flag-deleteall').addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation();
  showConfirm('Delete All Flags', 'Delete all flags? This cannot be undone.', () => transport.send('flag.deleteAll'));
});
document.getElementById('flag-close').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); closeDialog(); });
document.getElementById('bn-abmenu').addEventListener('pointerdown', e => { e.stopPropagation(); bnToggleFly(bnAb, e.currentTarget); });

// ---- AB-line creation (mirrors native QuickABSelector + DrawAB dialogs) ----
// All four modes run the REAL native commands on the host (no geometry in JS):
//   straight/curve   — DrawAB/DrawCurve: tap points on the map (s2w → track.drawPoint)
//   driveAB          — set A then B at the vehicle's live GPS (track.setABGps)
//   recordCurve      — record a curve by driving, Finish to create
// The client only captures input, mirrors a live preview for the map-tap modes, and
// ships points/commands. `drawMode` drives the map-tap preview; `abFlow` is the active
// creation flow (any of the four) that the bottom toolbar + hint follow.
let drawMode = null;     // 'straight' | 'curve' (map-tap modes only) — drives the preview
let drawPts = [];        // [{e,n}] captured so far — preview only (host holds the real list)
let abFlow = null;       // 'straight' | 'curve' | 'driveAB' | 'recordCurve' | 'boundaryPick' | 'bndSegExtend' | null
let driveStep = 0;       // Drive-AB / record-curve step: 0 → next press sets A/Start, 1 → sets B/End
let curveMarks = [];     // client-side path dots dropped while driving a record-curve (Set Start→End)
let _lastCurveMark = null;
const drawBar = document.getElementById('draw-toolbar');
const hintEl = document.getElementById('maptap-hint');
function abHint() {
  switch (abFlow) {
    case 'straight': return drawPts.length === 0 ? 'Tap Point A' : 'Tap Point B';
    case 'curve': return `Tap points (${drawPts.length}) — Finish when done`;
    case 'driveAB': return driveStep === 0 ? 'Drive to A, then Set Point' : 'Drive to B, then Set Point';
    case 'recordCurve': return driveStep === 0 ? 'Drive to A, then Set Point A' : 'Drive the curve to B, then Set Point B';
  }
  return '';
}
function setAbHint() { hintEl.textContent = abHint(); }
// #40: while drawing a straight AB line, ride an "A"/"B" letter on the crosshair so it's
// clear which point the next tap places, and render the placed points as native-style dots
// (orange A / blue B, matching FormABDraw's DrawABTouchPoints colours).
const AB_A_COLOR = '#FFBF59', AB_B_COLOR = '#8080FF';
const abLabelEl = document.getElementById('maptap-ab');
const abDotLabelsEl = document.getElementById('ab-dot-labels');
// Sequential point label: A, B, C … then 27, 28 … for very long curves.
function abLetter(i) { return i < 26 ? String.fromCharCode(65 + i) : String(i + 1); }
function abLabelColor(i) { return i === 0 ? AB_A_COLOR : i === 1 ? AB_B_COLOR : '#FFFFFF'; }
// A/B (straight) or A,B,C… (curve) letter riding the crosshair — the letter the NEXT tap places.
function updateAbCursorLabel(x, y) {
  if (!abLabelEl) return;
  if (mapTap && (abFlow === 'straight' || abFlow === 'curve')) {
    const i = drawPts.length;
    abLabelEl.textContent = abLetter(i);
    abLabelEl.style.color = abLabelColor(i);
    if (x != null) { abLabelEl.style.left = x + 'px'; abLabelEl.style.top = y + 'px'; }
    abLabelEl.classList.add('show');
  } else {
    abLabelEl.classList.remove('show');
  }
}
// Persistent letter beside each dropped point (straight A/B, curve A,B,C…). Positioned per
// frame from drawPts (world→screen); cleared when the draw ends. DOM (CanvasKit has no text).
function renderAbDotLabels() {
  if (!abDotLabelsEl) return;
  const n = drawPts.length || 0; // letters for map-tap AND Drive-AB markers (drawMode may be null)
  while (abDotLabelsEl.children.length < n) abDotLabelsEl.appendChild(document.createElement('span'));
  for (let i = 0; i < abDotLabelsEl.children.length; i++) {
    const span = abDotLabelsEl.children[i];
    if (i >= n || pw(drawPts[i].e, drawPts[i].n) < 1.0) { span.style.display = 'none'; continue; }
    const xy = w2s(drawPts[i].e, drawPts[i].n);
    span.textContent = abLetter(i);
    span.style.color = abLabelColor(i);
    span.style.left = xy[0] + 'px';
    span.style.top = xy[1] + 'px';
    span.style.display = 'block';
  }
}
// Configure the bottom toolbar buttons for the active flow. ext = the A/B ±fence
// boundary-curve trim row (the curve already exists: Done keeps it, Cancel discards it and
// restores the previous selection — track.boundarySegCancel).
// bnd = the AB / Curve / Cancel touch choice row shown once two boundary points are picked
// (AOG FormABDraw's btnMakeABLine / btnMakeCurve / btnCancelTouch); Cancel stays = leave the tool.
function showAbBar(setPoint, undo, finish, ext, bnd) {
  drawBar.querySelector('#draw-setpoint').style.display = setPoint ? '' : 'none';
  drawBar.querySelector('#draw-undo').style.display = undo ? '' : 'none';
  drawBar.querySelector('#draw-finish').style.display = finish ? '' : 'none';
  drawBar.querySelector('#draw-finish').textContent = 'Finish'; // callers wanting Save/Done set it after
  for (const b of drawBar.querySelectorAll('.draw-ext')) b.style.display = ext ? '' : 'none';
  for (const b of drawBar.querySelectorAll('.draw-bnd')) b.style.display = bnd ? '' : 'none';
  drawBar.querySelector('#draw-cancel').style.display = ''; // always — every flow can be backed out
  drawBar.classList.add('show');
}
function startDrawTrack(mode) {           // map-tap straight/curve (ungated — creating data)
  abFlow = mode; drawMode = mode; drawPts = [];
  transport.send(mode === 'straight' ? 'track.drawStraight' : 'track.drawCurve');
  showAbBar(false, mode === 'curve', mode === 'curve');
  startMapTap({ hint: abHint(), onTap: drawTap });
}
function startDriveAB() {                  // GPS: set A then B at the vehicle (ungated)
  abFlow = 'driveAB'; driveStep = 0; drawPts = []; // drawPts holds the dropped A/B markers
  transport.send('track.driveAB');
  showAbBar(true, false, false);
  document.getElementById('draw-setpoint').textContent = 'Set Point A';
  hintEl.classList.add('show'); setAbHint();
}
function startRecordCurve() {              // GPS: record by driving — Set Start → Set End (ungated)
  abFlow = 'recordCurve'; driveStep = 0; curveMarks = []; _lastCurveMark = null;
  // Recording doesn't begin until "Set Start" (deferred track.recordCurve), so the operator
  // can position at the curve's start first. The set-point button drives the two-step.
  showAbBar(true, false, false);
  document.getElementById('draw-setpoint').textContent = 'Set Point A';
  hintEl.classList.add('show'); setAbHint();
}
// "Bnd. AB/Curve" — tap point A then point B on the boundary, THEN choose what to make
// (mirrors AgOpenGPS FormABDraw: the second tap is a selection, not a commit). "AB" sends
// track.boundaryAB (straight line through the two snapped fence vertices), "Curve" sends
// track.boundaryCurveSeg (the host walks the shorter arc between them), "Cancel touch"
// clears the two dots and stays in the tool (btnCancelTouch). Points snap to the nearest
// boundary vertex for display; the host re-snaps authoritatively.
// Ring-aware snap, mirroring the host (and AOG FormABDraw.cs:625-661): tap A searches
// EVERY ring (outer + inners); tap B searches only the ring A landed on, so the preview
// dots show exactly the vertices the host will use. onlyRing = index into scene.boundaries.
function nearestBoundaryPt(e, n, onlyRing) {
  let best = null, bd = Infinity, bestRing = -1;
  const bs = scene && scene.boundaries;
  if (bs) for (let r = 0; r < bs.length; r++) {
    if (onlyRing != null && r !== onlyRing) continue;
    for (const p of bs[r]) {
      const dx = p.e - e, dy = p.n - n, d = dx * dx + dy * dy;
      if (d < bd) { bd = d; best = p; bestRing = r; }
    }
  }
  return best ? { e: best.e, n: best.n, ring: bestRing } : { e, n, ring: -1 };
}
function startBoundaryCurve() {
  abFlow = 'boundaryPick'; drawPts = [];
  showAbBar(false, false, false);          // draw toolbar shows just Cancel until A and B are picked
  startMapTap({ hint: 'Tap point A on the boundary', onTap: boundaryCurveTap });
}
function boundaryCurveTap(e, n) {
  drawPts.push(nearestBoundaryPt(e, n, drawPts.length ? drawPts[0].ring : null));   // A: any ring; B: A's ring
  if (drawPts.length < 2) { hintEl.textContent = 'Tap point B on the boundary'; return; }
  // Two points picked — review before commit: the orange A / blue B dots stay on the map
  // while the operator chooses AB / Curve / Cancel touch (or Cancel to leave the tool).
  endMapTap();                             // taps done — the buttons drive the rest
  showAbBar(false, false, false, false, true);
  hintEl.textContent = 'Make an AB line or a Curve through A and B'; hintEl.classList.add('show');
}
function boundaryPickArg() {
  const a = drawPts[0], b = drawPts[1];
  return a.e.toFixed(3) + ',' + a.n.toFixed(3) + ',' + b.e.toFixed(3) + ',' + b.n.toFixed(3);
}
function boundaryMakeAB() {                // "AB" — straight line through the two fence vertices
  if (abFlow !== 'boundaryPick' || drawPts.length < 2) return;
  transport.send('track.boundaryAB|' + boundaryPickArg());
  endAbFlow();                             // line created, selected and saved host-side
}
function boundaryMakeCurve() {             // "Curve" — arc along the boundary between A and B
  if (abFlow !== 'boundaryPick' || drawPts.length < 2) return;
  transport.send('track.boundaryCurveSeg|' + boundaryPickArg());
  // Curve created — stay open in a trim phase: A/B ±fence walk each end along the boundary
  // (5 m per tap, host-clamped; our feature, not AOG's straight A++/B++). Done keeps the
  // curve (client-only — it was committed at creation); Cancel discards it host-side.
  abFlow = 'bndSegExtend'; drawPts = [];
  showAbBar(false, false, true, true);
  document.getElementById('draw-finish').textContent = 'Done';
  hintEl.textContent = 'Trim each end along the fence, then Done (Cancel discards the curve)'; hintEl.classList.add('show');
}
function boundaryCancelTouch() {           // "Cancel touch" — drop both dots, pick again (tool stays open)
  if (abFlow !== 'boundaryPick') return;
  drawPts = [];
  showAbBar(false, false, false);
  startMapTap({ hint: 'Tap point A on the boundary', onTap: boundaryCurveTap });
}
function drawTap(e, n) {                    // map-tap point captured (straight/curve)
  drawPts.push({ e, n });
  transport.send('track.drawPoint|' + e.toFixed(3) + ',' + n.toFixed(3));
  // Straight = exactly 2 points; the host auto-creates the line on the 2nd, so we're done.
  if (abFlow === 'straight' && drawPts.length >= 2) { endAbFlow(); return; }
  setAbHint();
}
function abSetPoint() {                     // Set-point button — drives Drive-AB and record-curve
  const btn = document.getElementById('draw-setpoint');
  if (abFlow === 'driveAB') {
    if (driveStep > 1) return;             // guard a double-B while B lingers
    transport.send('track.setABGps');      // SetABPoint uses live GPS in DriveAB mode
    // Drop an A/B marker at the vehicle so the operator sees where each point landed.
    if (tick && tick.pose) drawPts.push({ e: tick.pose.e, n: tick.pose.n });
    if (driveStep === 0) { driveStep = 1; btn.textContent = 'Set Point B'; setAbHint(); }
    else {                                  // B set → host created the line; show B a beat, then clear
      driveStep = 2;
      setTimeout(() => { if (abFlow === 'driveAB') endAbFlow(); }, 700);
    }
  } else if (abFlow === 'recordCurve') {
    if (driveStep > 1) return;              // guard a double-B while B lingers
    if (driveStep === 0) {                  // Set Point A → drop the A endpoint + begin recording
      transport.send('track.recordCurve');
      driveStep = 1; btn.textContent = 'Set Point B';
      if (tick && tick.pose) {
        drawPts.push({ e: tick.pose.e, n: tick.pose.n });   // labeled "A" endpoint dot (orange)
        _lastCurveMark = { e: tick.pose.e, n: tick.pose.n }; // seed green-dot spacing; no dot at A itself
      }
      setAbHint();
    } else {                                // Set Point B → drop the B endpoint + finish
      driveStep = 2;
      if (tick && tick.pose) drawPts.push({ e: tick.pose.e, n: tick.pose.n }); // labeled "B" endpoint dot (blue)
      transport.send('track.finishCurve');  // host builds the curve from its recorded points
      setTimeout(() => { if (abFlow === 'recordCurve') endAbFlow(); }, 700);   // show B a beat, then clear
    }
  }
}
function abUndo() {
  if (abFlow !== 'curve' || !drawPts.length) return;
  drawPts.pop();
  transport.send('track.drawUndo');
  setAbHint();
}
function abFinish() {
  if (abFlow === 'curve') transport.send('track.drawFinish');
  else if (abFlow === 'recordCurve') transport.send('track.finishCurve');
  else if (abFlow === 'bndSegExtend') { endAbFlow(); return; } // Done — curve already committed
  else return;
  endAbFlow();
}
function abCancel() {
  if (!abFlow) return;
  if (abFlow === 'bndSegExtend') { transport.send('track.boundarySegCancel'); endAbFlow(); return; } // discard the new curve, restore the previous selection
  transport.send('track.drawCancel'); // CancelABCreationCommand — universal (all modes)
  endAbFlow();
}
function endAbFlow() {
  if (abFlow === 'bndSegExtend') document.getElementById('draw-finish').textContent = 'Finish';
  abFlow = null; drawMode = null; drawPts = []; driveStep = 0;
  curveMarks = []; _lastCurveMark = null; // clear record-curve path dots
  drawBar.classList.remove('show');
  endMapTap();
  hintEl.classList.remove('show');
}
// Drop a path dot every ~1.5 m while recording a drive-curve (between Set Point A and B), so
// the operator sees the shape being captured. Visual only — the host records the real curve.
function sampleCurveMark() {
  if (abFlow !== 'recordCurve' || driveStep !== 1 || !tick || !tick.pose) return;
  const p = tick.pose;
  if (_lastCurveMark && Math.hypot(p.e - _lastCurveMark.e, p.n - _lastCurveMark.n) < 1.5) return;
  _lastCurveMark = { e: p.e, n: p.n };
  curveMarks.push(_lastCurveMark);
}
function drawCurveMarksSk(canvas) {
  if (!curveMarks.length) return;
  const rad = Math.max(3, 0.4 * pxPerM);
  SKP.flagFill.setColor(ckColor('#39FF6A')); // recording green
  for (const p of curveMarks) {
    if (pw(p.e, p.n) < 1.0) continue; // behind the near plane
    const xy = w2s(p.e, p.n);
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagFill);
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagOutline);
  }
}
// ---- boundary draw-on-map (Phase MT) — native BoundaryMapDialog equivalent ----
// Shows a Bing aerial underlay (satEnabled) and captures the boundary polygon by tapping
// the imagery (s2w → field E/N). On finish the host builds the boundary AND assembles the
// covered Bing tiles into the field-background PNG (boundary.fromMapPoints). Client-side
// point buffer for the live preview; no geometry in JS.
let satBnd = false;      // drawing a boundary on the satellite underlay
let satPts = [];         // [{e,n}] polygon vertices (preview only)
function startSatBoundary() {
  satEnabled = true; satBnd = true; satPts = [];
  lnCloseAll();                           // close the boundary menu
  showAbBar(false, true, true);           // Undo + Finish + Cancel
  startMapTap({ hint: 'Tap the field corners on the imagery — Finish when done', onTap: satTap });
}
function satTap(e, n) { satPts.push({ e, n }); setSatHint(); }
function setSatHint() { hintEl.textContent = `Boundary: ${satPts.length} point${satPts.length === 1 ? '' : 's'} — Finish when done`; }
function satUndo() { if (satPts.length) { satPts.pop(); setSatHint(); } }
function satFinish() {
  if (satPts.length >= 3)
    transport.send('boundary.fromMapPoints|' + satPts.map(p => p.e.toFixed(3) + ',' + p.n.toFixed(3)).join(';'));
  endSatBoundary();
}
function satCancel() { endSatBoundary(); }
function endSatBoundary() {
  satBnd = false; satEnabled = false; satPts = [];
  drawBar.classList.remove('show'); endMapTap(); hintEl.classList.remove('show');
}

// ---- headland draw-on-map (Field Builder stage 2) — tap two boundary points -----------
// Preview snaps each tap to the nearest boundary vertex (visual only; the host re-snaps and
// builds the inward offset + headland). Ships headland.fromMapPoints on the 2nd tap.
let hlFlow = null;       // 'line' | 'curve' — drawing a headland segment, else null
let hlPts = [];          // [{e,n}] tapped+snapped boundary points (preview)
let hlOffset = 12;       // inward offset (m) chosen in the Add sub-view
function hlSnap(e, n) {
  const ring = scene && scene.boundaries && scene.boundaries[0];
  if (!ring || !ring.length) return { e, n };
  let best = ring[0], bd = Infinity;
  for (const p of ring) { const d = (p.e - e) ** 2 + (p.n - n) ** 2; if (d < bd) { bd = d; best = p; } }
  return { e: best.e, n: best.n };
}
function setHlHint() { hintEl.textContent = hlPts.length === 0 ? 'Tap the start of an edge (add a line per edge to enclose the headland)' : 'Tap the end of the edge'; }
function startHeadlandDraw(mode, offset) {
  const ring = scene && scene.boundaries && scene.boundaries[0];
  if (!ring || ring.length < 3) {
    const h = document.getElementById('maptap-hint');
    h.textContent = 'No boundary to offset'; h.classList.add('show');
    setTimeout(() => h.classList.remove('show'), 1800);
    return;
  }
  hlFlow = mode; hlOffset = offset; hlPts = [];
  showAbBar(false, true, true);   // Undo + Finish + Cancel — draw MANY lines, Finish to stop
  startMapTap({ hint: 'Tap the start of an edge (add a line per edge to enclose the headland)', onTap: hlTap });
}
function hlTap(e, n) {
  hlPts.push(hlSnap(e, n));
  if (hlPts.length >= 2) {
    // Ship this edge line and immediately re-arm for the NEXT edge — headland drawing is
    // continuous (one line per edge) until the operator taps Finish, mirroring native.
    transport.send('headland.fromMapPoints|' + hlFlow + ',' + hlOffset + ',' +
      hlPts[0].e.toFixed(3) + ',' + hlPts[0].n.toFixed(3) + ',' +
      hlPts[1].e.toFixed(3) + ',' + hlPts[1].n.toFixed(3));
    hlPts = [];
    hintEl.textContent = 'Edge added — draw the next edge, or tap Finish';
    return;
  }
  setHlHint();
}
function hlUndo() { if (hlPts.length) { hlPts.pop(); setHlHint(); } }
function endHeadlandDraw() {
  hlFlow = null; hlPts = [];
  drawBar.classList.remove('show'); endMapTap(); hintEl.classList.remove('show');
}

// ---- Field Builder stage 4 — on-map point editing -----------------------------------
// Drag a track's / headland segment's points on the main map, then Save. The client only
// captures the dragged positions; the host rebuilds (track heading recompute, or headland
// snap-to-boundary + re-offset + rebuild). One handle drag at a time; the map still pans
// when the press doesn't start on a handle.
let editSession = null;  // { kind:'track'|'headland', index, points:[{e,n}] }
let editDragIdx = -1;
let flashHintTimer = null;
function flashHint(t, ms = 1800) {
  const h = document.getElementById('maptap-hint'); h.textContent = t; h.classList.add('show');
  if (flashHintTimer) clearTimeout(flashHintTimer);
  flashHintTimer = setTimeout(() => h.classList.remove('show'), ms);
}
function startTrackEdit() {
  const active = (scene && scene.trackList || []).find(t => t.active);
  const at = scene && scene.tracks && scene.tracks[0];
  if (!active || !at || !at.points || at.points.length < 2) { flashHint('Select/activate a track to edit'); return; }
  editSession = { kind: 'track', index: active.index, points: at.points.map(p => ({ e: p.e, n: p.n })) };
  beginEdit('Drag the track points, then Save');
}
function startHeadlandEdit() {
  const hs = scene && scene.headlandSegs; if (fbHlSel < 0 || !hs || !hs[fbHlSel]) return;
  const s = hs[fbHlSel];
  if (s.type === 'Boundary') { flashHint('Whole-boundary headland is not point-edited'); return; }
  editSession = { kind: 'headland', index: s.index, points: [{ e: s.endA.e, n: s.endA.n }, { e: s.endB.e, n: s.endB.n }] };
  beginEdit('Drag the two endpoints (snap to boundary), then Save');
}
function beginEdit(hint) {
  showAbBar(false, false, true);  // Save (Finish) + Cancel
  document.getElementById('draw-finish').textContent = 'Save';
  hintEl.textContent = hint; hintEl.classList.add('show'); document.body.classList.add('maptap');
}
function editHitTest(px, py) {
  if (!editSession) return -1;
  let best = -1, bd = 22 * 22;
  for (let i = 0; i < editSession.points.length; i++) {
    const p = editSession.points[i];
    if ((pw(p.e, p.n)) < 1.0) continue;
    const xy = w2s(p.e, p.n); const dx = xy[0] - px, dy = xy[1] - py; const d = dx * dx + dy * dy;
    if (d < bd) { bd = d; best = i; }
  }
  return best;
}
function saveEdit() {
  if (!editSession) return;
  const pts = editSession.points;
  if (editSession.kind === 'track')
    transport.send('track.editSave|' + editSession.index + ';' + pts.map(p => p.e.toFixed(3) + ',' + p.n.toFixed(3)).join(';'));
  else if (pts.length >= 2)
    transport.send('headland.editSave|' + editSession.index + ',' + pts[0].e.toFixed(3) + ',' + pts[0].n.toFixed(3) + ',' + pts[1].e.toFixed(3) + ',' + pts[1].n.toFixed(3));
  endEdit();
}
function endEdit() {
  editSession = null; editDragIdx = -1;
  drawBar.classList.remove('show'); document.getElementById('draw-finish').textContent = 'Finish';
  hintEl.classList.remove('show'); document.body.classList.remove('maptap');
}
function drawEditHandlesSk(canvas) {
  if (!editSession) return;
  if (editSession.points.length >= 2) strokePtsSk(canvas, editSession.points, false, SKP.track);
  const rad = Math.max(5, 0.7 * pxPerM);
  for (let i = 0; i < editSession.points.length; i++) {
    const p = editSession.points[i];
    if ((pw(p.e, p.n)) < 1.0) continue;
    const xy = w2s(p.e, p.n);
    SKP.flagFill.setColor(ckColor(i === editDragIdx ? '#FFD24A' : '#40E0FF'));
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagFill);
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagOutline);
  }
}
document.getElementById('fb-hl-edit').addEventListener('pointerdown', e => { e.stopPropagation(); startHeadlandEdit(); });

// Draw toolbar serves whichever map-draw flow is active (AB line / boundary-on-map / headland).
drawBar.addEventListener('pointerdown', e => e.stopPropagation()); // don't pan / not a map tap
document.getElementById('draw-setpoint').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); abSetPoint(); });
document.getElementById('draw-undo').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); hlFlow ? hlUndo() : satBnd ? satUndo() : abUndo(); });
document.getElementById('draw-finish').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); editSession ? saveEdit() : hlFlow ? endHeadlandDraw() : satBnd ? satFinish() : abFinish(); });
document.getElementById('draw-cancel').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); editSession ? endEdit() : hlFlow ? endHeadlandDraw() : satBnd ? satCancel() : abCancel(); });
// Bnd. AB/Curve choice row — shown once both boundary points are picked.
document.getElementById('draw-bnd-ab').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); boundaryMakeAB(); });
document.getElementById('draw-bnd-curve').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); boundaryMakeCurve(); });
document.getElementById('draw-bnd-canceltouch').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); boundaryCancelTouch(); });
// Bnd. Curve trim row (A/B ±fence) — one 5 m step ALONG THE BOUNDARY per tap (repeat-tap
// friendly; the host clamps the ends). Not AOG's A++/B++ — those are the straight run-out in
// the Tracks manager (track.extendEnd).
for (const [id, arg] of [['draw-exta-plus', 'A,1'], ['draw-exta-minus', 'A,-1'], ['draw-extb-minus', 'B,-1'], ['draw-extb-plus', 'B,1']])
  document.getElementById(id).addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); transport.send('track.boundarySegExtend|' + arg); });
// Touch cancel for bare map-tap flows (flag). stopPropagation so the pill isn't taken as a map tap.
document.getElementById('maptap-cancel').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); if (mapTap && mapTap.onCancel) mapTap.onCancel(); else endMapTap(); });

// ---- AB flyout launchers → the three creation/management dialogs ----
function abFlyoutClose() { bnAb.classList.remove('open'); document.getElementById('bn-abmenu').classList.remove('menuopen'); }
document.getElementById('bn-tracks').addEventListener('pointerdown', e => { e.stopPropagation(); abFlyoutClose(); openTracksManager(); });
document.getElementById('bn-quickab').addEventListener('pointerdown', e => { e.stopPropagation(); abFlyoutClose(); openDialog('dlg-quickab'); });
// Quick-AB selector buttons.
// Quick AB — the single creation hub: GPS-driven, from-boundary, and draw-on-map modes.
document.getElementById('dlg-quickab').querySelectorAll('[data-qab]').forEach(b => b.addEventListener('pointerdown', e => {
  e.preventDefault(); e.stopPropagation(); closeDialog();
  const m = b.dataset.qab;
  if (m === 'aPlus') {
    // A+ builds an AB line from the vehicle's current position + heading. It needs a GPS
    // fix; the host bails silently without one, so give feedback here (StatusMessage isn't
    // on the wire). flashHint after a tick so it isn't cleared by the dialog close.
    const p = tick && tick.pose;
    if (!p || (!p.e && !p.n)) setTimeout(() => flashHint('A+ needs a GPS position — start the simulator or GPS'), 0);
    else transport.send('track.aPlus');
  }
  else if (m === 'driveAB') startDriveAB();
  else if (m === 'recordCurve') startRecordCurve();
  else if (m === 'boundaryEdge') transport.send('track.createFromBoundary');
  else if (m === 'boundaryCurve') startBoundaryCurve();
  else if (m === 'allEdges') transport.send('track.allEdges');
  else if (m === 'drawStraight') startDrawTrack('straight');
  else if (m === 'drawCurve') startDrawTrack('curve');
}));
document.getElementById('dlg-qab-cancel').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); closeDialog(); });

// ---- Tracks manager (mirrors native TracksDialogPanel) ----
// View-only without control (just reads scene.trackList); the actions are Tier-2.
function openTracksManager() { renderTracksList(); openDialog('dlg-tracks'); }
function renderTracksList() {
  // Dim the guidance-affecting actions when we're not the operator. Delete/swap/activate
  // change the active line; import, visibility and rec-path display are data → ungated.
  for (const id of ['trk-delete', 'trk-swap', 'trk-activate', 'trk-exta', 'trk-extb'])
    document.getElementById(id).classList.toggle('disabled', !iHoldControl);
  const list = document.getElementById('trk-list');
  const tl = (scene && scene.trackList) || [];
  // A+49 m / B+49 m (AOG A++/B++) act on the SELECTED track and only make sense on a curve
  // (AOG FormABDraw greys them unless mode == Curve): show them only when the active row is
  // a Curve. The host re-checks (AB line / closed track refused with a status message).
  const selCurve = tl.some(t => t.active && t.type === 'Curve');
  for (const id of ['trk-exta', 'trk-extb']) document.getElementById(id).hidden = !selCurve;
  if (!tl.length) { list.innerHTML = '<div class="trk-empty">No tracks in this field</div>'; return; }
  list.innerHTML = '';
  for (const t of tl) {
    const row = document.createElement('div');
    row.className = 'trk-row' + (t.active ? ' active' : '');
    row.innerHTML =
      '<input type="checkbox" class="trk-vis"' + (t.visible ? ' checked' : '') + '>' +
      '<span class="trk-name"></span>' +
      '<span class="trk-type"></span>' +
      '<span class="trk-dot"></span>';
    row.querySelector('.trk-name').textContent = t.name;
    row.querySelector('.trk-type').textContent = t.type || '—';
    // Tap row (not the checkbox) → toggle active. Checkbox → toggle visibility.
    row.addEventListener('pointerdown', e => {
      if (e.target.classList.contains('trk-vis')) return; // let the checkbox handle it
      e.stopPropagation();
      if (iHoldControl) transport.send('track.select|' + t.index);
    });
    const cb = row.querySelector('.trk-vis');
    cb.addEventListener('change', e => {
      e.stopPropagation(); // visibility is display-only → ungated (any browser)
      transport.send('track.setVisible|' + t.index + ',' + (cb.checked ? 1 : 0));
    });
    list.appendChild(row);
  }
}
document.getElementById('trk-delete').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); if (iHoldControl) transport.send('track.delete'); });
document.getElementById('trk-swap').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); if (iHoldControl) transport.send('track.swapAB'); });
// AOG A++ / B++: straight 49 m run-out from the selected curve's end (track.extendEnd|A|B,metres).
document.getElementById('trk-exta').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); if (iHoldControl) transport.send('track.extendEnd|A,49'); });
document.getElementById('trk-extb').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); if (iHoldControl) transport.send('track.extendEnd|B,49'); });
document.getElementById('trk-activate').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); if (iHoldControl) transport.send('track.activate'); });
document.getElementById('trk-recpaths').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); transport.send('track.toggleRecPaths'); });
document.getElementById('trk-import').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); closeDialog(); lnOpen('importtracks'); });
document.getElementById('dlg-tracks-close').addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); closeDialog(); });

// ---- left nav (Phase 9) — config/settings panels via the config bridge ----
// Vertical button bar; each button toggles a non-modal panel (only one open at a
// time). Panels READ from the config frame (or existing frames, e.g. units =
// Status.isMetric) and WRITE via `config.set|key:value` (Tier-1; the host applies it
// to ConfigurationStore). Grows one entry per sub-phase.
// Navigation: top-level buttons open a panel; sub-panels (vehicle/tool config) are
// reached from the hub and carry a Back button. One panel open at a time.
const LN_NAV_PANELS = ['routeplan', 'ratecontrol', 'modulesetup', 'obstacles', 'tankmix', 'screenalerts', 'tools', 'rollcorr', 'fieldtools', 'fieldbuilder', 'offsetfix', 'importtracks', 'recpath', 'boundarymenu', 'boundaryplayer', 'kmlboundary', 'vehtoolhub', 'vehiclecfg', 'toolcfg', 'autosteercfg', 'networkio', 'ntripprofiles', 'ntripeditor', 'smartwas', 'fieldops', 'fieldsandjobs', 'newfield', 'fromexisting', 'isoimport', 'kmlimport', 'resumejob', 'agsettings', 'agupload', 'agdownload', 'filemenu', 'appsettings', 'language', 'viewsettings', 'logviewer', 'hotkeys', 'help', 'about', 'bugreport'];
// Watch-the-tractor panels opt OUT of the light-dismiss scrim — the map must stay
// interactive (pan/zoom to follow the tractor while capturing). They close only via
// the header (Back / ✕). The Route Planner joined them: the operator pans/inspects
// the planned paths while adjusting knobs, so a map tap must not dismiss the panel.
const NO_SCRIM = new Set(['smartwas', 'recpath', 'boundaryplayer', 'fieldbuilder', 'routeplan']);
const lnScrim = document.getElementById('ln-scrim');
function lnCloseAll() {
  for (const id of LN_NAV_PANELS) document.getElementById(id).classList.remove('open');
  lnScrim.classList.remove('open');
  document.getElementById('ln-screenalerts').classList.remove('active');
  document.getElementById('ln-vehicle').classList.remove('active');
  document.getElementById('ln-autosteer').classList.remove('active');
  document.getElementById('ln-network').classList.remove('active');
  document.getElementById('ln-fieldops').classList.remove('active');
  document.getElementById('ln-filemenu').classList.remove('active');
  document.getElementById('ln-tools').classList.remove('active');
  document.getElementById('ln-fieldtools').classList.remove('active');
}
function lnOpen(panelId, navBtnId, onOpen) {
  lnCloseAll();
  document.getElementById(panelId).classList.add('open');
  if (!NO_SCRIM.has(panelId)) lnScrim.classList.add('open'); // transparent light-dismiss
  if (navBtnId) document.getElementById(navBtnId).classList.add('active');
  if (onOpen) onOpen();
}
// Outside tap on the scrim closes the chain and is CONSUMED (stopPropagation keeps the
// map from panning / the background from actuating).
lnScrim.addEventListener('pointerdown', e => { e.stopPropagation(); lnCloseAll(); });
for (const id of LN_NAV_PANELS) document.getElementById(id).addEventListener('pointerdown', e => e.stopPropagation());
document.getElementById('ln-screenalerts').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (document.getElementById('screenalerts').classList.contains('open')) lnCloseAll();
  else lnOpen('screenalerts', 'ln-screenalerts', populateScreenAlerts);
});
document.getElementById('ln-vehicle').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const anyOpen = ['vehtoolhub', 'vehiclecfg', 'toolcfg'].some(id => document.getElementById(id).classList.contains('open'));
  if (anyOpen) lnCloseAll(); else lnOpen('vehtoolhub', 'ln-vehicle', refreshHub);
});
document.getElementById('ln-autosteer').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (document.getElementById('autosteercfg').classList.contains('open')) lnCloseAll();
  else lnOpen('autosteercfg', 'ln-autosteer', () => {
    // Always open collapsed (left pane only), like the native panel.
    asPanel.classList.remove('expanded');
    document.getElementById('as-expand').textContent = '▶ Full';
    populateAutoSteer(true);
  });
});
document.getElementById('ln-network').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (document.getElementById('networkio').classList.contains('open')) lnCloseAll();
  else {
    lnOpen('networkio', 'ln-network', renderNetworkIo);
    nioSerialTick();
    if (!nioSerialPoll) nioSerialPoll = setInterval(nioSerialTick, 2000);
    nioRcTick();
    if (!nioRcPoll) nioRcPoll = setInterval(nioRcTick, 2000);
  }
});
// Route Planner controls: pattern picker, ± steppers, Plan / Clear.
for (const b of document.querySelectorAll('#routeplan .rp-pat[data-pat]'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); rpPattern = +b.dataset.pat; rpRender(); });
document.getElementById('rp-cornerfill').addEventListener('pointerdown', e => { e.stopPropagation(); rpCornerFill = !rpCornerFill; rpRender(); });
wireTabStrip(document.getElementById('routeplan'), 'rp-top');
for (const b of document.querySelectorAll('#routeplan [data-hlstyle]'))
  b.addEventListener('pointerdown', e => {
    e.stopPropagation();
    rpHlStyle = +b.dataset.hlstyle; localStorage.rpHlStyle = rpHlStyle;
    rpRender();
  });
for (const b of document.querySelectorAll('#routeplan [data-hlorder]'))
  b.addEventListener('pointerdown', e => {
    e.stopPropagation();
    rpHlFirst = b.dataset.hlorder === '1'; localStorage.rpHlOrder = rpHlFirst ? '1' : '0';
    rpRender();
  });
document.getElementById('rp-backcut').addEventListener('pointerdown', e => {
  e.stopPropagation();
  rpBackCut = !rpBackCut; localStorage.rpBackCut = rpBackCut ? '1' : '0';
  rpRender();
});
document.getElementById('rp-weave').addEventListener('pointerdown', e => {
  e.stopPropagation();
  rpWeave = !rpWeave; localStorage.rpWeave = rpWeave ? '1' : '0';
  rpRender();
});
document.getElementById('rp-slope').addEventListener('pointerdown', e => {
  e.stopPropagation();
  rpSlope = !rpSlope; localStorage.rpSlope = rpSlope ? '1' : '0';
  rpRender();
});
for (const b of document.querySelectorAll('#routeplan .rp-sb, #obstacles .rp-sb'))
  b.addEventListener('pointerdown', e => {
    e.stopPropagation();
    const d = +b.dataset.d;
    switch (b.dataset.rp) {
      case 'hl': rpHeadland = Math.max(0, Math.min(5, rpHeadland + d)); break;
      case 'skip': rpSkip = Math.max(0, Math.min(8, rpSkip + d)); break;
      case 'block': rpBlock = Math.max(1, Math.min(8, rpBlock + d)); break;
      case 'rowsp': rpRowSp = Math.max(0, Math.min(50, Math.round((rpRowSp + d) * 10) / 10)); localStorage.rpRowSp = rpRowSp; break;
      case 'angle': rpAngle = ((rpAngle + d) % 360 + 360) % 360; break;
      case 'obsw': rpObsW = Math.max(1, Math.min(60, rpObsW + d)); break;
      case 'obsl': rpObsL = Math.max(1, Math.min(60, rpObsL + d)); break;
      case 'obsalarmdist': {
        const cur = (config && config.display && config.display.obstacleAlarmDistanceM) || 10;
        const nv = Math.max(1, Math.min(100, cur + d));
        cfgSend('display.obstacleAlarmDistanceM', nv);
        if (config && config.display) config.display.obstacleAlarmDistanceM = nv;
        break;
      }
    }
    rpRender();
  });
for (const b of document.querySelectorAll('[data-obstype]'))
  b.addEventListener('pointerdown', e => {
    e.stopPropagation();
    rpObsType = b.dataset.obstype;
    // Sensible size presets per type (user can still adjust the steppers).
    if (rpObsType === 'POLE') { rpObsW = 1; rpObsL = 1; }
    else if (rpObsType === 'HOSE') { rpObsW = 1; rpObsL = 20; }
    else { rpObsW = 4; rpObsL = 4; }
    rpRender();
  });
document.getElementById('rp-placeobs').addEventListener('pointerdown', e => { e.stopPropagation(); lnCloseAll(); placeObstacle(); });
document.getElementById('rp-delobs').addEventListener('pointerdown', e => { e.stopPropagation(); lnCloseAll(); deleteObstacle(); });
document.getElementById('rp-obsalarm').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const on = !(config && config.display && config.display.obstacleAlarmEnabled);
  cfgSend('display.obstacleAlarmEnabled', on ? 1 : 0);
  if (config && config.display) config.display.obstacleAlarmEnabled = on;
  rpRender();
});
document.getElementById('rp-splitdraw').addEventListener('pointerdown', e => { e.stopPropagation(); lnCloseAll(); drawSplitLine(); });
document.getElementById('rp-splitclear').addEventListener('pointerdown', e => { e.stopPropagation(); clearSplitLines(); rpBlocksRefresh(); });
document.getElementById('rp-applyblock').addEventListener('pointerdown', e => { e.stopPropagation(); applyBlockPath(); });
document.getElementById('rp-tracksel').addEventListener('change', e => {
  const idx = +e.target.value;
  if (idx >= 0) transport.send('track.select|' + idx);
});
// Plot A–B: two taps make a real AB track (server names it "AB n" and selects
// it), then the plan-along flow fires automatically.
document.getElementById('rp-plotab').addEventListener('pointerdown', e => {
  e.stopPropagation();
  startMapTap({
    hint: 'Tap point A',
    onTap: (ae, an) => {
      startMapTap({
        hint: 'Tap point B',
        onTap: (be, bn) => {
          endMapTap();
          transport.send('track.abFromPoints|' + [ae, an, be, bn].join(','));
          setTimeout(() => document.getElementById('rp-plantrack')
            .dispatchEvent(new PointerEvent('pointerdown', { bubbles: true })), 400);
        },
      });
    },
  });
});
document.getElementById('rp-plantrack').addEventListener('pointerdown', e => {
  e.stopPropagation();
  // Pattern rides along too: the track fixes the heading, Skip/Block still set the row order.
  transport.send('route.planTrack|' + [rpHeadland, rpSkip, rpBlock,
    rpHlStyle, rpHlFirst ? 1 : 0, rpBackCut ? 1 : 0, rpPattern].join(','));
  document.getElementById('rp-stats').textContent = 'Planning along selected track…';
  let tries = 0;
  const poll = () => {
    tries++;
    refreshRoutePlan().then(ok => {
      if (ok) { rpBlocksRefresh(); return; }
      if (tries < 28) { setTimeout(poll, 250); return; }
      document.getElementById('rp-stats').textContent = 'No route — select a track in the Tracks manager first.';
    });
  };
  setTimeout(poll, 250);
});
document.getElementById('rp-plan').addEventListener('pointerdown', e => { e.stopPropagation(); planRoute(); });
document.getElementById('rp-clear').addEventListener('pointerdown', e => { e.stopPropagation(); clearRoute(); });
document.getElementById('rp-steermain').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('route.steerMain'); });
document.getElementById('rp-steerhead').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('route.steerHeadland'); });
document.getElementById('rp-drive').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('route.drive'); });
document.getElementById('rp-stop').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('route.stopDrive'); });
document.getElementById('ln-fieldops').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const anyOpen = ['fieldops', 'fieldsandjobs', 'newfield'].some(id => document.getElementById(id).classList.contains('open'));
  if (anyOpen) lnCloseAll(); else lnOpen('fieldops', 'ln-fieldops', renderFieldOps);
});
document.getElementById('ln-tools').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (document.getElementById('tools').classList.contains('open')) lnCloseAll();
  else lnOpen('tools', 'ln-tools', renderToolsPanel);
});
// Tools fly-out items. Steer Wizard + Log Viewer reuse existing flows; Roll
// Correction is an inert placeholder (matches the native button, which has no
// command bound). The three chart buttons toggle their floating chart cards.
document.getElementById('tl-wizard').addEventListener('pointerdown', e => {
  e.stopPropagation(); lnCloseAll();
  if (typeof openSteerWizard === 'function') openSteerWizard();
});
// Log Viewer is a shortcut to the App Log Viewer (the File menu owns the same panel);
// track which parent opened it so Back returns to the right place.
let logViewerParent = 'filemenu';
document.getElementById('tl-logviewer').addEventListener('pointerdown', e => {
  e.stopPropagation(); logViewerParent = 'tools'; lnOpen('logviewer', 'ln-tools', renderLogViewer);
});
document.getElementById('lv-back').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (logViewerParent === 'tools') lnOpen('tools', 'ln-tools', renderToolsPanel);
  else lnOpen('filemenu', 'ln-filemenu');
});
// Roll Correction → the wizard's roll-cal piece as a standalone chain panel.
document.getElementById('tl-rollcorr').addEventListener('pointerdown', e => {
  e.stopPropagation(); lnOpen('rollcorr', 'ln-tools', renderRollCorr);
});
document.getElementById('rc-back').addEventListener('pointerdown', e => {
  e.stopPropagation(); lnOpen('tools', 'ln-tools', renderToolsPanel);
});
document.getElementById('rc-invert').addEventListener('pointerdown', e => {
  e.stopPropagation(); cfgSend('roll.isRollInvert', cfgGet('roll.isRollInvert') ? '0' : '1');
});
document.getElementById('rc-zero').addEventListener('pointerdown', e => {
  e.stopPropagation(); transport.send('roll.zeroCalibrate');
});
for (const b of document.querySelectorAll('.tl-chartbtn'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); toggleChart(b.dataset.chart); });
for (const b of document.querySelectorAll('.chart-x'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); setChartOpen(b.dataset.chart, false); });
// Field Tools fly-out.
document.getElementById('ln-fieldtools').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const anyOpen = ['fieldtools', 'offsetfix'].some(id => document.getElementById(id).classList.contains('open'));
  if (anyOpen) lnCloseAll(); else lnOpen('fieldtools', 'ln-fieldtools');
});
document.getElementById('ft-deleteapplied').addEventListener('pointerdown', e => {
  e.stopPropagation();
  showConfirm('Delete Applied Area',
    'Delete all applied area coverage? This cannot be undone.',
    () => transport.send('field.deleteApplied'));
});
document.getElementById('ft-offsetfix').addEventListener('pointerdown', e => {
  e.stopPropagation(); lnOpen('offsetfix', 'ln-fieldtools', renderOffsetFix);
});
document.getElementById('of-back').addEventListener('pointerdown', e => {
  e.stopPropagation(); lnOpen('fieldtools', 'ln-fieldtools');
});
document.getElementById('ft-importtracks').addEventListener('pointerdown', e => {
  e.stopPropagation(); lnOpen('importtracks', 'ln-fieldtools', renderImportTracks);
});
document.getElementById('it-back').addEventListener('pointerdown', e => {
  e.stopPropagation(); lnOpen('fieldtools', 'ln-fieldtools');
});
// Recorded Path.
document.getElementById('ft-recpath').addEventListener('pointerdown', e => {
  e.stopPropagation(); lnOpen('recpath', 'ln-fieldtools', renderRecPath);
});
document.getElementById('rp-back').addEventListener('pointerdown', e => {
  e.stopPropagation(); lnOpen('fieldtools', 'ln-fieldtools');
});
document.getElementById('rp-tab-rec').addEventListener('pointerdown', e => { e.stopPropagation(); recPathTab = 0; renderRecPath(); });
document.getElementById('rp-tab-play').addEventListener('pointerdown', e => { e.stopPropagation(); recPathTab = 1; renderRecPath(); });
document.getElementById('rp-start').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('recpath.start'); });
// NOTE: was id "rp-stop", which COLLIDED with the Route Planner's Stop button —
// getElementById bound both handlers to the route panel and this button was dead.
document.getElementById('rec-stop').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('recpath.stop'); });
document.getElementById('rec-stopplay').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('recpath.stopPlay'); });
document.getElementById('rp-save').addEventListener('pointerdown', e => {
  e.stopPropagation(); transport.send('recpath.save|' + document.getElementById('rp-name').value);
});
document.getElementById('rp-playbtn').addEventListener('pointerdown', e => { e.stopPropagation(); rnSend('recpath.play'); });
document.getElementById('rp-resume').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('recpath.cycleResume'); });
document.getElementById('rp-reverse').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('recpath.reverse'); });
// Boundary recording menu.
document.getElementById('ft-boundary').addEventListener('pointerdown', e => {
  e.stopPropagation(); transport.send('boundary.refresh'); lnOpen('boundarymenu', 'ln-fieldtools', renderBoundaryMenu);
});
document.getElementById('ft-routeplan').addEventListener('pointerdown', e => { e.stopPropagation(); openRoutePlanner(); });
document.getElementById('ft-ratecontrol').addEventListener('pointerdown', e => { e.stopPropagation(); rtOpen(); });

// ---- Rate Control (native AOG_RC port) -------------------------------------
// Products A-E on RC hardware modules. Status polls /api/ratecontrol at 1 Hz
// while the panel is open; edits go out as rate.set|idx,key,value (Tier-2).
let rtProducts = [], rtSel = 0, rtPlane = false, rtPoll = null, rtCatalog = [], rtTool = '', rtSensorCounts = {}, rtStatus = null;
// ── Rate readout (on-map) ───────────────────────────────────────────────────
// The small always-there panel the separate RateController app used to give
// you: what is going on now, against what was asked for. Polls on its own (the
// Rate Control panel is usually closed while driving) and only when the hitched
// tool actually meters product, so it never clutters the map behind harrows.
// Mode: 0 off, 1 current, 2 target, 3 both — kept client-side in localStorage
// because it is a per-operator display preference, not machine configuration.
let rhMode = parseInt(localStorage.rateHud != null ? localStorage.rateHud : '3');
if (!Number.isFinite(rhMode)) rhMode = 3;
let rhPoll = null;
function rhSetMode(m) {
  rhMode = m;
  localStorage.rateHud = String(m);
  rhTick();
}
function rhRender(d) {
  const hud = document.getElementById('rate-hud');
  if (!hud) return;
  const enabled = ((d && d.products) || []).filter(p => p.enabled);
  // Hidden unless this tool meters product. Stays up with no enabled channel
  // (muted hint instead) — it hosts the switchbox + Rate Control buttons.
  if (!rhMode || !d || !d.useRateControl) { hud.classList.remove('on'); return; }
  hud.classList.add('on');
  const swBtn = document.getElementById('rh-swb');
  if (swBtn) swBtn.classList.toggle('on', !!(d.switchbox && d.switchbox.onScreen));
  const host = document.getElementById('rh-rows');
  host.innerHTML = '';
  if (!enabled.length) {
    const n = document.createElement('div');
    n.className = 'rh-none';
    n.textContent = 'no product enabled';
    host.appendChild(n);
    return;
  }
  for (const p of enabled) {
    const row = document.createElement('div');
    row.className = 'rh-row';
    const name = document.createElement('span');
    name.className = 'rh-name';
    name.textContent = p.name || '';
    const vals = document.createElement('span');
    const actual = Number(p.actualRate || 0), target = Number(p.targetRate || 0);
    if (rhMode === 1 || rhMode === 3) {
      const v = document.createElement('span');
      v.className = 'rh-val';
      v.textContent = actual.toFixed(actual < 10 ? 1 : 0);
      vals.appendChild(v);
    }
    if (rhMode === 3) vals.appendChild(document.createTextNode(' '));
    if (rhMode === 2 || rhMode === 3) {
      const t = document.createElement('span');
      t.className = rhMode === 2 ? 'rh-val' : 'rh-tgt';
      t.textContent = (rhMode === 3 ? '/ ' : '') + target.toFixed(target < 10 ? 1 : 0);
      vals.appendChild(t);
    }
    const u = document.createElement('span');
    u.className = 'rh-units';
    u.textContent = ' ' + (p.units || '');
    vals.appendChild(u);
    // Flag a rate that is off target while actually flowing — the number being
    // there is not the same as it being right.
    if (rhMode !== 2 && target > 0 && actual > 0 && Math.abs(actual - target) / target > 0.10)
      row.classList.add('rh-off');
    row.appendChild(name);
    row.appendChild(vals);
    host.appendChild(row);
    // Volume remaining, just under the rate/target. "~ … (est)" for a manual
    // (un-metered) product; tap "+" to top up. Status carries per-product tank + tankSize.
    if (p.tankSize > 0 || p.tank > 0) {
      const est = !p.connected;
      const pct = p.tankSize > 0 ? p.tank / p.tankSize : 1;
      const unit = (p.units || 'L').split('/')[0];
      const tk = document.createElement('div');
      tk.className = 'rh-tank' + (pct <= 0.10 ? ' low' : pct <= 0.25 ? ' warn' : '');
      const lbl = document.createElement('span');
      lbl.textContent = (est ? '~' : '') + Math.round(p.tank) + ' ' + unit + ' left' + (est ? ' (est)' : '');
      const add = document.createElement('span');
      add.className = 'rh-topup'; add.textContent = '+'; add.title = 'Top up tank';
      const pi = (d.products || []).indexOf(p);
      add.addEventListener('pointerdown', ev => {
        ev.stopPropagation();
        askKeypad({ title: 'Top up ' + (p.name || 'tank'), numLabel: 'Amount to ADD', units: ['L', 'kg'] }, r => {
          if (!r || !(r.value > 0)) return;
          swbSend('rate.set|' + pi + ',tankAdd,' + r.value);
        });
      });
      tk.appendChild(lbl); tk.appendChild(add);
      host.appendChild(tk);
    }
    if (p.binEmpty) {
      const b = document.createElement('div');
      b.className = 'rh-bin';
      b.textContent = 'BIN EMPTY';
      host.appendChild(b);
    }
  }
}
let rcLive = null;   // latest /api/ratecontrol, shared by HUD + on-screen switchbox
function rhTick() {
  fetch('/api/ratecontrol').then(r => r.json()).then(d => {
    rcLive = d;
    if (rhMode) rhRender(d);
    else { const h = document.getElementById('rate-hud'); if (h) h.classList.remove('on'); }
    renderSwitchbox(d);
    rcAlmCheck(d);
  }).catch(() => {});
}

// ── Rate drop-off alarm ─────────────────────────────────────────────────────
// A rate module an ENABLED product needs, or the physical switchbox, stops
// answering AFTER having been seen this session → banner + beep. Rides the
// HUD's 1 Hz poll (always running); the server's heard windows (10 s module,
// 4 s box) debounce single lost packets. Screen/sound are independent
// per-device toggles (Network IO panel). Tap the banner to acknowledge —
// beep stops, banner dims; any CHANGE in the offline set re-arms.
let rcAlmScreen = localStorage.rcAlmScreen !== '0';
let rcAlmSound = localStorage.rcAlmSound !== '0';
const rcAlmSeen = new Set();
let rcAlmSbSeen = false;
let rcAlmActive = [];
let rcAlmAck = false;
let rcAlmBeepTimer = null;
let rcAlmCtx = null;
function rcAlmBeep() {
  if (!rcAlmSound) return;
  try {
    rcAlmCtx = rcAlmCtx || new (window.AudioContext || window.webkitAudioContext)();
    if (rcAlmCtx.state === 'suspended') rcAlmCtx.resume();
    const t0 = rcAlmCtx.currentTime;
    for (let i = 0; i < 3; i++) {
      const o = rcAlmCtx.createOscillator(), g = rcAlmCtx.createGain();
      o.type = 'square'; o.frequency.value = 880;
      g.gain.setValueAtTime(0.0001, t0 + i * 0.25);
      g.gain.exponentialRampToValueAtTime(0.28, t0 + i * 0.25 + 0.02);
      g.gain.exponentialRampToValueAtTime(0.0001, t0 + i * 0.25 + 0.18);
      o.connect(g); g.connect(rcAlmCtx.destination);
      o.start(t0 + i * 0.25); o.stop(t0 + i * 0.25 + 0.2);
    }
  } catch {}
}
const rcAlmT0 = Date.now();  // page start — boot grace for never-seen modules
function rcAlmCheck(d) {
  if (!d || !d.useRateControl) { rcAlmSet([]); return; }
  const heard = new Set(d.modulesHeard || []);
  for (const id of heard) rcAlmSeen.add(id);
  const p = d.switchbox && d.switchbox.physical;
  const offline = [];
  // A module is EXPECTED when an enabled product uses it. Once seen, absence
  // alarms immediately; never seen, it still alarms after a 30 s boot grace
  // (module powers up slower than the app) — so a dead module is caught at
  // morning start, not only after a mid-session drop.
  const graceOver = Date.now() - rcAlmT0 > 30000;
  const need = new Set((d.products || []).filter(x => x.enabled).map(x => x.moduleId));
  for (const id of need)
    if (!heard.has(id) && (rcAlmSeen.has(id) || graceOver)) offline.push('RATE MODULE ' + id);
  // The switchbox is expected per-tool (persisted server-side; auto-set the
  // first time a box is ever seen on the tool, clearable in Network IO).
  if (p && p.expected && !p.connected && graceOver) offline.push('SWITCHBOX');
  else if (p && p.connected) rcAlmSbSeen = true;
  if (rcAlmSbSeen && !(p && p.connected) && !offline.includes('SWITCHBOX')) offline.push('SWITCHBOX');
  rcAlmSet(offline);
}
function rcAlmSet(offline) {
  const el = document.getElementById('rc-alarm');
  if (!el) return;
  if (offline.join(',') !== rcAlmActive.join(',')) rcAlmAck = false;
  rcAlmActive = offline;
  el.classList.toggle('on', offline.length > 0 && rcAlmScreen);
  el.classList.toggle('ack', rcAlmAck);
  if (offline.length)
    document.getElementById('rc-alarm-txt').textContent =
      offline.join(' + ') + ' OFFLINE' + (rcAlmAck ? '' : ' — tap to silence');
  if (offline.length > 0 && !rcAlmAck) {
    if (!rcAlmBeepTimer) { rcAlmBeep(); rcAlmBeepTimer = setInterval(rcAlmBeep, 4000); }
  } else if (rcAlmBeepTimer) { clearInterval(rcAlmBeepTimer); rcAlmBeepTimer = null; }
}
document.getElementById('rc-alarm').addEventListener('pointerdown', e => {
  e.stopPropagation();
  rcAlmAck = true;
  rcAlmSet(rcAlmActive);
});

// ── On-screen switchbox ─────────────────────────────────────────────────────
// The native RC floating switch panel: master valve, primed start, auto rate,
// rate nudge, and the section GROUP switches (sections allocated to the same
// switch flip together — the native zone idea).
function swbGroups(d) {
  const alloc = (d.switchbox && d.switchbox.sectionSwitch) || [];
  const secs = (tick && tick.sections) || [];
  const groups = new Map();
  for (let i = 0; i < secs.length; i++) {
    const sw = i < alloc.length ? alloc[i] : (i < 8 ? i : 7);
    if (sw < 0) continue;
    if (!groups.has(sw)) groups.set(sw, []);
    groups.get(sw).push(i);
  }
  return [...groups.entries()].sort((a, b) => a[0] - b[0]);
}
// Deliberate actuation/config sends take the seat first. Every rate.* id is Tier-2;
// a bare transport.send from an Observer tab was dropped by the hub while the panel
// still printed "Module config sent." / "ID N assigned." — a false success. Routing the
// Rate Control / Module Setup / Switches sends through here makes those messages true.
function swbSend(cmd) {
  if (!iHoldControl) transport.send('control.takeover|Browser');
  transport.send(cmd);
}
function renderSwitchbox(d) {
  const box = document.getElementById('rc-switchbox');
  if (!box) return;
  const show = d && d.useRateControl && d.switchbox && d.switchbox.onScreen;
  box.classList.toggle('on', !!show);
  if (!show) return;
  const mst = document.getElementById('swb-mst');
  mst.classList.toggle('on', !!d.effectiveMaster);
  mst.disabled = d.switchbox.masterMode === 2;      // override: always on
  mst.title = mst.disabled ? 'Master override is on (Switches tab)' : 'Master valve';
  const prm = document.getElementById('swb-prm');
  prm.classList.toggle('on', !!(d.primed && d.primed.active));
  prm.disabled = d.switchbox.switchType === 1;      // maintained: no primed start
  prm.textContent = (d.primed && d.primed.active) ? 'PRM ' + d.primed.remaining : 'PRM';
  const autoR = document.getElementById('swb-autorate');
  autoR.classList.toggle('on', !!d.switchbox.autoRate);
  // Auto sections = the section auto master (same thing the physical box's
  // AUTO toggle and the right-nav button drive); live state rides the tick.
  const autoS = document.getElementById('swb-autosec');
  autoS.classList.toggle('on', !!(tick && tick.op && tick.op.sectionAuto));
  // section group switches
  const host = document.getElementById('swb-secs');
  const groups = swbGroups(d);
  if (host.childElementCount !== groups.length) {
    host.innerHTML = '';
    for (const [sw] of groups) {
      const b = document.createElement('button');
      b.dataset.sw = sw;
      b.addEventListener('pointerdown', e => {
        e.stopPropagation();
        const gs = swbGroups(rcLive).find(g => g[0] === sw);
        if (!gs) return;
        const secs = (tick && tick.sections) || [];
        // tick.sections is the numeric colour code per section, not an object.
        const isOn = i => { const c = Number(secs[i] ?? 0); return c >= 1 && c <= 4; };
        const allOn = gs[1].every(isOn);
        // one press moves the whole group to the same state
        for (const i of gs[1]) {
          if (isOn(i) === allOn) swbSend('section.toggle|' + i);
        }
      });
      host.appendChild(b);
    }
  }
  const secs = (tick && tick.sections) || [];
  // The switch shows only the SWITCH POSITION (Auto / Manual / Off) — what it's
  // set to, not whether it's spraying right now (the bottom section bar + tractor
  // icon show live application). Derive the button state from the colour code:
  // 0=Off, 1=Manual-On, 2..5=any Auto state.
  const btnState = c => c === 0 ? 'off' : c === 1 ? 'manual' : 'auto';
  let bi = 0;
  for (const [sw, members] of groups) {
    const b = host.children[bi++];
    if (!b) break;
    const states = members.map(i => btnState(Number(secs[i] ?? 0)));
    const uniq = [...new Set(states)];
    const st = uniq.length === 1 ? uniq[0] : 'mix';
    b.textContent = 'S' + (sw + 1) + (members.length > 1 ? ' (' + members.length + ')' : '');
    b.title = 'sections ' + members.map(i => i + 1).join(', ') + ' — ' +
      (st === 'off' ? 'OFF' : st === 'manual' ? 'Manual ON' : st === 'auto' ? 'AUTO' : 'mixed');
    b.classList.toggle('auto', st === 'auto');
    b.classList.toggle('manual', st === 'manual');
    b.classList.toggle('off', st === 'off');
    b.classList.toggle('mix', st === 'mix');
    b.classList.remove('on', 'armed');   // no live-spraying colour on the switch itself
  }
}
document.getElementById('swb-mst').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (rcLive) swbSend('rate.master|' + (rcLive.masterOn ? 0 : 1));
});
document.getElementById('swb-prm').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (rcLive) swbSend('rate.primed|' + (rcLive.primed && rcLive.primed.active ? 0 : 1));
});
document.getElementById('swb-autorate').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (rcLive) swbSend('rate.sw|autoRate,' + (rcLive.switchbox.autoRate ? 0 : 1));
});
document.getElementById('swb-autosec').addEventListener('pointerdown', e => {
  e.stopPropagation();
  swbSend('section.master');
});
// Drag-by-grip for floating panels; position persists across sessions under
// the given localStorage key. Default (never moved) is the panel's CSS dock.
function makeFloatDrag(boxId, gripId, storeKey) {
  const box = document.getElementById(boxId);
  const grip = document.getElementById(gripId);
  if (!box || !grip) return;
  const clamp = (l, t) => {
    const w = box.offsetWidth || 300, h = box.offsetHeight || 72;
    return [Math.min(Math.max(0, l), Math.max(0, innerWidth - w)),
            Math.min(Math.max(0, t), Math.max(0, innerHeight - h))];
  };
  const place = (l, t) => {
    [l, t] = clamp(l, t);
    box.classList.add('moved');
    box.style.left = l + 'px';
    box.style.top = t + 'px';
  };
  try {
    const p = JSON.parse(localStorage[storeKey] || 'null');
    if (p && typeof p.l === 'number') place(p.l, p.t);
  } catch {}
  let drag = null;
  grip.addEventListener('pointerdown', e => {
    e.stopPropagation(); e.preventDefault();
    const r = box.getBoundingClientRect();
    drag = { dx: e.clientX - r.left, dy: e.clientY - r.top };
    grip.setPointerCapture(e.pointerId);
  });
  grip.addEventListener('pointermove', e => {
    if (!drag) return;
    place(e.clientX - drag.dx, e.clientY - drag.dy);
  });
  grip.addEventListener('pointerup', () => {
    if (!drag) return;
    drag = null;
    const r = box.getBoundingClientRect();
    localStorage[storeKey] = JSON.stringify({ l: r.left, t: r.top });
  });
}
makeFloatDrag('rc-switchbox', 'swb-grip', 'swbPos');
makeFloatDrag('rate-hud', 'rh-grip', 'rhPos');
for (const [id, dir] of [['swb-dn', -5], ['swb-up', 5]])
  document.getElementById(id).addEventListener('pointerdown', e => {
    e.stopPropagation();
    if (!rcLive) return;
    rcLive.products.forEach((p, i) => { if (p.enabled) swbSend('rate.bump|' + i + ',' + dir); });
  });
{
  // Header buttons replace the old tap-anywhere-to-open: the HUD is draggable
  // now, so opening lives on its own button and SW toggles the on-screen box.
  const hud = document.getElementById('rate-hud');
  if (hud) hud.addEventListener('pointerdown', e => e.stopPropagation());
  const menu = document.getElementById('rh-menu');
  if (menu) menu.addEventListener('pointerdown', e => { e.stopPropagation(); rtOpen(); });
  const swb = document.getElementById('rh-swb');
  if (swb) swb.addEventListener('pointerdown', e => {
    e.stopPropagation();
    if (rcLive && rcLive.switchbox)
      swbSend('rate.sw|onScreen,' + (rcLive.switchbox.onScreen ? 0 : 1));
    setTimeout(rhTick, 300);
  });
  rhPoll = setInterval(rhTick, 1000);
  rhTick();
}

// ── Module setup ────────────────────────────────────────────────────────────
// Pins, module flags and valve tuning: the settings the module keeps in EEPROM,
// which nothing else can write (its own web page is only WiFi + a master
// button). Saved against the tool, so a replaced module can be recommissioned
// here rather than from a Windows machine that no longer exists in the cab.
let msData = null, msMod = 0, msSen = 0;
function msOpen() {
  lnOpen('modulesetup', 'ln-fieldtools', null);
  msRefresh();
}
function msRefresh() {
  fetch('/api/ratemodules').then(r => r.json()).then(d => { msData = d; msRender(); }).catch(() => {});
}
function msCurrent() {
  if (!msData || !msData.modules) return null;
  const m = msData.modules.find(x => x.moduleId === msMod) || msData.modules[0];
  if (!m) return null;
  const s = (m.sensors || []).find(x => x.sensorId === msSen) || (m.sensors || [])[0];
  return { m, s };
}
function msRender() {
  if (!msData) return;
  document.getElementById('ms-tool').textContent = msData.tool || '(none)';
  const heard = msData.modulesHeard || [];
  document.getElementById('ms-heard').textContent =
    heard.length ? 'heard: ' + heard.join(', ') : 'no modules heard';
  // module / sensor selectors
  const modSel = document.getElementById('ms-mod');
  if (modSel.dataset.n !== String((msData.modules || []).length)) {
    modSel.innerHTML = '';
    for (const m of (msData.modules || [])) {
      const o = document.createElement('option');
      o.value = m.moduleId; o.textContent = m.moduleId; modSel.appendChild(o);
    }
    modSel.dataset.n = String((msData.modules || []).length);
  }
  modSel.value = String(msMod);
  const cur = msCurrent();
  if (!cur) return;
  msMod = cur.m.moduleId;
  const senSel = document.getElementById('ms-sen');
  const sensors = cur.m.sensors || [];
  if (senSel.dataset.n !== String(sensors.length)) {
    senSel.innerHTML = '';
    for (const s of sensors) {
      const o = document.createElement('option');
      o.value = s.sensorId; o.textContent = s.sensorId; senSel.appendChild(o);
    }
    senSel.dataset.n = String(sensors.length);
  }
  if (cur.s) { msSen = cur.s.sensorId; senSel.value = String(msSen); }
  // A channel pointing past the module's sensor count reaches nothing, and the
  // symptom (no flow) is identical to a wiring fault — so say which it is.
  const senWarn = document.getElementById('ms-senwarn');
  if (senWarn) {
    const n = (cur.m.cfg && cur.m.cfg.sensorCount) || 0;
    const bad = n > 0 && msSen >= n;
    senWarn.hidden = !bad;
    if (bad) senWarn.textContent =
      'Sensor ' + msSen + ' is past this module’s sensor count of ' + n +
      ' — the module ignores it. Sensors are numbered from 0.';
  }
  const val = k => {
    const [grp, name] = k.split('.');
    if (grp === 'cfg') {
      if (name.startsWith('relay')) return (cur.m.relayPins || [])[parseInt(name.slice(5))];
      return cur.m.cfg ? cur.m.cfg[name] : undefined;
    }
    if (!cur.s) return undefined;
    return grp === 'pins' ? cur.s.pins[name] : cur.s.ctl[name];
  };
  // [data-k] matters: the subnet octet boxes share .ms-num for styling but are
  // not module-setup fields, and reading a key off them threw here — which took
  // out the selects and toggles rendered below.
  for (const inp of document.querySelectorAll('#modulesetup .ms-num[data-k]')) {
    const v = val(inp.dataset.k);
    if (v != null && document.activeElement !== inp) inp.value = v;
  }
  for (const sel of document.querySelectorAll('#modulesetup .ms-sel')) {
    const v = val(sel.dataset.k);
    if (v != null && document.activeElement !== sel) sel.value = String(v);
  }
  // Valve wiring is is3Wire alone. The invert-flow bit next to it is direction,
  // not wiring — the firmware uses it to decide which way is "more flow".
  const vm = document.getElementById('ms-valvemode');
  if (vm && cur.m.cfg && document.activeElement !== vm) vm.value = cur.m.cfg.is3Wire ? '3' : '2';
  const bd = document.getElementById('ms-board');
  if (bd && document.activeElement !== bd) bd.value = cur.m.board || 'esp32';
  const ph = document.getElementById('ms-pinhelp');
  if (ph) ph.textContent = MS_BOARD_PINS[cur.m.board] || MS_BOARD_PINS.esp32;
  msRenderRelays(cur.m);
  for (const b of document.querySelectorAll('#modulesetup .ms-tgl')) {
    const on = !!val(b.dataset.k);
    b.classList.toggle('active', on);
    b.textContent = on ? 'On' : 'Off';
  }
}
// Relay functions. The number box means different things per type — a section
// for Section/Invert section, a switch for Switch, nothing for the rest — so it
// is labelled and enabled from the type rather than always sitting there.
const MS_RLY_SECTION = [0, 4];        // types that address a section
const MS_RLY_SWITCH = 10;             // the type that addresses a switch
function msRenderRelays(m) {
  const relays = m.relays || [];
  for (const sel of document.querySelectorAll('#modulesetup .rly-type')) {
    const r = relays.find(x => x.id === parseInt(sel.dataset.r));
    if (!r) continue;
    if (document.activeElement !== sel) sel.value = String(r.type);
    const num = document.querySelector('#modulesetup .rly-num[data-r="' + r.id + '"]');
    if (!num) continue;
    const wantsSection = MS_RLY_SECTION.includes(r.type);
    const wantsSwitch = r.type === MS_RLY_SWITCH;
    num.hidden = !(wantsSection || wantsSwitch);
    num.title = wantsSwitch ? 'Switch number (0-15)' : 'Section number, or -1 for none';
    if (document.activeElement !== num) num.value = wantsSwitch ? r.switch : r.section;
  }
  const fm = document.getElementById('ms-fmmode');
  if (fm && document.activeElement !== fm) fm.value = String(m.flowMasterMode || 0);
  const note = document.getElementById('ms-fmnote');
  if (note) {
    const n = relays.filter(r => r.type === 14).length;      // FlowMaster
    note.textContent =
      m.flowMasterMode === 1
        ? (n === 1
            ? 'The module will drive relay ' + (relays.find(r => r.type === 14).id + 1) +
              ' as a pair. Needs a board with an H-bridge, such as the RC15.'
            : n + ' relays are set to FlowMaster — this mode needs exactly one, so nothing is sent and the valve will not close.')
      : m.flowMasterMode === 2
        ? 'Set one relay to FlowMaster and another to Invert FlowMaster: one powers open, the other powers close. For boards without an H-bridge, such as the RC11-2.'
      : 'One relay signals; the valve runs on its own supply and manages open/close itself. The usual choice.';
    note.classList.toggle('rt-warn', m.flowMasterMode === 1 && n !== 1);
  }
  // Seeing the computed word is the only way to check a relay map short of
  // watching the outputs click.
  const live = document.getElementById('ms-rlylive');
  if (live) {
    const w = m.liveRelays || 0;
    const on = [];
    for (let i = 0; i < 16; i++) if (w & (1 << i)) on.push('R' + (i + 1));
    live.textContent = 'On now: ' + (on.length ? on.join(' ') : 'none') +
      (m.liveValveIndex !== 255 ? ' · 2-wire valve on R' + (m.liveValveIndex + 1) : '');
  }
}
// Factory pin maps, shown so the operator can see what the board should be
// wired as without loading defaults over their own values.
const MS_BOARD_PINS = {
  esp32:  'ESP32 (RC15): sensor 0 flow 17, dir 32, PWM 33. Sensor 1 flow 16, dir 25, PWM 26. Work and pressure unassigned; relays via PCA9685.',
  teensy: 'Teensy 4.1 (RC11-2): sensor 0 flow 28, dir 37, PWM 36. Sensor 1 flow 29, dir 14, PWM 15. Work 30, pressure 40; relays 1-8 on pins 8-12, 25-27.',
  nano:   'Nano (RC12-3): sensor 0 flow 3, dir 4, PWM 5. Sensor 1 flow 2, dir 6, PWM 9. Work 15 (A1), pressure 14 (A0); relays via MCP23017. On a Nano, A0-A7 are pins 14-21.',
};
function msSend(key, value) {
  swbSend('rate.modSet|' + msMod + ',' + msSen + ',' + key + ',' + value);
  setTimeout(msRefresh, 150);
}
function msStatus(t) {
  const el = document.getElementById('ms-status');
  el.textContent = t;
  setTimeout(() => { if (el.textContent === t) el.textContent = ''; }, 4000);
}
document.getElementById('rt-modsetup').addEventListener('pointerdown', e => { e.stopPropagation(); msOpen(); });
wireTabStrip(document.getElementById('modulesetup'), 'ms-top');
document.getElementById('ms-valvemode').addEventListener('change', e => {
  msSend('cfg.is3Wire', e.target.value === '3' ? '1' : '0');
});
for (const sel of document.querySelectorAll('#modulesetup .ms-sel'))
  sel.addEventListener('change', () => msSend(sel.dataset.k, sel.value));
for (const sel of document.querySelectorAll('#modulesetup .rly-type'))
  sel.addEventListener('change', () => msSend('rly' + sel.dataset.r + '.type', sel.value));
for (const inp of document.querySelectorAll('#modulesetup .rly-num'))
  inp.addEventListener('change', () => {
    const sel = document.querySelector('#modulesetup .rly-type[data-r="' + inp.dataset.r + '"]');
    const isSwitch = sel && parseInt(sel.value) === MS_RLY_SWITCH;
    msSend('rly' + inp.dataset.r + (isSwitch ? '.switch' : '.section'), inp.value);
  });
document.getElementById('ms-fmmode').addEventListener('change', e => msSend('cfg.flowMasterMode', e.target.value));
document.getElementById('ms-rlyrenum').addEventListener('pointerdown', e => {
  e.stopPropagation();
  showConfirm('Renumber sections',
    'Give every section relay on module ' + msMod + ' a consecutive number, in relay order? ' +
    'Relays past the last section are set to none. Other relay types are left alone.',
    () => { swbSend('rate.relayRenumber|' + msMod + ',0');
            setTimeout(msRefresh, 300); msStatus('Sections renumbered.'); });
});
document.getElementById('ms-rlyreset').addEventListener('pointerdown', e => {
  e.stopPropagation();
  showConfirm('Reset relays',
    'Put all 16 relays on module ' + msMod + ' back to driving their own section? ' +
    'Any master, bypass, tram or hydraulic assignments are lost.',
    () => { swbSend('rate.relayReset|' + msMod);
            setTimeout(msRefresh, 300); msStatus('Relays reset to sections.'); });
});
document.getElementById('ms-board').addEventListener('change', e => msSend('cfg.board', e.target.value));
// Nothing selects a module id that has no saved entry yet, so a board cannot be
// prepared before it is given that id without this.
document.getElementById('ms-addmod').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const used = ((msData && msData.modules) || []).map(m => m.moduleId);
  let id = 0;
  while (used.includes(id) && id < 8) id++;
  if (id > 7) { msStatus('All eight module ids already have settings.'); return; }
  swbSend('rate.modAdd|' + id);
  msMod = id;
  setTimeout(() => { msRefresh(); msStatus('Module ' + id + ' added — load its board defaults on Commission.'); }, 300);
});
document.getElementById('ms-pushsubnet').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const o = ['ms-ip0','ms-ip1','ms-ip2'].map(id => parseInt(document.getElementById(id).value));
  if (o.some(n => !Number.isFinite(n) || n < 0 || n > 255)) { msStatus('Subnet octets must be 0-255.'); return; }
  showConfirm('Set wired subnet',
    'Module ' + msMod + ' will reboot onto ' + o.join('.') + '.' + (50 + msMod) +
    ' and leave the current wired network. Its WiFi connection is unaffected. Continue?',
    () => { swbSend('rate.modSubnet|' + msMod + ',' + o.join(','));
            msStatus('Subnet sent — module rebooting onto ' + o.join('.') + '.' + (50 + msMod)); });
});
document.getElementById('ms-defaults').addEventListener('pointerdown', e => {
  e.stopPropagation();
  // Staged locally only — the operator reviews the values and presses send.
  // Nothing reaches the module until then.
  const board = document.getElementById('ms-board').value || 'esp32';
  const label = document.getElementById('ms-board').selectedOptions[0].textContent;
  showConfirm('Load board defaults',
    'Fill module ' + msMod + "'s pins, relay map, flags and valve tuning with the factory " +
    'defaults for ' + label + '? This only fills the form — nothing is sent until you ' +
    'press a Send button.',
    () => { swbSend('rate.modDefaults|' + msMod + ',' + board); setTimeout(msRefresh, 400);
            msStatus(label + ' defaults loaded — review, then Send.'); });
});
// Rate control entry points on Tool config -> Machine -> Rate Control.
{
  const openRate = document.getElementById('tcm-openrate');
  if (openRate) openRate.addEventListener('pointerdown', e => { e.stopPropagation(); rtOpen(); });
  const openMod = document.getElementById('tcm-openmod');
  if (openMod) openMod.addEventListener('pointerdown', e => { e.stopPropagation(); msOpen(); });
  const hud = document.getElementById('tcm-hudmode');
  if (hud) {
    hud.value = String(rhMode);
    hud.addEventListener('change', ev => rhSetMode(parseInt(ev.target.value) || 0));
  }
}
// Rate modules in the status-bar Modules popup. They speak a different UDP plane
// from GPS/IMU/AutoSteer/Machine, so the host's module scan never sees them and
// this has to come from the rate service. Polled slowly — it is a presence
// indicator, not telemetry.
setInterval(() => {
  const row = document.getElementById('sb-rcrow');
  if (!row) return;
  fetch('/api/ratemodules').then(r => r.json()).then(d => {
    if (!d.useRateControl) { row.hidden = true; return; }   // not a metering tool
    row.hidden = false;
    const heard = d.modulesHeard || [];
    const dot = document.getElementById('sb-rc');
    const det = document.getElementById('sb-rc-d');
    if (dot) dot.style.background = heard.length ? '#2ecc71' : '#e74c3c';
    if (det) det.textContent = heard.length ? 'id ' + heard.join(', ') : 'none';
  }).catch(() => {});
}, 3000);

// Keep the machine tab's status line current while it is open.
setInterval(() => {
  const el = document.getElementById('tcm-heard');
  const panel = document.getElementById('toolcfg');
  if (!el || !panel || !panel.classList.contains('open')) return;
  fetch('/api/ratemodules').then(r => r.json()).then(d => {
    const h = d.modulesHeard || [];
    el.textContent = h.length ? h.join(', ') : 'none';
  }).catch(() => {});
}, 2000);
document.getElementById('ms-back').addEventListener('pointerdown', e => { e.stopPropagation(); rtOpen(); });
document.getElementById('ms-mod').addEventListener('change', e => { msMod = parseInt(e.target.value) || 0; msRender(); });
document.getElementById('ms-sen').addEventListener('change', e => { msSen = parseInt(e.target.value) || 0; msRender(); });
for (const inp of document.querySelectorAll('#modulesetup .ms-num[data-k]'))
  inp.addEventListener('change', () => {
    const v = parseInt(inp.value);
    if (Number.isFinite(v)) msSend(inp.dataset.k, v);
  });
for (const b of document.querySelectorAll('#modulesetup .ms-tgl'))
  b.addEventListener('pointerdown', e => {
    e.stopPropagation();
    msSend(b.dataset.k, b.classList.contains('active') ? '0' : '1');
  });
// Relay pins ride in the module-config packet, so the Relays tab pushes the
// same thing the Module tab does.
for (const id of ['ms-pushcfg', 'ms-pushcfg2'])
  document.getElementById(id).addEventListener('pointerdown', e => {
    e.stopPropagation(); swbSend('rate.modPush|' + msMod + ',' + msSen + ',cfg'); msStatus('Module config sent.');
  });
document.getElementById('ms-pushpins').addEventListener('pointerdown', e => {
  e.stopPropagation(); swbSend('rate.modPush|' + msMod + ',' + msSen + ',pins');
  msStatus('Pins sent — the module restarts if any changed.');
});
document.getElementById('ms-pushctl').addEventListener('pointerdown', e => {
  e.stopPropagation(); swbSend('rate.modPush|' + msMod + ',' + msSen + ',ctl'); msStatus('Valve tuning sent.');
});
document.getElementById('ms-pushall').addEventListener('pointerdown', e => {
  e.stopPropagation(); swbSend('rate.modPush|' + msMod + ',' + msSen + ',all');
  msStatus('Full setup sent to module ' + msMod + '.');
});
// ID assignment is unconditional at the module end: every board listening adopts
// it. Guarded here because getting it wrong gives two modules the same id and
// neither works.
document.getElementById('ms-assignid').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const heard = (msData && msData.modulesHeard) || [];
  if (heard.length > 1) {
    msStatus('Refused: ' + heard.length + ' modules are answering. Disconnect all but one.');
    return;
  }
  showConfirm('Assign module ID',
    'EVERY rate module connected right now will take ID ' + msMod +
    '. Only do this with a single board connected.',
    () => { swbSend('rate.modAssignId|' + msMod); msStatus('ID ' + msMod + ' assigned.'); });
});
function rtOpen() {
  lnOpen('ratecontrol', 'ln-fieldtools', rtRender);
  rtRefresh();
  if (rtPoll) clearInterval(rtPoll);
  rtPoll = setInterval(() => {
    if (!document.getElementById('ratecontrol').classList.contains('open')) { clearInterval(rtPoll); rtPoll = null; return; }
    rtRefresh();
  }, 1000);
}
function rtRefresh() {
  fetch('/api/ratecontrol').then(r => r.json()).then(d => {
    rtProducts = (d && d.products) || [];
    rtPlane = !!(d && d.plane);
    rtCatalog = (d && d.catalog) || [];
    rtTool = (d && d.tool) || '';
    rtSensorCounts = (d && d.moduleSensorCounts) || {};
    rtStatus = d;
    rtRenderSwitches();
    rtRender();
  }).catch(() => {});
}
function rtSend(key, value) { swbSend('rate.set|' + rtSel + ',' + key + ',' + value); setTimeout(rtRefresh, 250); }
// Rate up/down nudges the selected channel's target, and Reset returns it to
// the catalogue rate — the "what was it supposed to be" after a few nudges.
// (Master moved to the on-screen switchbox; swbSend owns it.)
document.getElementById('rt-rateup').addEventListener('pointerdown', e => {
  e.stopPropagation(); swbSend('rate.bump|' + rtSel + ',5'); setTimeout(rtRefresh, 250);
});
document.getElementById('rt-ratedn').addEventListener('pointerdown', e => {
  e.stopPropagation(); swbSend('rate.bump|' + rtSel + ',-5'); setTimeout(rtRefresh, 250);
});
document.getElementById('rt-ratereset').addEventListener('pointerdown', e => {
  e.stopPropagation(); swbSend('rate.rateReset|' + rtSel); setTimeout(rtRefresh, 250);
});
document.getElementById('rt-hudmode').addEventListener('change', e => {
  rhSetMode(parseInt(e.target.value) || 0);
});
document.getElementById('rt-catsel').addEventListener('change', e => {
  const name = e.target.value;
  if (!name) return;
  // Loads identity + usual rate only; the channel's meter cal and module/sensor
  // stay put, because those describe this implement, not the product.
  swbSend('rate.assign|' + rtSel + ',' + name);
  setTimeout(rtRefresh, 250);
});
document.getElementById('rt-catadd').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const p = rtProducts[rtSel];
  if (!p || !p.name) return;
  swbSend('rate.catAdd|' + String(p.name).replace(/[|,]/g, ' ').trim() + ',' +
                 String(p.units || '').replace(/[|,]/g, ' ').trim() + ',' + (p.targetRate || 0));
  setTimeout(rtRefresh, 250);
});
// Products live in a shared catalogue (a product is used by more than one tool:
// DAP goes through the spreader and the drill); the channel holds this tool's
// hardware settings, so meter calibration never follows a product to another
// machine.
function rtRenderCatalog() {
  const sel = document.getElementById('rt-catsel');
  if (!sel) return;
  const items = rtCatalog || [];
  const cur = (rtProducts[rtSel] || {}).name || '';
  sel.innerHTML = '';
  const none = document.createElement('option');
  none.value = ''; none.textContent = items.length ? '— pick —' : '(catalogue empty)';
  sel.appendChild(none);
  for (const it of items) {
    const o = document.createElement('option');
    o.value = it.name;
    o.textContent = it.name + ' (' + it.units + ')';
    sel.appendChild(o);
  }
  sel.value = items.some(i => i.name === cur) ? cur : '';
  const tool = document.getElementById('rt-tool');
  if (tool) tool.textContent = rtTool || '(none)';
  const hudSel = document.getElementById('rt-hudmode');
  if (hudSel) hudSel.value = String(rhMode);
}
function rtRender() {
  rtRenderCatalog();
  const tabs = document.getElementById('rt-tabs');
  tabs.innerHTML = '';
  for (let i = 0; i < 5; i++) {
    const p = rtProducts[i];
    const b = document.createElement('button');
    b.className = 'rp-pat' + (i === rtSel ? ' on' : '');
    const dot = p && p.connected ? '🟢' : (p && p.enabled ? '🔴' : '');
    b.textContent = String.fromCharCode(65 + i) + dot;
    b.addEventListener('pointerdown', e => { e.stopPropagation(); rtSel = i; rtRender(); });
    tabs.appendChild(b);
  }
  const p = rtProducts[rtSel];
  if (!p) return;
  const set = (id, v) => { const el = document.getElementById(id); if (document.activeElement !== el) el.value = v; };
  set('rt-name', p.name || '');
  document.getElementById('rt-enabled').classList.toggle('on', !!p.enabled);
  document.getElementById('rt-auto').classList.toggle('on', !!p.auto);
  set('rt-mod', p.moduleId); set('rt-sen', p.sensorId);
  // Sensor numbers count from 0, so a one-sensor module answers on sensor 0 and
  // ignores sensor 1 — which reads as a dead flow meter, not a setting mistake.
  const sw = document.getElementById('rt-senwarn');
  if (sw) {
    const n = rtSensorCounts[p.moduleId];
    const bad = n != null && p.sensorId >= n;
    sw.hidden = !bad;
    if (bad) sw.textContent =
      'Module ' + p.moduleId + ' is set up for ' + n + ' sensor' + (n === 1 ? '' : 's') +
      ', so sensor ' + p.sensorId + ' never reports. Sensors are numbered from 0.';
  }
  set('rt-target', p.targetRate);
  const un = document.getElementById('rt-units'); if (document.activeElement !== un) un.value = String(p.coverageUnits);
  set('rt-cal', p.meterCal);
  const ct = document.getElementById('rt-ctype'); if (document.activeElement !== ct) ct.value = String(p.controlType);
  document.getElementById('rt-pwm').textContent = p.manualPwm;
  set('rt-tanksize', p.tankSize); set('rt-tank', p.tank);
  set('rt-units-txt', p.units || 'L');
  const tankPct = p.tankSize > 0 ? Math.round(p.tank / p.tankSize * 100) : 0;
  document.getElementById('rt-live').textContent = (rtPlane ? '' : 'PLANE OFFLINE · ')
    + (p.connected ? 'module OK' : 'module —')
    + ` · flow ${p.upm} ${p.units || 'u'}/min · PWM ${p.pwm} · ${p.hz} Hz`
    + ` · qty ${p.qty} · area ${p.area} ha · tank ${tankPct}%`
    + (p.binEmpty ? ' · BIN EMPTY' : '');
  // calibration state
  document.getElementById('rt-calstart').classList.toggle('on', !!p.calActive);
  document.getElementById('rt-calrow').style.display = (p.calActive || p.calStopped) ? '' : 'none';
  document.getElementById('rt-calind').textContent =
    'Indicated: ' + (p.calIndicated || 0) + ' ' + (p.units || '') + (p.calActive ? ' (running…)' : '');
}
wireTabStrip(document.getElementById('ratecontrol'), 'rt-top');

// Switches + Primed tabs (native Machine > Switches / Primed Start).
function rtRenderSwitches() {
  const d = rtStatus;
  if (!d || !d.switchbox) return;
  const sm = document.getElementById('sw-mastermode');
  if (document.activeElement !== sm) sm.value = String(d.switchbox.masterMode);
  const st = document.getElementById('sw-type');
  if (document.activeElement !== st) st.value = String(d.switchbox.switchType);
  for (const [id, on] of [['sw-workgate', d.switchbox.workGate], ['sw-onscreen', d.switchbox.onScreen], ['sw-autorate', d.switchbox.autoRate]]) {
    const b = document.getElementById(id);
    b.classList.toggle('active', !!on); b.textContent = on ? 'On' : 'Off';
  }
  const ws = document.getElementById('sw-workstate');
  if (ws) {
    ws.textContent = d.switchbox.workSwitchOn ? 'ON (implement down)' : 'off';
    ws.style.color = d.switchbox.workSwitchOn ? '#2ecc71' : '';
  }
  // Physical PGN 32618 box: read-only status + live switch levels.
  const phys = d.switchbox.physical;
  const pc = document.getElementById('sw-phys');
  if (pc) {
    const on = !!(phys && phys.connected);
    pc.textContent = on ? 'connected' : '—';
    pc.style.color = on ? '#2ecc71' : '';
    const ps = document.getElementById('sw-physstate');
    if (ps) {
      if (on) {
        const secs = [];
        for (let i = 0; i < 16; i++) if (phys.sections & (1 << i)) secs.push(i + 1);
        ps.textContent = 'work ' + (phys.work ? 'ON' : 'off')
          + ' · auto sec ' + (phys.autoSection ? 'ON' : 'off')
          + ' · auto rate ' + (phys.autoRate ? 'ON' : 'off')
          + ' · sw ' + (secs.length ? secs.join(',') : 'none');
      } else ps.textContent = '—';
    }
  }
  // allocation grid: one select per section
  const grid = document.getElementById('sw-alloc');
  const alloc = d.switchbox.sectionSwitch || [];
  if (grid.childElementCount !== alloc.length) {
    grid.innerHTML = '';
    for (let i = 0; i < alloc.length; i++) {
      const cell = document.createElement('div'); cell.className = 'tc-cell';
      const lab = document.createElement('span'); lab.textContent = 'Sec ' + (i + 1);
      const sel = document.createElement('select'); sel.className = 'cfg-sel'; sel.dataset.sec = i;
      sel.innerHTML = '<option value="-1">none</option>' +
        Array.from({length: 8}, (_, k) => '<option value="' + k + '">S' + (k + 1) + '</option>').join('');
      sel.addEventListener('change', () => swbSend('rate.sw|secSwitch:' + i + ',' + sel.value));
      cell.appendChild(lab); cell.appendChild(sel); grid.appendChild(cell);
    }
  }
  for (let i = 0; i < alloc.length; i++) {
    const sel = grid.children[i] && grid.children[i].querySelector('select');
    if (sel && document.activeElement !== sel) sel.value = String(alloc[i]);
  }
  if (d.primed) {
    for (const [id, v] of [['pr-ontime', d.primed.onTime], ['pr-speed', d.primed.speed], ['pr-delay', d.primed.delay]]) {
      const el = document.getElementById(id);
      if (document.activeElement !== el) el.value = v;
    }
    const rb = document.getElementById('pr-resume');
    rb.classList.toggle('active', !!d.primed.resume); rb.textContent = d.primed.resume ? 'On' : 'Off';
    document.getElementById('pr-status').textContent =
      d.primed.active ? 'PRIMING — ' + d.primed.remaining + 's left' : '';
  }
}
document.getElementById('sw-mastermode').addEventListener('change', e => swbSend('rate.sw|masterMode,' + e.target.value));
document.getElementById('sw-type').addEventListener('change', e => swbSend('rate.sw|switchType,' + e.target.value));
for (const [id, key] of [['sw-workgate','workGate'], ['sw-onscreen','onScreen'], ['sw-autorate','autoRate']])
  document.getElementById(id).addEventListener('pointerdown', e => {
    e.stopPropagation();
    const b = document.getElementById(id);
    swbSend('rate.sw|' + key + ',' + (b.classList.contains('active') ? 0 : 1));
    setTimeout(rtRefresh, 250);
  });
for (const [id, key] of [['pr-ontime','primed.onTime'], ['pr-speed','primed.speed'], ['pr-delay','primed.delay']])
  document.getElementById(id).addEventListener('change', e => swbSend('rate.sw|' + key + ',' + e.target.value));
document.getElementById('pr-resume').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const b = document.getElementById('pr-resume');
  swbSend('rate.sw|primed.resume,' + (b.classList.contains('active') ? 0 : 1));
  setTimeout(rtRefresh, 250);
});
document.getElementById('pr-test').addEventListener('pointerdown', e => {
  e.stopPropagation(); swbSend('rate.primed|1'); setTimeout(rtRefresh, 300);
});
document.getElementById('rt-back').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('fieldtools', 'ln-fieldtools'); });
document.getElementById('rt-name').addEventListener('change', () => rtSend('name', document.getElementById('rt-name').value.replace(/[|,]/g, ' ')));
document.getElementById('rt-enabled').addEventListener('pointerdown', e => { e.stopPropagation(); const p = rtProducts[rtSel]; rtSend('enabled', p && p.enabled ? 0 : 1); });
document.getElementById('rt-auto').addEventListener('pointerdown', e => { e.stopPropagation(); const p = rtProducts[rtSel]; rtSend('auto', p && p.auto ? 0 : 1); });
for (const [id, key] of [['rt-mod','moduleId'],['rt-sen','sensorId'],['rt-target','targetRate'],['rt-cal','meterCal'],['rt-tanksize','tankSize'],['rt-tank','tankRemaining']]) {
  document.getElementById(id).addEventListener('change', () => rtSend(key, document.getElementById(id).value));
}
document.getElementById('rt-units').addEventListener('change', () => rtSend('coverageUnits', document.getElementById('rt-units').value));
document.getElementById('rt-ctype').addEventListener('change', () => rtSend('controlType', document.getElementById('rt-ctype').value));
for (const b of document.querySelectorAll('#ratecontrol .rp-sb'))
  b.addEventListener('pointerdown', e => {
    e.stopPropagation();
    const p = rtProducts[rtSel]; if (!p) return;
    rtSend('manualPwm', Math.max(-255, Math.min(255, (p.manualPwm | 0) + (+b.dataset.d))));
  });
document.getElementById('rt-resetqty').addEventListener('pointerdown', e => { e.stopPropagation(); swbSend('rate.resetQty|' + rtSel); setTimeout(rtRefresh, 250); });
document.getElementById('rt-resetarea').addEventListener('pointerdown', e => { e.stopPropagation(); swbSend('rate.resetArea|' + rtSel); setTimeout(rtRefresh, 250); });
document.getElementById('rt-units-txt').addEventListener('change', () => rtSend('units', document.getElementById('rt-units-txt').value.replace(/[|,]/g, ' ').trim()));
document.getElementById('rt-calstart').addEventListener('pointerdown', e => { e.stopPropagation(); swbSend('rate.calStart|' + rtSel); setTimeout(rtRefresh, 250); });
document.getElementById('rt-calstop').addEventListener('pointerdown', e => { e.stopPropagation(); swbSend('rate.calStop|' + rtSel); setTimeout(rtRefresh, 250); });
document.getElementById('rt-calapply').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const v = parseFloat(document.getElementById('rt-calactual').value);
  if (Number.isFinite(v) && v > 0) { swbSend('rate.calApply|' + rtSel + ',' + v); document.getElementById('rt-calactual').value = ''; setTimeout(rtRefresh, 300); }
});
document.getElementById('ft-obstacles').addEventListener('pointerdown', e => { e.stopPropagation(); openObstacles(); });
document.getElementById('bm-back').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('fieldtools', 'ln-fieldtools'); });

// ---- Field Builder (Phase MT) — Tracks / Headland / Tram editor (NO_SCRIM: the map
// stays interactive so you draw/edit on it). Stage 1 = Tracks tab; reuses the existing
// AB-draw / create-from-boundary flows on the main map. ----
let fbTab = 'tracks';   // active FB tab
let fbSel = -1;         // selected track index in the Tracks tab
document.getElementById('ft-fieldbuilder').addEventListener('pointerdown', e => {
  e.stopPropagation(); fbTab = 'tracks'; fbSel = -1; lnOpen('fieldbuilder', 'ln-fieldtools', renderFieldBuilder);
});
document.getElementById('fieldbuilder').addEventListener('pointerdown', e => e.stopPropagation()); // panel taps don't pan the map (NO_SCRIM)
document.querySelector('#fieldbuilder .fb-back').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('fieldtools', 'ln-fieldtools'); });
function showFbTab(tab) {
  fbTab = tab;
  document.getElementById('fb-addtrack').hidden = true;
  document.getElementById('fb-addhl').hidden = true;
  document.getElementById('fb-tramedit').hidden = true;
  for (const t of document.querySelectorAll('#fieldbuilder .fb-tab')) t.classList.toggle('active', t.dataset.fbtab === tab);
  for (const p of document.querySelectorAll('#fieldbuilder .fb-pane[data-fbpane]')) p.hidden = (p.dataset.fbpane !== tab);
  if (tab === 'tracks') renderFbTracks();
  else if (tab === 'headland') renderFbHeadland();
  else if (tab === 'tram') renderFbTram();
}
function renderFieldBuilder() { showFbTab(fbTab); }
for (const t of document.querySelectorAll('#fieldbuilder .fb-tab'))
  t.addEventListener('pointerdown', e => { e.stopPropagation(); showFbTab(t.dataset.fbtab); });
function renderFbTracks() {
  const list = document.getElementById('fb-tracklist');
  const tl = (scene && scene.trackList) || [];
  if (fbSel >= tl.length) fbSel = -1;
  if (!tl.length) { list.innerHTML = '<div class="trk-empty">No tracks</div>'; return; }
  list.innerHTML = '';
  tl.forEach((t, i) => {
    const row = document.createElement('div');
    row.className = 'fb-trkrow' + (t.active ? ' active' : '') + (i === fbSel ? ' sel' : '');
    row.innerHTML = '<span class="fb-dot"></span><span class="fb-tname"></span>';
    row.querySelector('.fb-tname').textContent = t.name;
    row.addEventListener('pointerdown', ev => {
      ev.stopPropagation(); fbSel = i;
      if (iHoldControl) transport.send('track.select|' + t.index); // activate (highlights on map)
      renderFbTracks();
    });
    list.appendChild(row);
  });
}
// Add-track sub-view.
document.getElementById('fb-trk-add').addEventListener('pointerdown', e => {
  e.stopPropagation();
  for (const p of document.querySelectorAll('#fieldbuilder .fb-pane[data-fbpane]')) p.hidden = true;
  document.getElementById('fb-addtrack').hidden = false;
});
document.getElementById('fb-add-back').addEventListener('pointerdown', e => { e.stopPropagation(); showFbTab('tracks'); });
for (const b of document.querySelectorAll('#fb-addtrack .fb-addbtn'))
  b.addEventListener('pointerdown', e => {
    e.stopPropagation();
    const m = b.dataset.fbadd;
    showFbTab('tracks');
    if (m === 'ab') startDrawTrack('straight');        // map-tap draw (FB stays open, NO_SCRIM)
    else if (m === 'curve') startDrawTrack('curve');
    else if (m === 'aplus') transport.send('track.aPlus');
    else if (m === 'bedge') transport.send('track.createFromBoundary');
    else if (m === 'bcurve') transport.send('track.boundaryCurve');
    else if (m === 'ball') transport.send('track.allEdges');
  });
// Track actions (Edit = stage 4).
document.getElementById('fb-trk-edit').addEventListener('pointerdown', e => { e.stopPropagation(); startTrackEdit(); });
document.getElementById('fb-trk-delete').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const tl = scene && scene.trackList; if (fbSel < 0 || !tl || !tl[fbSel] || !iHoldControl) return;
  transport.send('track.deleteAt|' + tl[fbSel].index); fbSel = -1;
});
document.getElementById('fb-trk-deleteall').addEventListener('pointerdown', e => {
  e.stopPropagation();
  showConfirm('Delete All Tracks', 'Delete all tracks? This cannot be undone.',
    () => { if (iHoldControl) transport.send('track.deleteAll'); fbSel = -1; });
});
document.getElementById('fb-trk-rename').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const tl = scene && scene.trackList; if (fbSel < 0 || !tl || !tl[fbSel]) return;
  const t = tl[fbSel];
  renderFbTracks();
  const row = document.querySelectorAll('#fb-tracklist .fb-trkrow')[fbSel]; if (!row) return;
  const nameEl = row.querySelector('.fb-tname');
  const input = document.createElement('input'); input.className = 'flg-nameedit'; input.value = t.name;
  nameEl.replaceWith(input); input.focus(); input.select();
  const commit = () => { const v = input.value.trim(); if (v && v !== t.name) swbSend('track.rename|' + t.index + ',' + v); renderFbTracks(); };
  input.addEventListener('keydown', ev => { ev.stopPropagation(); if (ev.key === 'Enter') commit(); else if (ev.key === 'Escape') renderFbTracks(); });
  input.addEventListener('blur', commit);
});
// ---- Field Builder Headland tab (stage 2) — segment list + build ----------------------
// Building is field-data editing (Tier-1, ungated). Line/Curve = tap two boundary points
// on the main map (host snaps + builds the inward offset + the headland polygon); Whole
// Boundary = the entire boundary offset inward. The host runs ALL geometry — the client
// only captures the two taps + the offset and ships them (no headland math in JS).
let fbHlSel = -1;        // selected segment index in the Headland tab
function fbDefaultOffset() {  // tool width × 2 (host re-defaults to the same if 0 is sent)
  const tw = toolWidthM();
  return tw > 0 ? +(tw * 2).toFixed(1) : 12;
}
function renderFbHeadland() {
  const list = document.getElementById('fb-hllist');
  const hs = (scene && scene.headlandSegs) || [];
  if (fbHlSel >= hs.length) fbHlSel = -1;
  const offrow = document.getElementById('fb-hl-offrow');
  if (!hs.length) { list.innerHTML = '<div class="trk-empty">No headland yet. Add a line along each edge (or Whole Boundary). Where the lines cross they enclose the headland.</div>'; offrow.hidden = true; return; }
  list.innerHTML = '';
  hs.forEach((s, i) => {
    const row = document.createElement('div');
    row.className = 'fb-hlrow' + (s.effective ? '' : ' noeffect') + (i === fbHlSel ? ' sel' : '');
    row.innerHTML = '<span class="fb-dot"></span><span class="fb-hlname"></span><span class="fb-hlmeta"></span>';
    row.querySelector('.fb-hlname').textContent = s.name;
    row.querySelector('.fb-hlmeta').textContent = s.type + ' · ' + (+s.offset).toFixed(1) + ' m';
    row.addEventListener('pointerdown', ev => { ev.stopPropagation(); fbHlSel = i; renderFbHeadland(); });
    list.appendChild(row);
  });
  // Offset editor for the selected segment (re-runs the build on change).
  if (fbHlSel >= 0 && hs[fbHlSel]) {
    offrow.hidden = false;
    const off = document.getElementById('fb-hl-off');
    if (document.activeElement !== off) off.value = (+hs[fbHlSel].offset).toFixed(1);
  } else offrow.hidden = true;
}
document.getElementById('fb-hl-off').addEventListener('change', e => {
  e.stopPropagation();
  const hs = scene && scene.headlandSegs; if (fbHlSel < 0 || !hs || !hs[fbHlSel]) return;
  const v = parseFloat(e.target.value);
  if (Number.isFinite(v) && v > 0) transport.send('headland.setOffset|' + hs[fbHlSel].index + ',' + v);
});
// Inset = N tool widths (dropdown, mirrors AgOpen's cboxToolWidths) → fills the metre box;
// editing the metre box directly flips the dropdown to Custom.
const fbTwSel = document.getElementById('fb-hl-tw');
const fbNewOff = document.getElementById('fb-hl-newoff');
function fbApplyTw() { const n = parseInt(fbTwSel.value); if (n > 0) fbNewOff.value = +(n * toolWidthM()).toFixed(1); }
fbTwSel.addEventListener('change', e => { e.stopPropagation(); fbApplyTw(); });
fbNewOff.addEventListener('input', e => { e.stopPropagation(); fbTwSel.value = '0'; });
document.getElementById('fb-hl-add').addEventListener('pointerdown', e => {
  e.stopPropagation();
  fbTwSel.value = '2'; fbApplyTw();   // default 2 tool widths
  for (const p of document.querySelectorAll('#fieldbuilder .fb-pane[data-fbpane]')) p.hidden = true;
  document.getElementById('fb-addhl').hidden = false;
});
document.getElementById('fb-hl-addback').addEventListener('pointerdown', e => { e.stopPropagation(); showFbTab('headland'); });
for (const b of document.querySelectorAll('#fb-addhl .fb-addbtn'))
  b.addEventListener('pointerdown', e => {
    e.stopPropagation();
    const off = parseFloat(document.getElementById('fb-hl-newoff').value);
    const offset = (Number.isFinite(off) && off > 0) ? off : fbDefaultOffset();
    const m = b.dataset.fbhl;
    showFbTab('headland');
    if (m === 'whole') transport.send('headland.wholeBoundary|' + offset);
    else startHeadlandDraw(m, offset);   // 'line' | 'curve' — map-tap two boundary points
  });
document.getElementById('fb-hl-delete').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const hs = scene && scene.headlandSegs; if (fbHlSel < 0 || !hs || !hs[fbHlSel]) return;
  transport.send('headland.delete|' + hs[fbHlSel].index); fbHlSel = -1;
});
document.getElementById('fb-hl-deleteall').addEventListener('pointerdown', e => {
  e.stopPropagation();
  showConfirm('Delete All Headland', 'Delete all headland segments? The headland reverts to the boundary.',
    () => { transport.send('headland.deleteAll'); fbHlSel = -1; });
});
document.getElementById('fb-hl-rename').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const hs = scene && scene.headlandSegs; if (fbHlSel < 0 || !hs || !hs[fbHlSel]) return;
  const s = hs[fbHlSel];
  renderFbHeadland();
  const row = document.querySelectorAll('#fb-hllist .fb-hlrow')[fbHlSel]; if (!row) return;
  const nameEl = row.querySelector('.fb-hlname');
  const input = document.createElement('input'); input.className = 'flg-nameedit'; input.value = s.name;
  nameEl.replaceWith(input); input.focus(); input.select();
  const commit = () => { const v = input.value.trim(); if (v && v !== s.name) transport.send('headland.rename|' + s.index + ',' + v); renderFbHeadland(); };
  input.addEventListener('keydown', ev => { ev.stopPropagation(); if (ev.key === 'Enter') commit(); else if (ev.key === 'Escape') renderFbHeadland(); });
  input.addEventListener('blur', commit);
});
// ---- Field Builder Tram tab (stage 3) — system list + per-system editor --------------
// Systems live in ConfigStore.Tram.Systems (host SoT); the browser sends field edits and
// the host regenerates the lines + persists. Tram building is field-data editing (Tier-1).
let fbTramSel = -1;
let fbTramPendingEdit = false; // open the editor on the system we just added (native parity)
function openTramEditor() {
  for (const p of document.querySelectorAll('#fieldbuilder .fb-pane[data-fbpane]')) p.hidden = true;
  document.getElementById('fb-tramedit').hidden = false;
  populateTramEdit();
}
function renderFbTram() {
  const list = document.getElementById('fb-tramlist');
  const ts = (scene && scene.tramSystems) || [];
  if (fbTramPendingEdit && ts.length) { fbTramPendingEdit = false; fbTramSel = ts.length - 1; openTramEditor(); return; }
  if (fbTramSel >= ts.length) fbTramSel = -1;
  if (!ts.length) { list.innerHTML = '<div class="trk-empty">No tram systems. Add one referencing a track or the boundary.</div>'; return; }
  list.innerHTML = '';
  ts.forEach((s, i) => {
    const row = document.createElement('div');
    row.className = 'fb-hlrow' + (s.enabled ? '' : ' noeffect') + (i === fbTramSel ? ' sel' : '');
    row.innerHTML = '<span class="fb-dot"></span><span class="fb-hlname"></span><span class="fb-hlmeta"></span>';
    row.querySelector('.fb-hlname').textContent = s.name;
    row.querySelector('.fb-hlmeta').textContent = s.width.toFixed(1) + ' m · ' + (s.passCount ? s.passCount + ' pass' : 'all');
    row.addEventListener('pointerdown', ev => { ev.stopPropagation(); fbTramSel = i; renderFbTram(); });
    list.appendChild(row);
  });
}
function curTram() { const ts = scene && scene.tramSystems; return (fbTramSel >= 0 && ts && ts[fbTramSel]) ? ts[fbTramSel] : null; }
function tramSet(field, value) { const s = curTram(); if (s) transport.send('tram.set|' + s.index + ',' + field + ',' + value); }
document.getElementById('fb-tram-add').addEventListener('pointerdown', e => { e.stopPropagation(); fbTramPendingEdit = true; transport.send('tram.add'); });
document.getElementById('fb-tram-delete').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const s = curTram(); if (!s) return;
  showConfirm('Delete Tram System', 'Delete "' + s.name + '"?', () => { transport.send('tram.delete|' + s.index); fbTramSel = -1; });
});
document.getElementById('fb-tram-edit').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (curTram()) openTramEditor();
});
document.getElementById('fb-tram-back').addEventListener('pointerdown', e => { e.stopPropagation(); showFbTab('tram'); });
document.getElementById('fb-tram-done').addEventListener('pointerdown', e => { e.stopPropagation(); showFbTab('tram'); });
function populateTramEdit() {
  const s = curTram(); if (!s) { showFbTab('tram'); return; }
  document.getElementById('fb-tram-title').textContent = 'Edit: ' + s.name;
  const en = document.getElementById('fb-tram-en'); en.classList.toggle('active', s.enabled); en.textContent = s.enabled ? 'On' : 'Off';
  const ref = document.getElementById('fb-tram-ref');
  const opts = ['(Boundary)'];
  for (const t of (scene.trackList || [])) if (t.type !== 'Path' && t.type !== 'Contour') opts.push(t.name);
  ref.innerHTML = opts.map(o => { const e = document.createElement('option'); e.textContent = o; return e.outerHTML; }).join('');
  ref.value = s.refLabel;
  const wEl = document.getElementById('fb-tram-w'); if (document.activeElement !== wEl) wEl.value = s.width.toFixed(1);
  const offEl = document.getElementById('fb-tram-off'); if (document.activeElement !== offEl) offEl.value = s.offset.toFixed(1);
  const pEl = document.getElementById('fb-tram-passes'); if (document.activeElement !== pEl) pEl.value = s.passCount;
  for (const b of document.querySelectorAll('#fb-tramedit [data-tmode]')) b.classList.toggle('sel', +b.dataset.tmode === s.mode);
  for (const b of document.querySelectorAll('#fb-tramedit [data-tdir]')) b.classList.toggle('sel', +b.dataset.tdir === s.direction);
  document.getElementById('fb-tram-offrow').hidden = s.isBoundary;   // offset/direction don't apply
  document.getElementById('fb-tram-dirlbl').hidden = s.isBoundary;   // to boundary-referenced systems
  document.getElementById('fb-tram-dir').hidden = s.isBoundary;
}
document.getElementById('fb-tram-en').addEventListener('pointerdown', e => { e.stopPropagation(); const s = curTram(); if (s) tramSet('enabled', s.enabled ? '0' : '1'); });
document.getElementById('fb-tram-ref').addEventListener('change', e => { e.stopPropagation(); tramSet('ref', e.target.value); });
document.getElementById('fb-tram-w').addEventListener('change', e => { e.stopPropagation(); const v = parseFloat(e.target.value); if (v > 0) tramSet('width', v); });
document.getElementById('fb-tram-off').addEventListener('change', e => { e.stopPropagation(); const v = parseFloat(e.target.value); if (Number.isFinite(v)) tramSet('offset', v); });
document.getElementById('fb-tram-passes').addEventListener('change', e => { e.stopPropagation(); const v = parseInt(e.target.value); if (Number.isFinite(v)) tramSet('passes', Math.max(0, v)); });
for (const b of document.querySelectorAll('#fb-tramedit [data-tmode]')) b.addEventListener('pointerdown', e => { e.stopPropagation(); tramSet('mode', b.dataset.tmode); });
for (const b of document.querySelectorAll('#fb-tramedit [data-tdir]')) b.addEventListener('pointerdown', e => { e.stopPropagation(); tramSet('dir', b.dataset.tdir); });
document.getElementById('bm-delete').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (!boundary || boundary.selectedIndex < 0) return;
  const it = boundary.items.find(i => i.index === boundary.selectedIndex);
  showConfirm('Delete Boundary', 'Delete the ' + (it ? it.boundaryType : 'selected') + ' boundary? This cannot be undone.',
    () => transport.send('boundary.delete'));
});
document.getElementById('bm-buildtracks').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('boundary.buildFromTracks'); });
document.getElementById('bm-drawmap').addEventListener('pointerdown', e => { e.stopPropagation(); startSatBoundary(); });
// Import-KML boundary picker: lists KML/KMZ from the Import folder (FieldOps frame),
// tapping one imports it as the field boundary (host parses + imports, replacing the outer).
document.getElementById('bm-importkml').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('kmlboundary', 'ln-fieldtools', renderKmlBoundary); });
document.getElementById('kb-back').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('boundarymenu', 'ln-fieldtools', renderBoundaryMenu); });
function renderKmlBoundary() {
  const list = document.getElementById('kb-list'); list.innerHTML = '';
  const files = (fieldOps && fieldOps.kmlFiles) || [];
  if (!files.length) { list.innerHTML = '<div class="trk-empty">No KML/KMZ files in the Import folder.</div>'; return; }
  for (const f of files) {
    const row = document.createElement('div'); row.className = 'fb-trkrow';
    row.innerHTML = '<span class="fb-dot"></span><span class="fb-tname"></span>';
    row.querySelector('.fb-tname').textContent = f;
    row.addEventListener('pointerdown', ev => {
      ev.stopPropagation();
      showConfirm('Import KML Boundary', 'Import "' + f + '" as the field boundary? This replaces the current outer boundary.',
        () => { transport.send('boundary.importKmlFile|' + f); lnCloseAll(); });
    });
    list.appendChild(row);
  }
}
document.getElementById('bm-drivearound').addEventListener('pointerdown', e => {
  e.stopPropagation(); transport.send('boundary.driveAround'); lnOpen('boundaryplayer', 'ln-fieldtools', renderBoundaryPlayer);
});
document.getElementById('bm-driveinner').addEventListener('pointerdown', e => {
  e.stopPropagation(); transport.send('boundary.driveAroundInner'); lnOpen('boundaryplayer', 'ln-fieldtools', renderBoundaryPlayer);
});
document.getElementById('bm-accept').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('boundary.accept'); lnCloseAll(); });
// Boundary player.
document.getElementById('bp-back').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('boundary.refresh'); lnOpen('boundarymenu', 'ln-fieldtools', renderBoundaryMenu); });
document.getElementById('bp-offset').addEventListener('change', e => { e.stopPropagation(); const v = parseFloat(e.target.value); if (Number.isFinite(v)) transport.send('boundary.setOffset|' + v); });
document.getElementById('bp-clear').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('boundary.clear'); });
document.getElementById('bp-section').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('boundary.toggleSectionControl'); });
document.getElementById('bp-undo').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('boundary.undo'); });
document.getElementById('bp-add').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('boundary.addPoint'); });
document.getElementById('bp-leftright').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('boundary.toggleLeftRight'); });
document.getElementById('bp-antennatool').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('boundary.toggleAntennaTool'); });
document.getElementById('bp-stop').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('boundary.stop'); transport.send('boundary.refresh'); lnOpen('boundarymenu', 'ln-fieldtools', renderBoundaryMenu); });
document.getElementById('bp-record').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('boundary.toggleRecording'); });
// Offset Fix D-pad (argless Tier-1 commands).
for (const b of document.querySelectorAll('#offsetfix .of-btn'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); transport.send(b.dataset.cmd); });
// Manual Easting/Northing entry → absolute set (offset.set|E,N).
function sendOffsetSet() {
  const ns = parseFloat(document.getElementById('of-ns-in').value);
  const ew = parseFloat(document.getElementById('of-ew-in').value);
  if (Number.isFinite(ns) && Number.isFinite(ew)) transport.send('offset.set|' + ew + ',' + ns);
}
for (const id of ['of-ns-in', 'of-ew-in'])
  document.getElementById(id).addEventListener('change', e => { e.stopPropagation(); sendOffsetSet(); });
for (const b of document.querySelectorAll('.ln-back'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('vehtoolhub', 'ln-vehicle', refreshHub); });
// Standard header close (X) → close the chain to the map. Tagged .ln-closex so the
// NTRIP chain Back/X buttons (which need parent-aware nav) keep their own handlers.
for (const b of document.querySelectorAll('.ln-closex'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); lnCloseAll(); });
// Units (Screen & Alerts) → config bridge write.
const saPanel = document.getElementById('screenalerts');
// Units + device settings (keyboard/fullscreen/elevation) moved to App Settings (File menu).
// Screen & Alerts toggles → config.set|display.X; action rows (theme/quality) → command.
for (const b of saPanel.querySelectorAll('.sa-tgl[data-key]'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); cfgSend(b.dataset.key, b.classList.contains('active') ? '0' : '1'); });
// Satellite/aerial map background — a client-only toggle (no backend display key).
document.getElementById('sa-sat').addEventListener('pointerdown', e => { e.stopPropagation(); toggleSatBackground(); });
// Terrain elevation shading — client-side toggle; fetches /api/elevation while on.
document.getElementById('sa-terrain').addEventListener('pointerdown', e => { e.stopPropagation(); toggleTerrain(); });
for (const b of saPanel.querySelectorAll('.sa-act'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); transport.send(b.dataset.cmd); });
const saExtra = document.getElementById('sa-extracount');
saExtra.addEventListener('change', () => { const v = parseInt(saExtra.value); if (Number.isFinite(v)) cfgSend('display.extraGuidelinesCount', v); });
function populateScreenAlerts() {
  if (!config || !config.display) return;
  const d = config.display;
  for (const b of saPanel.querySelectorAll('.sa-tgl[data-key]')) b.classList.toggle('active', !!d[b.dataset.key.split('.')[1]]);
  document.getElementById('sa-sat').classList.toggle('active', satEnabled);
  document.getElementById('sa-terrain').classList.toggle('active', terrainOn);
  if (document.activeElement !== saExtra) saExtra.value = d.extraGuidelinesCount;
  document.getElementById('sa-quality').textContent = d.resolutionLabel || '—';
}
// Vehicle config panel — full native VehicleConfigDialog surface (Vehicle/GPS/Roll).
// Generic config controls keyed by data-key="<section>.<field>"; reused by later
// sub-phases. Writes via config.set; Save Profile via profile.save.
const vcPanel = document.getElementById('vehiclecfg'), vcName = document.getElementById('vc-name');
// Hitch-type labels (mirrors ConfigurationViewModel.HitchTypeOptions); value = code =
// index − 1 (code −1 → "Not available").
const HITCH_OPTS = ['Not available', 'Unknown', 'ISO 6489-3 Tractor drawbar',
  'ISO 730 Three-point-hitch semi-mounted', 'ISO 730 Three-point-hitch mounted',
  'ISO 6489-1 Hitch-hook', 'ISO 6489-2 Clevis coupling 40', 'ISO 6489-4 Piton type coupling',
  'ISO 6489-5 CUNA hitch', 'ISO 24347 Ball type hitch', 'Chassis Mounted - Self-Propelled',
  'ISO 5692-2 Pivot wagon hitch'];
const vcHitchSel = vcPanel.querySelector('.cfg-sel[data-key="vehicle.hitchType"]');
HITCH_OPTS.forEach((label, i) => { const o = document.createElement('option'); o.value = i - 1; o.textContent = label; vcHitchSel.appendChild(o); });
const vcFw = vcPanel.querySelector('.cfg-slider[data-key="gps.headingFusionWeight"]');
function cfgGet(key) { if (!config) return undefined; const p = key.split('.'); return config[p[0]] && config[p[0]][p[1]]; }
function cfgSend(key, val) { transport.send('config.set|' + key + ':' + val); }
// Tabs.
for (const t of vcPanel.querySelectorAll('.cfg-tab'))
  t.addEventListener('pointerdown', e => {
    e.stopPropagation();
    for (const b of vcPanel.querySelectorAll('.cfg-tab')) b.classList.toggle('active', b === t);
    for (const body of vcPanel.querySelectorAll('.cfg-body')) body.hidden = (body.id !== t.dataset.tab);
  });
// Generic config controls keyed by data-key="<section>.<field>" — reused by the
// Vehicle and Tool panels. Tab strips, selects, sliders and dynamic lists wire per
// panel. cfg-typebtn active state compares the config value to data-active (falling
// back to data-val) so name-valued buttons (tool type) can match an int field.
// Slider readout formatter (data-fmt): f0/f1/f2 fixed decimals, p0 = value×100 %,
// pct = value %, deg = value °. Used by generic .cfg-slider controls.
function fmtRo(v, fmt) {
  const n = Number(v);
  switch (fmt) {
    case 'f0': return n.toFixed(0);
    case 'f1': return n.toFixed(1);
    case 'f2': return n.toFixed(2);
    case 'p0': return Math.round(n * 100) + '%';
    case 'pct': return Math.round(n) + '%';
    case 'deg': return Math.round(n) + '°';
    default: return String(v);
  }
}
function wireCfgControls(panel) {
  for (const inp of panel.querySelectorAll('.cfg-num'))
    inp.addEventListener('change', () => { const v = parseFloat(inp.value); if (Number.isFinite(v)) cfgSend(inp.dataset.key, v); });
  for (const b of panel.querySelectorAll('.cfg-tgl'))
    b.addEventListener('pointerdown', e => { e.stopPropagation(); cfgSend(b.dataset.key, b.classList.contains('active') ? '0' : '1'); });
  for (const b of panel.querySelectorAll('.cfg-typebtn'))
    b.addEventListener('pointerdown', e => { e.stopPropagation(); cfgSend(b.dataset.key, b.dataset.val); });
  // .cfg-act = config.set action buttons; .rn-gated ones carry data-cmd (a gated
  // command, not a config key) and are wired separately, so exclude them here.
  for (const b of panel.querySelectorAll('.cfg-act:not(.rn-gated)'))
    b.addEventListener('pointerdown', e => { e.stopPropagation(); cfgSend(b.dataset.key, b.dataset.val); });
  for (const sel of panel.querySelectorAll('.cfg-isel'))
    sel.addEventListener('change', () => cfgSend(sel.dataset.key, sel.value));
  // Sliders with a data-fmt are generic: drag updates the readout live; the value
  // commits on release ('change') to avoid flooding the bridge. (Sliders without
  // data-fmt — e.g. the Vehicle heading-fusion slider — are wired by hand.)
  for (const s of panel.querySelectorAll('.cfg-slider[data-fmt]')) {
    const ro = panel.querySelector('.cfg-ro[data-for="' + s.dataset.key + '"]');
    s.addEventListener('input', () => { if (ro) ro.textContent = fmtRo(s.value, s.dataset.fmt); });
    s.addEventListener('change', () => cfgSend(s.dataset.key, s.value));
  }
}
function populateCfgControls(panel, force) {
  for (const inp of panel.querySelectorAll('.cfg-num')) {
    if (!force && document.activeElement === inp) continue;
    const val = cfgGet(inp.dataset.key);
    if (typeof val === 'number') inp.value = Math.round(val * 1000) / 1000;
  }
  for (const b of panel.querySelectorAll('.cfg-tgl')) {
    const on = !!cfgGet(b.dataset.key);
    b.classList.toggle('active', on);
    if (!b.dataset.keepLabel) b.textContent = on ? 'On' : 'Off';
  }
  for (const b of panel.querySelectorAll('.cfg-typebtn'))
    b.classList.toggle('active', String(cfgGet(b.dataset.key)) === (b.dataset.active != null ? b.dataset.active : b.dataset.val));
  for (const s of panel.querySelectorAll('.cfg-slider[data-fmt]')) {
    if (!force && document.activeElement === s) continue;
    const val = cfgGet(s.dataset.key);
    if (typeof val === 'number') {
      s.value = val;
      const ro = panel.querySelector('.cfg-ro[data-for="' + s.dataset.key + '"]');
      if (ro) ro.textContent = fmtRo(val, s.dataset.fmt);
    }
  }
  for (const sel of panel.querySelectorAll('.cfg-isel')) {
    if (document.activeElement === sel) continue;
    const val = cfgGet(sel.dataset.key);
    if (typeof val === 'number') sel.value = String(val);
  }
}
// Tab strip switcher: tabs + bodies share a data-strip id (so nested strips don't
// cross-toggle). Active tab → its data-tab body shown, siblings in the strip hidden.
function wireTabStrip(panel, stripId) {
  for (const t of panel.querySelectorAll('.cfg-tab[data-strip="' + stripId + '"]'))
    t.addEventListener('pointerdown', e => {
      e.stopPropagation();
      for (const b of panel.querySelectorAll('.cfg-tab[data-strip="' + stripId + '"]')) b.classList.toggle('active', b === t);
      for (const body of panel.querySelectorAll('.cfg-body[data-strip="' + stripId + '"]')) body.hidden = (body.id !== t.dataset.tab);
    });
}
wireCfgControls(vcPanel);
vcHitchSel.addEventListener('change', () => cfgSend('vehicle.hitchType', vcHitchSel.value));
vcFw.addEventListener('input', () => { document.getElementById('vc-hfw').textContent = Math.round(vcFw.value * 100) + '%'; cfgSend('gps.headingFusionWeight', vcFw.value); });
document.getElementById('vc-save').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('profile.save'); });
// Populate every control from the config frame. force (on open) fills all; otherwise
// skip the focused number input so we don't clobber what the user is typing.
function populateVehicleCfg(force) {
  if (!config || !config.vehicle) return;
  vcName.textContent = config.vehicle.name || '—';
  populateCfgControls(vcPanel, force);
  // Type-dependent measurement diagrams (mirror VehicleConfig.*ImageSource switches:
  // index by VehicleType 0 Tractor / 1 Harvester / 2 FourWD; '' = no image).
  const ty = Math.max(0, Math.min(2, cfgGet('vehicle.type') | 0));
  const setImg = (id, names) => {
    const el = document.getElementById(id), n = names[ty];
    if (!el) return;
    if (n) { const p = '/icons/' + n + '.png'; if (!el.src.endsWith(p)) el.src = p; el.style.display = ''; }
    else el.style.display = 'none';
  };
  setImg('vc-img-hitch', ['Hitch', '', 'HitchArticulated']);
  setImg('vc-img-wheelbase', ['WheelbaseTractor', 'WheelbaseHarvester', 'WheelbaseArticulated']);
  setImg('vc-img-track', ['TrackWidthTractor', 'TrackWidthHarvester', 'TrackWidthArticulated']);
  setImg('vc-img-antside', ['AntennaTractorTop', 'AntennaHarvesterTop', 'AntennaArticulatedTop']);
  setImg('vc-img-antoffset', ['AntennaTractorOffset', 'AntennaHarvesterOffset', 'AntennaArticulatedOffset']);
  vcHitchSel.value = cfgGet('vehicle.hitchType');
  const w = cfgGet('gps.headingFusionWeight') || 0;
  vcFw.value = w; document.getElementById('vc-hfw').textContent = Math.round(w * 100) + '%';
  // Dual-only fields are live only in Dual GPS mode; reverse detection only in single
  // (mirrors the native enable/disable gating).
  const dual = !!cfgGet('gps.isDualGps');
  const setEn = (key, en) => { const el = vcPanel.querySelector('[data-key="' + key + '"]'); if (el) { el.disabled = !en; el.style.opacity = en ? '1' : '0.4'; } };
  ['gps.dualHeadingOffset', 'gps.dualReverseDistance', 'gps.autoDualFix', 'gps.dualSwitchSpeed'].forEach(k => setEn(k, dual));
  setEn('gps.reverseDetection', !dual);
}

// ---- Tool config panel (full native ToolConfigDialog) ----
const tcPanel = document.getElementById('toolcfg'), tcName = document.getElementById('tc-name');
const tcHitchSel = tcPanel.querySelector('.cfg-sel[data-key="tool.hitchType"]');
HITCH_OPTS.forEach((label, i) => { const o = document.createElement('option'); o.value = i - 1; o.textContent = label; tcHitchSel.appendChild(o); });
const tcSingleColor = document.getElementById('tc-singlecolor');
wireCfgControls(tcPanel);
wireTabStrip(tcPanel, 'tc-top');
wireTabStrip(tcPanel, 'tc-sub');
wireTabStrip(tcPanel, 'tc-mac');
tcHitchSel.addEventListener('change', () => cfgSend('tool.hitchType', tcHitchSel.value));
tcSingleColor.addEventListener('change', () => cfgSend('tool.singleCoverageColor', tcSingleColor.value.slice(1)));
document.getElementById('tc-resetpins').addEventListener('pointerdown', e => { e.stopPropagation(); cfgSend('machine.resetPins', '1'); });
document.getElementById('tc-machinesend').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('machine.sendSave'); });
document.getElementById('tc-save').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('profile.save'); });
// PinFunction enum labels (mirror MachineConfig.PinFunction).
const PIN_FUNCS = ['None', 'Sec1', 'Sec2', 'Sec3', 'Sec4', 'Sec5', 'Sec6', 'Sec7', 'Sec8', 'Sec9', 'Sec10',
  'Sec11', 'Sec12', 'Sec13', 'Sec14', 'Sec15', 'Sec16', 'HydUp', 'HydDown', 'TramLeft', 'TramRight', 'GeoStop'];
const _tcBuilt = { sw: -1, ze: -1, sc: false, pins: false };
function tcDynInput(parent, idx, label, type, onChange) {
  const c = document.createElement('div'); c.className = 'tc-cell';
  const sp = document.createElement('span'); sp.textContent = label;
  const inp = document.createElement(type === 'pin' ? 'select' : 'input');
  if (type === 'pin') PIN_FUNCS.forEach((l, fi) => { const o = document.createElement('option'); o.value = fi; o.textContent = l; inp.appendChild(o); });
  else { inp.type = type; if (type === 'number') inp.step = '1'; }
  inp.dataset.idx = idx;
  inp.addEventListener('change', () => onChange(inp));
  c.appendChild(sp); c.appendChild(inp); parent.appendChild(c);
}
function tcShow(name, on) { for (const el of tcPanel.querySelectorAll('[data-show="' + name + '"]')) el.hidden = !on; }
function hex6(v) { return '#' + ((v >>> 0) & 0xFFFFFF).toString(16).padStart(6, '0'); }
function populateToolCfg(force) {
  if (!config || !config.tool) return;
  const t = config.tool;
  tcName.textContent = 'Tool: ' + (profiles ? profiles.activeTool : '—');
  populateCfgControls(tcPanel, force);
  if (document.activeElement !== tcHitchSel) tcHitchSel.value = t.hitchType;
  const hi = document.getElementById('tc-img-hitch');
  const hp = '/icons/' + (['ToolHitchPageFront', 'ToolHitchPageRear', 'ToolHitchPageTBT', 'ToolHitchPageTrailing'][t.type] || 'ToolHitchPageRear') + '.png';
  if (!hi.src.endsWith(hp)) hi.src = hp;
  // Conditional visibility by tool type (0 front, 1 rear, 2 TBT, 3 trailing).
  const trailing = t.type === 3, tbt = t.type === 2, trailtbt = trailing || tbt;
  tcShow('rigid', !trailing); tcShow('trailtbt', trailtbt); tcShow('tbt', tbt); tcShow('notrailtbt', !trailtbt);
  tcShow('individual', t.isSectionsNotZones); tcShow('zones', !t.isSectionsNotZones);
  tcShow('multicolor', t.isMultiColoredSections); tcShow('singlecolor', !t.isMultiColoredSections);
  tcShow('worksw', t.isWorkSwitchEnabled); tcShow('steersw', t.isSteerSwitchEnabled);
  const wsi = document.getElementById('tc-img-worksw');
  wsi.src = '/icons/' + (t.isWorkSwitchActiveLow ? 'SwitchActiveClosed' : 'SwitchActiveOpen') + '.png';
  // Dynamic lists — rebuild on count change, fill values (skip the focused control).
  const nSec = Math.max(1, Math.min(64, t.numSections)); // ToolConfig.MaxSections — backend supports 64
  if (_tcBuilt.sw !== nSec) {
    const g = document.getElementById('tc-sectionwidths'); g.innerHTML = '';
    for (let i = 0; i < nSec; i++) tcDynInput(g, i, 'S' + (i + 1), 'number', inp => { const v = parseFloat(inp.value); if (Number.isFinite(v)) cfgSend('tool.sectionWidth', i + ',' + v); });
    _tcBuilt.sw = nSec;
  }
  for (const inp of document.querySelectorAll('#tc-sectionwidths input')) if (force || document.activeElement !== inp) inp.value = Math.round(t.sectionWidths[+inp.dataset.idx]);
  const nZone = Math.max(1, Math.min(8, t.zones));
  if (_tcBuilt.ze !== nZone) {
    const g = document.getElementById('tc-zoneends'); g.innerHTML = '';
    for (let i = 1; i <= nZone; i++) tcDynInput(g, i, 'Zone ' + i, 'number', inp => { const v = parseInt(inp.value); if (Number.isFinite(v)) cfgSend('tool.zoneEnd', i + ',' + v); });
    _tcBuilt.ze = nZone;
  }
  for (const inp of document.querySelectorAll('#tc-zoneends input')) if (force || document.activeElement !== inp) inp.value = t.zoneRanges[+inp.dataset.idx];
  if (_tcBuilt.sc !== nSec) { // rebuild on count change; one swatch per section (was capped at 16)
    const g = document.getElementById('tc-sectioncolors'); g.innerHTML = '';
    for (let i = 0; i < nSec; i++) tcDynInput(g, i, 'S' + (i + 1), 'color', inp => cfgSend('tool.sectionColor', i + ',' + inp.value.slice(1)));
    _tcBuilt.sc = nSec;
  }
  for (const inp of document.querySelectorAll('#tc-sectioncolors input')) if (document.activeElement !== inp) inp.value = hex6(t.sectionColors[+inp.dataset.idx]);
  if (document.activeElement !== tcSingleColor) tcSingleColor.value = hex6(t.singleCoverageColor);
  if (!_tcBuilt.pins) {
    const g = document.getElementById('tc-pins'); g.innerHTML = '';
    for (let i = 0; i < 24; i++) tcDynInput(g, i, 'Pin ' + (i + 1), 'pin', sel => cfgSend('machine.pin', i + ',' + sel.value));
    _tcBuilt.pins = true;
  }
  for (const sel of document.querySelectorAll('#tc-pins select')) if (document.activeElement !== sel) sel.value = config.machine.pinAssignments[+sel.dataset.idx];
  document.getElementById('tc-totalwidth').textContent = 'Total width: ' + (t.totalWidth || 0).toFixed(2) + ' m';
}

// ---- AutoSteer config panel (Phase 9) — full native 9-tab surface + live Test Mode ----
// Field edits ride the generic config bridge (config.set|autosteer.*). Hardware-push
// actions (Send&Save / Zero-WAS / Reset / free-drive) are gated autosteer.* commands —
// routed through AutoSteerConfigViewModel host-side so PGN/free-drive logic isn't dupd.
const asPanel = document.getElementById('autosteercfg');
const asResetConfirm = document.getElementById('as-resetconfirm');
wireCfgControls(asPanel);
wireTabStrip(asPanel, 'as-left');   // left pane: 4 tabs
wireTabStrip(asPanel, 'as-right');  // right pane: 5 tabs
function hideAsResetConfirm() { asResetConfirm.hidden = true; }
// Generic gated action buttons (Zero-WAS, Send&Save, OK, free-drive) → send only while
// we hold control; the host re-checks (IsRestrictedCommand).
for (const b of asPanel.querySelectorAll('.rn-gated[data-cmd]'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); rnSend(b.dataset.cmd); });
// OK (green check) = Send&Save then close the panel.
document.getElementById('as-apply').addEventListener('pointerdown', e => { e.stopPropagation(); lnCloseAll(); });
document.getElementById('as-close').addEventListener('pointerdown', e => { e.stopPropagation(); lnCloseAll(); });
// Expand / collapse (native IsFullMode): collapsed = left pane only.
document.getElementById('as-expand').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const exp = asPanel.classList.toggle('expanded');
  e.currentTarget.textContent = exp ? '◀ Less' : '▶ Full';
});
// Reset → inline confirm bar (mirrors native ResetToDefaults confirmation).
document.getElementById('as-reset').addEventListener('pointerdown', e => { e.stopPropagation(); asResetConfirm.hidden = false; });
document.getElementById('as-reset-no').addEventListener('pointerdown', e => { e.stopPropagation(); hideAsResetConfirm(); });
document.getElementById('as-reset-yes').addEventListener('pointerdown', e => { e.stopPropagation(); hideAsResetConfirm(); rnSend('autosteer.reset'); });
// Algorithm mode switch (Pure Pursuit ↔ Stanley) — native puts this on the Algorithm
// tab as a single tap-to-toggle button.
document.getElementById('as-modeswitch').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (config && config.autosteer) cfgSend('autosteer.isStanleyMode', config.autosteer.isStanleyMode ? '0' : '1');
});
// Satellite-dialog launchers — defined by their own sub-phases (Smart-WAS / Wizard).
document.getElementById('as-smartwas').addEventListener('pointerdown', e => { e.stopPropagation(); if (typeof openSmartWas === 'function') openSmartWas(); });
document.getElementById('as-wizard').addEventListener('pointerdown', e => { e.stopPropagation(); if (typeof openSteerWizard === 'function') openSteerWizard(); });

function asSetText(id, t) { const el = document.getElementById(id); if (el) el.textContent = t; }
// Dim/disable gated controls when this browser doesn't hold control.
function updateAsGated() {
  for (const el of asPanel.querySelectorAll('.rn-gated')) el.classList.toggle('disabled', !iHoldControl);
}
function populateAutoSteer(force) {
  if (!config || !config.autosteer) return;
  const a = config.autosteer;
  populateCfgControls(asPanel, force);
  // Mode title + tab icon track Pure Pursuit / Stanley.
  asSetText('as-modetitle', a.isStanleyMode ? 'Stanley' : 'Pure Pursuit');
  const modeIc = document.getElementById('as-modetab-ic');
  if (modeIc) modeIc.src = a.isStanleyMode ? '/icons/ModeStanley.png' : '/icons/ModePurePursuit.png';
  const msw = document.getElementById('as-modeswitch');
  if (msw) { msw.textContent = a.isStanleyMode ? 'Stanley' : 'Pure Pursuit'; msw.classList.toggle('stanley', a.isStanleyMode); }
  asSetText('as-wasoffset', a.wasOffset);
  // Conditional sections (data-show) mirror native enable/visibility gating.
  const show = (name, on) => { for (const el of asPanel.querySelectorAll('[data-show="' + name + '"]')) el.style.display = on ? '' : 'none'; };
  show('purepursuit', !a.isStanleyMode);
  show('stanley', a.isStanleyMode);
  show('turnsensor', a.turnSensorEnabled);
  show('pressuresensor', a.pressureSensorEnabled);
  show('currentsensor', a.currentSensorEnabled);
  updateAsGated();
}
// Live steer telemetry — the status bar (Set/Act/Err) + Test-mode angle + free-drive
// button state. Refreshed every status frame.
function renderAutoSteerLive() {
  if (!statusBar) return;
  const set = statusBar.setSteerAngle || 0, act = statusBar.actualSteerAngle || 0;
  asSetText('as-stat-set', set.toFixed(1) + '°');
  asSetText('as-stat-act', act.toFixed(1) + '°');
  asSetText('as-stat-err', (set - act).toFixed(1) + '°');
  asSetText('as-live-angle', act.toFixed(1) + '°');
  const fdBtn = document.getElementById('as-fd-toggle'), fdIc = document.getElementById('as-fd-ic');
  if (fdBtn) {
    const on = !!statusBar.steerFreeDrive;
    fdBtn.classList.toggle('on', on);
    if (fdIc) fdIc.src = on ? '/icons/SteerDriveOn.png' : '/icons/SteerDriveOff.png';
  }
}

// ---- Smart WAS Calibration — chain sub-panel of AutoSteer (watch-the-tractor; no
// scrim, map stays interactive). Live stats ride the Status frame; Start/Stop/Reset/
// Apply are gated smartwas.* cmds. Opening REPLACES AutoSteer; ← Back reopens it
// (sw-back), ✕ → map (sw-x carries .ln-closex → generic close).
const swPanel = document.getElementById('smartwas');
function openSmartWas() { lnOpen('smartwas', 'ln-autosteer', populateSmartWas); }
document.getElementById('sw-back').addEventListener('pointerdown', e => {
  e.stopPropagation();
  lnOpen('autosteercfg', 'ln-autosteer', () => populateAutoSteer(true));
});
for (const b of swPanel.querySelectorAll('.rn-gated[data-cmd]'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); rnSend(b.dataset.cmd); });
function populateSmartWas() {
  if (!statusBar) return;
  const collecting = !!statusBar.swCollecting;
  asSetText('sw-status', collecting ? 'Collecting' : 'Stopped');
  asSetText('sw-samples', (statusBar.swSamples || 0) + ' / 200 min');
  asSetText('sw-mean', (statusBar.swMean || 0).toFixed(2) + '°');
  asSetText('sw-median', (statusBar.swMedian || 0).toFixed(2) + '°');
  asSetText('sw-stddev', (statusBar.swStdDev || 0).toFixed(2) + '°');
  const cpd = (config && config.autosteer && config.autosteer.countsPerDegree) || 0;
  const counts = Math.round((statusBar.swOffsetDeg || 0) * cpd);
  asSetText('sw-offset', (statusBar.swOffsetDeg || 0).toFixed(2) + '° (' + counts + ' counts)');
  asSetText('sw-confidence', Math.round(statusBar.swConfidence || 0) + '%');
  // Button enable: gated by control + per-button state (start when stopped, stop when
  // collecting, apply when a valid calibration exists).
  const gate = (id, ok) => { const el = document.getElementById(id); if (el) el.classList.toggle('disabled', !(iHoldControl && ok)); };
  gate('sw-reset', true);
  gate('sw-start', !collecting);
  gate('sw-stop', collecting);
  gate('sw-apply', !!statusBar.swValid);
}

// ---- Network IO (Phase 9) — modules / scan / subnet / host IPs / NTRIP. Reads ride
// the Status frame; writes are config.set (module-present, Tier-1), net.scan (Tier-1),
// net.subnet (Tier-2 gated), and ntrip.* profile CRUD. The NTRIP editor keeps its
// editing buffer client-side; Save sends every field at once. ----
const nioPanel = document.getElementById('networkio');
nioPanel.addEventListener('pointerdown', e => e.stopPropagation());
for (const cb of nioPanel.querySelectorAll('.nio-chk'))
  cb.addEventListener('change', () => cfgSend(cb.dataset.key, cb.checked ? '1' : '0'));
document.getElementById('nio-scan').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('net.scan'); });
const nioO1 = document.getElementById('nio-o1'), nioO2 = document.getElementById('nio-o2'), nioO3 = document.getElementById('nio-o3');
const nioSubnetBtn = document.getElementById('nio-subnet');
nioSubnetBtn.addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (!iHoldControl) return; // gated; host re-checks
  const o1 = clampOct(nioO1.value), o2 = clampOct(nioO2.value), o3 = clampOct(nioO3.value);
  showConfirm('Change Module Subnet',
    'Set ALL connected modules to subnet ' + o1 + '.' + o2 + '.' + o3 + '.x and restart them?',
    () => transport.send('net.subnet|' + o1 + '.' + o2 + '.' + o3));
});
document.getElementById('nio-ntprofiles').addEventListener('pointerdown', e => { e.stopPropagation(); openNtripProfiles(); });
// --- Module connection: UDP (always on) vs a serial/USB module port. Serial
// state comes from /api/serial, polled while the panel is open.
let nioSerial = null, nioSerialPoll = null;
function nioSerialTick() {
  if (!nioPanel.classList.contains('open')) { clearInterval(nioSerialPoll); nioSerialPoll = null; return; }
  fetch('/api/serial').then(r => r.json()).then(j => { nioSerial = j; renderNioSerial(); }).catch(() => {});
}
// --- Rate-control rows: RC modules + the physical switchbox, polled from the
// rate plane while the panel is open (same lifecycle as the serial poll).
// Dot: green = frames arriving; red = a module an ENABLED product needs is
// silent; grey = known but idle / no product needs it.
let nioRcPoll = null;
function nioRcTick() {
  if (!nioPanel.classList.contains('open')) { clearInterval(nioRcPoll); nioRcPoll = null; return; }
  Promise.all([
    fetch('/api/ratecontrol').then(r => r.json()),
    fetch('/api/ratemodules').then(r => r.json())
  ]).then(([rc, mods]) => renderNioRc(rc, mods)).catch(() => {});
}
function renderNioRc(rc, mods) {
  const label = document.getElementById('nio-rc-label');
  const grid = document.getElementById('nio-rc-grid');
  const show = rc && rc.useRateControl;
  label.style.display = show ? '' : 'none';
  grid.style.display = show ? '' : 'none';
  const almRowEl = document.getElementById('nio-alm-row');
  if (almRowEl) almRowEl.style.display = show ? '' : 'none';
  if (!show) return;
  const heard = new Set((mods && mods.modulesHeard) || []);
  const ips = (mods && mods.moduleIps) || {};
  const expected = new Set((rc.products || []).filter(p => p.enabled).map(p => p.moduleId));
  let html = '';
  for (const m of (mods && mods.modules) || []) {
    const ok = heard.has(m.moduleId);
    const col = ok ? '#22c55e' : (expected.has(m.moduleId) ? '#ef4444' : '#6b7280');
    html += '<span></span><span class="nio-dot" style="background:' + col + '"></span>'
          + '<span class="nio-mod">Rate ' + m.moduleId + ' (' + (m.board || 'esp32') + ')</span>'
          + '<span class="nio-ip">' + (ips[m.moduleId] || '—') + '</span>';
  }
  const p = rc.switchbox && rc.switchbox.physical;
  const sbOk = !!(p && p.connected);
  const sbExp = !!(p && p.expected);
  html += '<input type="checkbox" class="nio-chk" id="nio-sb-exp"'
        + (sbExp ? ' checked' : '')
        + ' title="Switchbox should be present on this tool — alarm when it is missing">'
        + '<span class="nio-dot" style="background:' + (sbOk ? '#22c55e' : (sbExp ? '#ef4444' : '#6b7280')) + '"></span>'
        + '<span class="nio-mod">Switchbox</span>'
        + '<span class="nio-ip">' + (sbOk && p.inoId ? 'ino ' + p.inoId : '—') + '</span>';
  if (grid.dataset.html !== html) {
    grid.innerHTML = html;
    grid.dataset.html = html;
    const cb = document.getElementById('nio-sb-exp');
    if (cb) cb.addEventListener('change', () => {
      swbSend('rate.sw|expectPhysical,' + (cb.checked ? 1 : 0));
      setTimeout(nioRcTick, 400);
    });
  }
  const almRow = document.getElementById('nio-alm-row');
  if (almRow) almRow.style.display = show ? '' : 'none';
  const bell = document.getElementById('nio-alm-bell');
  if (bell) bell.classList.toggle('live', rcAlmActive.length > 0);
}
// Alarm toggles (persisted per device, default both on).
{
  const scr = document.getElementById('nio-alm-scr'), snd = document.getElementById('nio-alm-snd');
  const sync = () => { scr.classList.toggle('on', rcAlmScreen); snd.classList.toggle('on', rcAlmSound); };
  scr.addEventListener('pointerdown', e => {
    e.stopPropagation();
    rcAlmScreen = !rcAlmScreen; localStorage.rcAlmScreen = rcAlmScreen ? '1' : '0';
    sync(); rcAlmSet(rcAlmActive);
  });
  snd.addEventListener('pointerdown', e => {
    e.stopPropagation();
    rcAlmSound = !rcAlmSound; localStorage.rcAlmSound = rcAlmSound ? '1' : '0';
    sync(); rcAlmSet(rcAlmActive);
  });
  sync();
}
function renderNioSerial() {
  const j = nioSerial; if (!j) return;
  document.getElementById('nio-conn-udp').dataset.active = j.enabled ? 'false' : 'true';
  document.getElementById('nio-conn-serial').dataset.active = j.enabled ? 'true' : 'false';
  document.getElementById('nio-serialcfg').style.display = j.enabled ? '' : 'none';
  const sel = document.getElementById('nio-serport');
  if (document.activeElement !== sel) {
    const ports = j.ports || [];
    const want = ports.map(p => '<option>' + p + '</option>').join('') || '<option value="">(no ports)</option>';
    if (sel.dataset.opts !== want) { sel.innerHTML = want; sel.dataset.opts = want; }
    if (j.port) sel.value = j.port;
  }
  const baud = document.getElementById('nio-serbaud');
  if (document.activeElement !== baud && j.baud) baud.value = String(j.baud);
  document.getElementById('nio-serstatus').textContent =
    !j.enabled ? '—'
    : j.open ? ('Open — ' + (j.rxFrames || 0) + ' frames in, ' + (j.txFrames || 0) + ' out')
    : ('Not open' + (j.error ? ' — ' + j.error : ''));
}
function nioSerialSendCfg() {
  const port = document.getElementById('nio-serport').value;
  const baud = document.getElementById('nio-serbaud').value || '38400';
  if (port) transport.send('serial.cfg|' + port + ',' + baud);
  setTimeout(nioSerialTick, 400);
}
document.getElementById('nio-conn-udp').addEventListener('pointerdown', e => {
  e.stopPropagation(); transport.send('serial.enable|0'); setTimeout(nioSerialTick, 400);
});
document.getElementById('nio-conn-serial').addEventListener('pointerdown', e => {
  e.stopPropagation(); transport.send('serial.enable|1'); nioSerialSendCfg();
});
document.getElementById('nio-serport').addEventListener('change', nioSerialSendCfg);
document.getElementById('nio-serbaud').addEventListener('change', nioSerialSendCfg);
function clampOct(v) { let n = parseInt(v); if (!Number.isFinite(n)) n = 0; return Math.max(0, Math.min(255, n)); }
// Alive is always green (even unticked); ticked = "expected here", so a
// missing expected module goes red while an unticked absent one stays grey.
function nioDotColor(configured, ok) { return ok ? '#22c55e' : (configured ? '#ef4444' : '#6b7280'); }
let _lastModuleSubnet = '';
function renderNetworkIo() {
  const s = statusBar; if (!s) return;
  const setMod = (cfgId, dotId, ipId, configured, ok, ip) => {
    const cb = document.getElementById(cfgId);
    if (document.activeElement !== cb) cb.checked = !!configured;
    document.getElementById(dotId).style.background = nioDotColor(configured, ok);
    document.getElementById(ipId).textContent = ip || '—';
  };
  setMod('nio-gps-cfg', 'nio-gps-dot', 'nio-gps-ip', s.gpsConf, s.gpsOk, s.gpsIp);
  setMod('nio-as-cfg', 'nio-as-dot', 'nio-as-ip', s.autoSteerConf, s.autoSteerOk, s.autoSteerIp);
  setMod('nio-ma-cfg', 'nio-ma-dot', 'nio-ma-ip', s.machineConf, s.machineOk, s.machineIp);
  setMod('nio-imu-cfg', 'nio-imu-dot', 'nio-imu-ip', s.imuConf, s.imuOk, s.imuIp);
  // Seed the subnet octets from the detected /24 when it changes (a PGN 203 reply),
  // unless the operator is editing that box — mirrors the native ModuleSubnet binding.
  if (s.moduleSubnet && s.moduleSubnet !== _lastModuleSubnet) {
    _lastModuleSubnet = s.moduleSubnet;
    const p = s.moduleSubnet.split('.');
    if (p.length === 3) {
      if (document.activeElement !== nioO1) nioO1.value = p[0];
      if (document.activeElement !== nioO2) nioO2.value = p[1];
      if (document.activeElement !== nioO3) nioO3.value = p[2];
    }
  }
  if (!nioO1.value && document.activeElement !== nioO1) nioO1.value = 192;
  if (!nioO2.value && document.activeElement !== nioO2) nioO2.value = 168;
  if (!nioO3.value && document.activeElement !== nioO3) nioO3.value = 5;
  document.getElementById('nio-hostips').textContent = s.hostIps || '—';
  document.getElementById('nio-ntrip-dot').style.background = s.ntripConnected ? '#22c55e' : '#6b7280';
  document.getElementById('nio-ntrip-status').textContent = s.ntripStatus || 'Not Connected';
  document.getElementById('nio-ntrip-bytes').textContent = Math.floor((s.ntripBytes || 0) / 1024).toLocaleString() + ' KB';
  nioSubnetBtn.classList.toggle('disabled', !iHoldControl);
}

// NTRIP Profiles — chain sub-panel of Network IO (mirrors NtripProfilesDialogPanel).
// Native chain model: opening REPLACES the parent (lnOpen closes everything else);
// Back reopens the parent fly-out (Network IO); Close → map.
let ntSelId = null;
function openNtripProfiles() { lnOpen('ntripprofiles', 'ln-network', renderNtripList); }
document.getElementById('nt-back').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('networkio', 'ln-network', renderNetworkIo); });
document.getElementById('nt-x').addEventListener('pointerdown', e => { e.stopPropagation(); lnCloseAll(); });
document.getElementById('nt-add').addEventListener('pointerdown', e => { e.stopPropagation(); openNtripEditor(null); });
document.getElementById('nt-edit').addEventListener('pointerdown', e => { e.stopPropagation(); const p = selectedNtProfile(); if (p) openNtripEditor(p); });
document.getElementById('nt-del').addEventListener('pointerdown', e => {
  e.stopPropagation(); const p = selectedNtProfile();
  if (p) showConfirm('Delete NTRIP Profile', "Delete NTRIP profile '" + p.name + "'?",
    () => { transport.send('ntrip.delete|' + p.id); ntSelId = null; });
});
document.getElementById('nt-default').addEventListener('pointerdown', e => {
  e.stopPropagation(); const p = selectedNtProfile(); if (p) transport.send('ntrip.setDefault|' + p.id);
});
function selectedNtProfile() { return (ntripProfiles && ntripProfiles.profiles.find(p => p.id === ntSelId)) || null; }
function renderNtripList() {
  const list = document.getElementById('nt-list');
  list.innerHTML = '';
  const profs = ntripProfiles ? ntripProfiles.profiles : [];
  if (!profs.length) { list.innerHTML = '<div style="padding:18px;color:#8294ab;text-align:center">No profiles</div>'; return; }
  for (const p of profs) {
    const row = document.createElement('div');
    row.className = 'nio-ntrow' + (p.id === ntSelId ? ' sel' : '');
    row.innerHTML = '<div><div class="nt-name"></div><div class="nt-mount"></div></div>'
      + '<div class="nt-caster"></div><div class="nt-def">' + (p.isDefault ? '★' : '') + '</div>';
    row.querySelector('.nt-name').textContent = p.name;
    row.querySelector('.nt-mount').textContent = p.mountPoint;
    row.querySelector('.nt-caster').textContent = p.casterHost;
    row.addEventListener('pointerdown', ev => { ev.stopPropagation(); ntSelId = p.id; renderNtripList(); });
    list.appendChild(row);
  }
}

// Edit NTRIP Profile — chain sub-panel of the profiles list (mirrors
// NtripProfileEditorPanel). Opening REPLACES the list; Back reopens the list; Close →
// map; Save returns to the list (native NavigateBack). Client-side buffer.
let nteBuf = null, _nteTestActive = false;
function openNtripEditor(p) {
  nteBuf = p
    ? { id: p.id, associatedFields: (p.associatedFields || []).slice() }
    : { id: '', associatedFields: [] };
  document.getElementById('nte-title').textContent = p ? 'Edit NTRIP Profile' : 'New NTRIP Profile';
  document.getElementById('nte-name').value = p ? p.name : 'New Profile';
  document.getElementById('nte-host').value = p ? p.casterHost : '';
  document.getElementById('nte-port').value = p ? p.casterPort : 2101;
  document.getElementById('nte-mount').value = p ? p.mountPoint : '';
  document.getElementById('nte-user').value = p ? p.username : '';
  document.getElementById('nte-pass').value = p ? p.password : '';
  document.getElementById('nte-auto').checked = p ? !!p.autoConnect : true;
  document.getElementById('nte-default').checked = p ? !!p.isDefault : false;
  document.getElementById('nte-teststatus').textContent = '';
  _nteTestActive = false;
  lnOpen('ntripeditor', 'ln-network', renderNteFields);
}
document.getElementById('nte-back').addEventListener('pointerdown', e => { e.stopPropagation(); openNtripProfiles(); });
document.getElementById('nte-x').addEventListener('pointerdown', e => { e.stopPropagation(); lnCloseAll(); });
function renderNteFields() {
  const box = document.getElementById('nte-fields');
  box.innerHTML = '';
  const fields = ntripProfiles ? ntripProfiles.availableFields : [];
  if (!fields.length) { box.innerHTML = '<div style="color:#8294ab;padding:4px">No fields</div>'; return; }
  for (const f of fields) {
    const lbl = document.createElement('label');
    const cb = document.createElement('input'); cb.type = 'checkbox';
    cb.checked = nteBuf.associatedFields.includes(f);
    cb.addEventListener('change', () => {
      if (cb.checked) { if (!nteBuf.associatedFields.includes(f)) nteBuf.associatedFields.push(f); }
      else nteBuf.associatedFields = nteBuf.associatedFields.filter(x => x !== f);
    });
    lbl.appendChild(cb); lbl.appendChild(document.createTextNode(f));
    box.appendChild(lbl);
  }
}
document.getElementById('nte-test').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const host = document.getElementById('nte-host').value.trim();
  const port = document.getElementById('nte-port').value || '2101';
  const mount = document.getElementById('nte-mount').value.trim();
  const user = document.getElementById('nte-user').value, pass = document.getElementById('nte-pass').value;
  const st = document.getElementById('nte-teststatus');
  if (!host) { st.textContent = 'Error: Caster host is required'; return; }
  if (!mount) { st.textContent = 'Error: Mount point is required'; return; }
  _nteTestActive = true;
  st.textContent = 'Testing connection...';
  transport.send('ntrip.test|' + [host, port, mount, user, pass].join('\t'));
});
document.getElementById('nte-save').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (!nteBuf) return;
  const fields = [
    nteBuf.id || '',
    document.getElementById('nte-name').value.trim(),
    document.getElementById('nte-host').value.trim(),
    document.getElementById('nte-port').value || '2101',
    document.getElementById('nte-mount').value.trim(),
    document.getElementById('nte-user').value,
    document.getElementById('nte-pass').value,
    document.getElementById('nte-auto').checked ? '1' : '0',
    document.getElementById('nte-default').checked ? '1' : '0',
    nteBuf.associatedFields.join(','),
  ];
  transport.send('ntrip.save|' + fields.join('\t'));
  openNtripProfiles(); // native SaveNtripProfileCommand ends in NavigateBack → list
});

// ---- Field Operations (Phase 9) — field/job lifecycle. Fly-out launches the
// Fields-and-Jobs chain panel (fields table + jobs + new-job form) and New Field.
// Reads ride the FieldOps frame; writes are field.* commands (host-driven via the real
// StartWorkSessionDialogViewModel). Tier-1 (data management); deletes confirm client-side.
const JOB_STATUS = ['In progress', 'Done', 'Abandoned'];
let fjSelField = null, fjSelJob = null;

// Field Operations fly-out: status pill + Close gating from the live scene/status.
function renderFieldOps() {
  const has = !!(scene && scene.hasField);
  const pill = document.getElementById('fo-current');
  if (has) {
    const fld = (scene && scene.fieldName) || '—';
    const job = statusBar && statusBar.jobName;
    pill.textContent = 'Current: ' + fld + (job ? ' / ' + job : '');
    pill.style.display = 'block';
  } else pill.style.display = 'none';
  document.getElementById('fo-close').classList.toggle('disabled', !has);
}
document.getElementById('fo-fields').addEventListener('pointerdown', e => { e.stopPropagation(); openFieldsAndJobs(); });
document.getElementById('fo-resumelast').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('field.resumeLast'); lnCloseAll(); });
document.getElementById('fo-resumejob').addEventListener('pointerdown', e => { e.stopPropagation(); openResumeJob(); });
document.getElementById('fo-drivein').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('field.driveIn'); lnCloseAll(); });
document.getElementById('fo-close').addEventListener('pointerdown', e => { e.stopPropagation(); if (scene && scene.hasField) { transport.send('field.close'); lnCloseAll(); } });

// Fields-and-Jobs chain panel (mirrors StartWorkSessionDialogPanel).
function openFieldsAndJobs() {
  if (fieldOps && !fjSelField)
    fjSelField = fieldOps.activeField || (fieldOps.fields[0] && fieldOps.fields[0].name) || null;
  lnOpen('fieldsandjobs', 'ln-fieldops', renderFieldsAndJobs);
}
document.getElementById('fj-back').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('fieldops', 'ln-fieldops', renderFieldOps); });
// Pick on Map — close the panel so the map is tappable, then arm the picker; the tap
// selects the field here and defaults to its last job (see pickFieldOnMap).
document.getElementById('fj-pickmap').addEventListener('pointerdown', e => { e.stopPropagation(); lnCloseAll(); pickFieldOnMap(); });
function fjJobsArr() { return fieldOps ? fieldOps.jobs.filter(j => j.fieldName === fjSelField) : []; }
function renderFieldsAndJobs() {
  const fl = document.getElementById('fj-fieldlist'); fl.innerHTML = '';
  for (const f of (fieldOps ? fieldOps.fields : [])) {
    const row = document.createElement('div');
    row.className = 'fj-frow' + (f.name === fjSelField ? ' sel' : '');
    row.innerHTML = '<span class="fj-fname"></span><span class="fj-fnum"></span><span class="fj-fnum"></span>';
    row.querySelector('.fj-fname').textContent = f.name;
    const nums = row.querySelectorAll('.fj-fnum');
    nums[0].textContent = f.hasDistance ? f.distanceKm.toFixed(1) : '—';
    nums[1].textContent = f.areaHa.toFixed(1);
    row.addEventListener('pointerdown', ev => { ev.stopPropagation(); fjSelField = f.name; fjSelJob = null; renderFieldsAndJobs(); });
    fl.appendChild(row);
  }
  const sel = !!fjSelField;
  document.getElementById('fj-jobside').style.display = sel ? 'block' : 'none';
  document.getElementById('fj-empty').style.display = sel ? 'none' : 'block';
  if (sel) {
    const jl = document.getElementById('fj-joblist'); jl.innerHTML = '';
    for (const j of fjJobsArr()) {
      const row = document.createElement('div');
      row.className = 'fj-jrow' + (j.taskName === fjSelJob ? ' sel' : '');
      row.innerHTML = '<div class="fj-jtop"><span class="fj-jname"></span><span class="fj-jwt"></span><span class="fj-jst"></span></div><div class="fj-jsub"></div>';
      row.querySelector('.fj-jname').textContent = j.taskName;
      row.querySelector('.fj-jwt').textContent = j.workType;
      row.querySelector('.fj-jst').textContent = JOB_STATUS[j.status] || '';
      row.querySelector('.fj-jsub').textContent = 'Last opened: ' + j.lastOpened;
      row.addEventListener('pointerdown', ev => { ev.stopPropagation(); fjSelJob = j.taskName; renderFieldsAndJobs(); });
      jl.appendChild(row);
    }
    const dl = document.getElementById('fj-wtlist'); dl.innerHTML = '';
    for (const w of (fieldOps ? fieldOps.workTypes : [])) { const o = document.createElement('option'); o.value = w; dl.appendChild(o); }
    const tn = document.getElementById('fj-taskname');
    if (!tn.value && document.activeElement !== tn) tn.value = isoDate();
  }
}
function isoDate() { const d = new Date(), p = n => String(n).padStart(2, '0'); return d.getFullYear() + '-' + p(d.getMonth() + 1) + '-' + p(d.getDate()); }
function isoTime() { const d = new Date(), p = n => String(n).padStart(2, '0'); return p(d.getHours()) + '-' + p(d.getMinutes()); }
// New-job form helpers
document.getElementById('fj-uselast').addEventListener('pointerdown', e => { e.stopPropagation(); const j = fjJobsArr()[0]; if (j) document.getElementById('fj-notes').value = j.notes; });
document.getElementById('fj-date').addEventListener('pointerdown', e => { e.stopPropagation(); const tn = document.getElementById('fj-taskname'); tn.value = (tn.value ? tn.value + '_' : '') + isoDate(); });
document.getElementById('fj-time').addEventListener('pointerdown', e => { e.stopPropagation(); const tn = document.getElementById('fj-taskname'); tn.value = (tn.value ? tn.value + '_' : '') + isoTime(); });
// Footer actions
document.getElementById('fj-openonly').addEventListener('pointerdown', e => { e.stopPropagation(); if (fjSelField) { transport.send('field.openOnly|' + fjSelField); lnCloseAll(); } });
document.getElementById('fj-deletefield').addEventListener('pointerdown', e => {
  e.stopPropagation(); if (!fjSelField) return;
  showConfirm('Delete Field', "Delete field '" + fjSelField + "' and all its jobs?", () => { transport.send('field.deleteField|' + fjSelField); fjSelField = null; fjSelJob = null; });
});
document.getElementById('fj-resumejob').addEventListener('pointerdown', e => { e.stopPropagation(); if (fjSelField && fjSelJob) { transport.send('field.resumeJob|' + fjSelField + '\t' + fjSelJob); lnCloseAll(); } });
document.getElementById('fj-startjob').addEventListener('pointerdown', e => {
  e.stopPropagation(); if (!fjSelField) return;
  const wt = document.getElementById('fj-worktype').value.trim();
  const notes = document.getElementById('fj-notes').value;
  const task = document.getElementById('fj-taskname').value.trim();
  transport.send('field.startJob|' + [fjSelField, wt, notes, task].join('\t'));
  lnCloseAll();
});
document.getElementById('fj-deletejob').addEventListener('pointerdown', e => {
  e.stopPropagation(); if (!fjSelField || !fjSelJob) return;
  const job = fjSelJob;
  showConfirm('Delete Job', "Delete job '" + job + "' from field '" + fjSelField + "'?", () => { transport.send('field.deleteJob|' + fjSelField + '\t' + job); fjSelJob = null; });
});
// New Field chain panel (Creation column)
document.getElementById('fj-newfield').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('newfield', 'ln-fieldops', () => { document.getElementById('nf-name').value = ''; renderNewField(); }); });
document.getElementById('fj-fromexisting').addEventListener('pointerdown', e => { e.stopPropagation(); openFromExisting(); });
document.getElementById('fj-iso').addEventListener('pointerdown', e => { e.stopPropagation(); openImport('isoimport', 'iso-file', 'isoFiles'); });
document.getElementById('fj-kml').addEventListener('pointerdown', e => { e.stopPropagation(); openImport('kmlimport', 'kml-file', 'kmlFiles'); });
// Show the origin the field will be created at — the live GPS, or the host's
// no-fix fallback (40.7128, -74.0060) so the readout matches what gets written.
function renderNewField() {
  const hasFix = statusBar && (statusBar.lat || statusBar.lon);
  const lat = hasFix ? statusBar.lat : 40.7128, lon = hasFix ? statusBar.lon : -74.0060;
  document.getElementById('nf-pos').textContent = lat.toFixed(8) + ', ' + lon.toFixed(8);
  document.getElementById('nf-poshint').textContent = hasFix
    ? 'The field is created at this position.'
    : 'No GPS fix — a default origin will be used.';
}
document.getElementById('nf-back').addEventListener('pointerdown', e => { e.stopPropagation(); openFieldsAndJobs(); });
document.getElementById('nf-create').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const name = document.getElementById('nf-name').value.trim();
  if (!name) return;
  transport.send('field.new|' + name);
  fjSelField = name;
  openFieldsAndJobs(); // back to the list; the new field appears once the frame updates
});

// Helper: fill a <select> with options from a string array (preserving the choice).
function fillSelect(sel, items) {
  const prev = sel.value;
  sel.innerHTML = '';
  for (const it of items) { const o = document.createElement('option'); o.value = it; o.textContent = it; sel.appendChild(o); }
  if (items.includes(prev)) sel.value = prev;
}

// From Existing — clone a field (source + new name + copy toggles).
document.getElementById('fe-back').addEventListener('pointerdown', e => { e.stopPropagation(); openFieldsAndJobs(); });
for (const b of document.querySelectorAll('#fromexisting .cfg-tgl'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); b.classList.toggle('active'); });
function openFromExisting() {
  lnOpen('fromexisting', 'ln-fieldops', () => {
    fillSelect(document.getElementById('fe-source'), (fieldOps ? fieldOps.fields : []).map(f => f.name));
    document.getElementById('fe-name').value = '';
    for (const b of document.querySelectorAll('#fromexisting .cfg-tgl')) b.classList.add('active'); // all copy on by default
  });
}
document.getElementById('fe-create').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const src = document.getElementById('fe-source').value;
  const name = document.getElementById('fe-name').value.trim();
  if (!src || !name) return;
  const on = id => document.getElementById(id).classList.contains('active') ? '1' : '0';
  transport.send('field.fromExisting|' + [src, name, on('fe-flags'), on('fe-mapping'), on('fe-headland'), on('fe-lines')].join('\t'));
  fjSelField = name; openFieldsAndJobs();
});

// From ISO-XML / From KML — import a field from a file in the Import folder.
function openImport(panelId, selId, fileArr) {
  lnOpen(panelId, 'ln-fieldops', () => {
    fillSelect(document.getElementById(selId), fieldOps ? fieldOps[fileArr] : []);
    document.getElementById(panelId === 'isoimport' ? 'iso-name' : 'kml-name').value = '';
  });
}
document.getElementById('iso-back').addEventListener('pointerdown', e => { e.stopPropagation(); openFieldsAndJobs(); });
document.getElementById('kml-back').addEventListener('pointerdown', e => { e.stopPropagation(); openFieldsAndJobs(); });
document.getElementById('iso-create').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const file = document.getElementById('iso-file').value, name = document.getElementById('iso-name').value.trim();
  if (!file || !name) return;
  transport.send('field.fromIsoXml|' + file + '\t' + name); fjSelField = name; openFieldsAndJobs();
});
document.getElementById('kml-create').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const file = document.getElementById('kml-file').value, name = document.getElementById('kml-name').value.trim();
  if (!file || !name) return;
  transport.send('field.fromKml|' + file + '\t' + name); fjSelField = name; openFieldsAndJobs();
});

// Cross-field Resume Job picker (all jobs, recency order from the host).
function openResumeJob() { lnOpen('resumejob', 'ln-fieldops', renderResumeJob); }
document.getElementById('rj-back').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('fieldops', 'ln-fieldops', renderFieldOps); });
function renderResumeJob() {
  const list = document.getElementById('rj-list'); list.innerHTML = '';
  const jobs = fieldOps ? fieldOps.jobs : [];
  if (!jobs.length) { list.innerHTML = '<div class="fj-empty">No jobs yet.</div>'; return; }
  for (const j of jobs) {
    const row = document.createElement('div');
    row.className = 'fj-jrow';
    row.innerHTML = '<div class="fj-jtop"><span class="fj-jname"></span><span class="fj-jwt"></span><span class="fj-jst"></span></div><div class="fj-jsub"></div>';
    row.querySelector('.fj-jname').textContent = j.taskName;
    row.querySelector('.fj-jwt').textContent = j.fieldName;
    row.querySelector('.fj-jst').textContent = JOB_STATUS[j.status] || '';
    row.querySelector('.fj-jsub').textContent = 'Last opened: ' + j.lastOpened;
    row.addEventListener('pointerdown', ev => { ev.stopPropagation(); transport.send('field.resumeJob|' + j.fieldName + '\t' + j.taskName); lnCloseAll(); });
    list.appendChild(row);
  }
}

// ---- AgShare cloud sync (Settings / Upload / Download) — chain sub-panels of Field
// Operations. Settings via config.set conn.agShare*; actions via agshare.* with results
// (status / cloud list) riding the AgShare frame. Launched from the fly-out's AgShare row.
document.getElementById('fo-agupload').addEventListener('pointerdown', e => { e.stopPropagation(); openAgUpload(); });
document.getElementById('fo-agdownload').addEventListener('pointerdown', e => { e.stopPropagation(); openAgDownload(); });
document.getElementById('fo-agapi').addEventListener('pointerdown', e => { e.stopPropagation(); openAgSettings(); });

// Settings
function openAgSettings() { lnOpen('agsettings', 'ln-fieldops', renderAgSettings); }
document.getElementById('ag-back').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('fieldops', 'ln-fieldops', renderFieldOps); });
function renderAgSettings() {
  if (!agShare) return;
  const sv = document.getElementById('ag-server'), kv = document.getElementById('ag-key');
  if (document.activeElement !== sv) sv.value = agShare.serverUrl || '';
  if (document.activeElement !== kv) kv.value = agShare.apiKey || '';
  document.getElementById('ag-enabled').classList.toggle('active', !!agShare.enabled);
  document.getElementById('ag-teststatus').textContent = agShare.status || '';
}
document.getElementById('ag-server').addEventListener('change', () => cfgSend('conn.agShareServer', document.getElementById('ag-server').value.trim()));
document.getElementById('ag-key').addEventListener('change', () => cfgSend('conn.agShareApiKey', document.getElementById('ag-key').value.trim()));
document.getElementById('ag-enabled').addEventListener('pointerdown', e => { e.stopPropagation(); cfgSend('conn.agShareEnabled', e.currentTarget.classList.contains('active') ? '0' : '1'); });
document.getElementById('ag-test').addEventListener('pointerdown', e => {
  e.stopPropagation();
  cfgSend('conn.agShareServer', document.getElementById('ag-server').value.trim());
  cfgSend('conn.agShareApiKey', document.getElementById('ag-key').value.trim());
  transport.send('agshare.test');
});

// Upload
let aguSel = new Set();
function openAgUpload() { aguSel = new Set(); lnOpen('agupload', 'ln-fieldops', renderAgUpload); }
document.getElementById('agu-back').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('fieldops', 'ln-fieldops', renderFieldOps); });
function renderAgUpload() {
  const list = document.getElementById('agu-list'); list.innerHTML = '';
  for (const f of (agShare ? agShare.localFields : [])) {
    const row = document.createElement('label'); row.className = 'agu-row';
    const cb = document.createElement('input'); cb.type = 'checkbox'; cb.checked = aguSel.has(f.name);
    cb.addEventListener('change', () => { if (cb.checked) aguSel.add(f.name); else aguSel.delete(f.name); });
    const nm = document.createElement('span'); nm.className = 'agu-name'; nm.textContent = f.name;
    const bd = document.createElement('span'); bd.className = 'agu-bd ' + (f.hasBoundary ? 'ok' : 'no'); bd.textContent = f.hasBoundary ? 'Has boundary' : 'No boundary';
    row.appendChild(cb); row.appendChild(nm); row.appendChild(bd); list.appendChild(row);
  }
  document.getElementById('agu-status').textContent = agShare ? (agShare.status || '') : '';
}
document.getElementById('agu-selall').addEventListener('pointerdown', e => { e.stopPropagation(); for (const f of (agShare ? agShare.localFields : [])) aguSel.add(f.name); renderAgUpload(); });
document.getElementById('agu-selnone').addEventListener('pointerdown', e => { e.stopPropagation(); aguSel.clear(); renderAgUpload(); });
document.getElementById('agu-public').addEventListener('pointerdown', e => { e.stopPropagation(); e.currentTarget.classList.toggle('active'); });
document.getElementById('agu-upload').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (!aguSel.size) return;
  const pub = document.getElementById('agu-public').classList.contains('active') ? '1' : '0';
  transport.send('agshare.upload|' + [pub, ...aguSel].join('\t'));
});

// Download
let agdSel = null;
function openAgDownload() { agdSel = null; lnOpen('agdownload', 'ln-fieldops', () => { transport.send('agshare.fetch'); renderAgDownload(); }); }
document.getElementById('agd-back').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('fieldops', 'ln-fieldops', renderFieldOps); });
function renderAgDownload() {
  const list = document.getElementById('agd-list'); list.innerHTML = '';
  const cf = agShare ? agShare.cloudFields : [];
  if (!cf.length) list.innerHTML = '<div class="fj-empty">No cloud fields (Refresh to load).</div>';
  for (const f of cf) {
    const row = document.createElement('div'); row.className = 'fj-jrow' + (f.id === agdSel ? ' sel' : '');
    row.innerHTML = '<div class="fj-jtop"><span class="fj-jname"></span><span class="fj-jwt"></span></div>';
    row.querySelector('.fj-jname').textContent = f.name;
    row.querySelector('.fj-jwt').textContent = f.areaHa.toFixed(2) + ' ha';
    row.addEventListener('pointerdown', ev => { ev.stopPropagation(); agdSel = f.id; renderAgDownload(); });
    list.appendChild(row);
  }
  document.getElementById('agd-status').textContent = agShare ? (agShare.status || '') : '';
}
document.getElementById('agd-refresh').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('agshare.fetch'); });
document.getElementById('agd-download').addEventListener('pointerdown', e => { e.stopPropagation(); if (agdSel) transport.send('agshare.download|' + agdSel); });
document.getElementById('agd-force').addEventListener('pointerdown', e => { e.stopPropagation(); e.currentTarget.classList.toggle('active'); });
document.getElementById('agd-downloadall').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('agshare.downloadAll|' + (document.getElementById('agd-force').classList.contains('active') ? '1' : '0')); });

// ---- File / Application Menu (Phase 9) — settings/tools launcher. Reads ride the AppInfo
// frame + config/status; writes are config.set (App Settings) + app.* (language/reset/
// hotkeys/bug-report). Sub-panels are chain panels (replace the menu; Back reopens it).
document.getElementById('ln-filemenu').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const anyOpen = ['filemenu', 'appsettings', 'language', 'viewsettings', 'logviewer', 'hotkeys', 'help', 'about', 'bugreport'].some(id => document.getElementById(id).classList.contains('open'));
  if (anyOpen) lnCloseAll(); else lnOpen('filemenu', 'ln-filemenu');
});
for (const b of document.querySelectorAll('.fm-back'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('filemenu', 'ln-filemenu'); });
document.getElementById('fm-language').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('language', 'ln-filemenu', renderLanguage); });
document.getElementById('fm-reset').addEventListener('pointerdown', e => { e.stopPropagation(); showConfirm('Reset All Settings', 'Reset all settings to their defaults? This cannot be undone.', () => transport.send('app.resetSettings')); });
document.getElementById('fm-appsettings').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('appsettings', 'ln-filemenu', renderAppSettings); });
document.getElementById('fm-viewsettings').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('viewsettings', 'ln-filemenu', renderViewSettings); });
document.getElementById('fm-logviewer').addEventListener('pointerdown', e => { e.stopPropagation(); logViewerParent = 'filemenu'; lnOpen('logviewer', 'ln-filemenu', renderLogViewer); });
document.getElementById('fm-hotkeys').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('hotkeys', 'ln-filemenu', renderHotkeys); });
document.getElementById('fm-simulator').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('sim.togglePanel'); lnCloseAll(); });
document.getElementById('fm-help').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('help', 'ln-filemenu'); });
document.getElementById('fm-about').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('about', 'ln-filemenu', renderAbout); });
document.getElementById('fm-bugreport').addEventListener('pointerdown', e => { e.stopPropagation(); lnOpen('bugreport', 'ln-filemenu', renderBugReport); });

// App Settings: units (status) + device toggles (config.display) + app directories (appInfo).
function renderAppSettings() {
  const metric = !!(statusBar && statusBar.isMetric);
  document.getElementById('as-metric').classList.toggle('active', metric);
  document.getElementById('as-imperial').classList.toggle('active', !metric);
  const d = config && config.display;
  if (d) for (const b of document.querySelectorAll('#appsettings .as-tgl')) b.classList.toggle('active', !!d[b.dataset.key.split('.')[1]]);
  const dl = document.getElementById('as-dirs'); dl.innerHTML = '';
  for (const dir of (appInfo ? appInfo.directories : [])) {
    const row = document.createElement('div'); row.className = 'as-dir';
    row.innerHTML = '<div class="as-dirname"></div><div class="as-dirpath"></div>';
    row.querySelector('.as-dirname').textContent = dir.name + (dir.exists ? '' : ' (missing)');
    row.querySelector('.as-dirpath').textContent = dir.path;
    dl.appendChild(row);
  }
}
for (const b of document.querySelectorAll('#appsettings .ln-segbtn[data-units]'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('config.set|units:' + b.dataset.units); });
for (const b of document.querySelectorAll('#appsettings .as-tgl'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); cfgSend(b.dataset.key, b.classList.contains('active') ? '0' : '1'); });

// Language picker
function renderLanguage() {
  const list = document.getElementById('lang-list'); list.innerHTML = '';
  const cur = appInfo ? appInfo.currentLanguage : 'en';
  for (const l of (appInfo ? appInfo.languages : [])) {
    const b = document.createElement('button'); b.className = 'lang-btn' + (l.code === cur ? ' active' : ''); b.textContent = l.name;
    b.addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('app.setLanguage|' + l.code); });
    list.appendChild(b);
  }
}

// View All Settings — read-only tree rendered from the config frame the client already has.
function renderViewSettings() {
  const box = document.getElementById('vs-tree'); box.innerHTML = '';
  if (!config) { box.textContent = 'Loading…'; return; }
  const groups = { Vehicle: config.vehicle, GPS: config.gps, Roll: config.roll, Tool: config.tool, 'U-Turn': config.uturn, Tram: config.tram, Machine: config.machine, Display: config.display, AutoSteer: config.autosteer };
  for (const name in groups) {
    const obj = groups[name]; if (!obj) continue;
    const sec = document.createElement('div'); sec.className = 'vs-section';
    const h = document.createElement('div'); h.className = 'vs-group'; h.textContent = name; sec.appendChild(h);
    for (const k in obj) {
      const v = obj[k]; if (v !== null && typeof v === 'object') continue;
      const r = document.createElement('div'); r.className = 'vs-row';
      r.innerHTML = '<span class="vs-k"></span><span class="vs-v"></span>';
      r.querySelector('.vs-k').textContent = k; r.querySelector('.vs-v').textContent = String(v);
      sec.appendChild(r);
    }
    box.appendChild(sec);
  }
}

// Log Viewer — filter + list (from AppInfo).
let logFilter = 0;
const LOG_LVL = ['Trace', 'Debug', 'Info', 'Warn', 'Error', 'Crit'];
function renderLogViewer() {
  const list = document.getElementById('log-list'); list.innerHTML = '';
  for (const e of (appInfo ? appInfo.logs : [])) {
    if (e.level < logFilter) continue;
    const r = document.createElement('div'); r.className = 'log-row lvl' + e.level;
    r.innerHTML = '<span class="log-t"></span><span class="log-l"></span><span class="log-m"></span>';
    r.querySelector('.log-t').textContent = e.time;
    r.querySelector('.log-l').textContent = LOG_LVL[e.level] || '';
    r.querySelector('.log-m').textContent = e.message;
    list.appendChild(r);
  }
  list.scrollTop = list.scrollHeight;
}
for (const b of document.querySelectorAll('#logviewer .log-filt'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); logFilter = parseInt(b.dataset.lvl); for (const x of document.querySelectorAll('#logviewer .log-filt')) x.classList.toggle('active', x === b); renderLogViewer(); });

// Hotkeys — list + click-to-capture.
let hkCapture = null;
function renderHotkeys() {
  const list = document.getElementById('hk-list'); list.innerHTML = '';
  for (const hk of (appInfo ? appInfo.hotkeys : [])) {
    const r = document.createElement('div'); r.className = 'hk-row';
    r.innerHTML = '<span class="hk-label"></span><button class="hk-key"></button>';
    r.querySelector('.hk-label').textContent = hk.label;
    const kb = r.querySelector('.hk-key');
    kb.textContent = hkCapture === hk.action ? 'Press a key…' : (hk.key || '—');
    if (hkCapture === hk.action) kb.classList.add('capturing');
    kb.addEventListener('pointerdown', ev => { ev.stopPropagation(); hkCapture = hk.action; renderHotkeys(); });
    list.appendChild(r);
  }
}
window.addEventListener('keydown', e => {
  if (!hkCapture || !document.getElementById('hotkeys').classList.contains('open')) return;
  e.preventDefault();
  const key = e.key.length === 1 ? e.key.toUpperCase() : e.key;
  transport.send('app.setHotkey|' + hkCapture + ':' + key);
  hkCapture = null;
});
document.getElementById('hk-reset').addEventListener('pointerdown', e => { e.stopPropagation(); showConfirm('Reset Hotkeys', 'Reset all hotkeys to their defaults?', () => transport.send('app.resetHotkeys')); });

// Help — external links open in a new tab.
for (const b of document.querySelectorAll('#help .help-link'))
  b.addEventListener('pointerdown', e => { e.stopPropagation(); window.open(b.dataset.url, '_blank'); });

// About
function renderAbout() {
  document.getElementById('ab-version').textContent = appInfo ? ('Version ' + appInfo.version) : '';
  document.getElementById('ab-git').textContent = appInfo ? appInfo.gitHash : '';
}

// Bug Report
function renderBugReport() { document.getElementById('br-status').textContent = appInfo ? (appInfo.bugReportStatus || '') : ''; }
document.getElementById('br-submit').addEventListener('pointerdown', e => {
  e.stopPropagation();
  const t = document.getElementById('br-title').value.trim();
  if (!t) return;
  transport.send('app.bugReport|' + t + '\t' + document.getElementById('br-desc').value);
});

// ---- Steer Wizard (Phase 9) — full-screen, host-driven. The host runs the real
// SteerWizardViewModel; we rebuild #wz-content per step from the Wizard frame, edit via
// the existing config.set bridge, and forward nav (wizard.*) + gated calibration
// (wizard.action). Editable values display from the Config frame. ----
const wzOverlay = document.getElementById('wizard-overlay');
const wzContent = document.getElementById('wz-content');
let _wzKey = ''; // stepKind|index of the currently-built content (rebuild on change)
function openSteerWizard() { _wzKey = ''; wizard = null; transport.send('wizard.open'); wzOverlay.classList.add('open'); }
function wzClose(send) { if (send) transport.send('wizard.cancel'); wzOverlay.classList.remove('open'); wizard = null; _wzKey = ''; }
document.getElementById('wz-cancel').addEventListener('pointerdown', e => { e.stopPropagation(); wzClose(true); });
document.getElementById('wz-skip').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('wizard.skip'); });
document.getElementById('wz-back').addEventListener('pointerdown', e => { e.stopPropagation(); transport.send('wizard.back'); });
document.getElementById('wz-next').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (wizard && wizard.isLast) { transport.send('wizard.finish'); wzClose(false); }
  else transport.send('wizard.next');
});
// Delegated content events: data-cfg="key:val" (config.set), data-hw="n" (hardware
// level), data-act="Cmd" (gated wizard.action), data-tgl="key" (toggle), data-cfgnum
// (number input) on change.
wzContent.addEventListener('pointerdown', e => {
  const el = e.target.closest('[data-cfg],[data-hw],[data-act],[data-tgl]'); if (!el) return;
  e.stopPropagation();
  if (el.dataset.cfg) { const i = el.dataset.cfg.indexOf(':'); cfgSend(el.dataset.cfg.slice(0, i), el.dataset.cfg.slice(i + 1)); }
  else if (el.dataset.hw != null) transport.send('wizard.hw|' + el.dataset.hw);
  else if (el.dataset.act) rnSend('wizard.action|' + el.dataset.act);
  else if (el.dataset.tgl) cfgSend(el.dataset.tgl, cfgGet(el.dataset.tgl) ? '0' : '1');
});
wzContent.addEventListener('change', e => {
  const inp = e.target.closest('[data-cfgnum]'); if (!inp) return;
  const v = parseFloat(inp.value); if (Number.isFinite(v)) cfgSend(inp.dataset.cfgnum, v);
});
// Per-step content. Editable values read from the Config frame; live values get ids
// (data-live) refreshed each frame. esc() guards interpolated strings.
function esc(s) { return String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }
function wzVal(key, dflt) { const v = cfgGet(key); return typeof v === 'number' ? v : (dflt || 0); }
function wzNum(label, sub, key, step, unit) {
  return '<div class="wz-fld"><label>' + esc(label) + '</label><div class="sub">' + esc(sub || '') + '</div>' +
    '<input class="wz-num" data-cfgnum="' + key + '" type="number" step="' + (step || '0.01') + '" value="' + wzVal(key) + '"><span class="wz-unit">' + esc(unit || '') + '</span></div>';
}
function wzSeg(label, sub, key, opts) { // opts: [[text,val],...]
  return '<div class="wz-row"><div class="lbl">' + esc(label) + (sub ? '<div class="sub">' + esc(sub) + '</div>' : '') + '</div><div class="wz-seg">' +
    opts.map(o => '<button data-cfg="' + key + ':' + o[1] + '" data-activekey="' + key + '" data-activeval="' + o[1] + '">' + esc(o[0]) + '</button>').join('') + '</div></div>';
}
function wzTgl(label, sub, key) {
  return '<div class="wz-row"><div class="lbl">' + esc(label) + (sub ? '<div class="sub">' + esc(sub) + '</div>' : '') + '</div><button class="wz-tgl" data-tgl="' + key + '" data-tglkey="' + key + '">Off</button></div>';
}
function wzLive(lbl, key) { return '<div class="wz-live"><div class="big" data-live="' + key + '">—</div><div class="lbl">' + esc(lbl) + '</div></div>'; }
function buildWizardContent(w) {
  const cfg = config || {}, veh = cfg.vehicle || {}, ast = cfg.autosteer || {}, roll = cfg.roll || {};
  const ty = Math.max(0, Math.min(2, (veh.type | 0)));
  const head = '<div class="wz-h1">' + esc(w.title) + '</div><div class="wz-desc">' + esc(w.description) + '</div>';
  switch (w.stepKind) {
    case 'welcome': return head + '<div class="wz-center"><div class="wz-okcard"><div class="t">AutoSteer</div><div class="s">Configuration Wizard</div></div></div>';
    case 'vehicleType': {
      const c = (i, ic, t) => '<div class="wz-card" data-cfg="vehicle.type:' + i + '" data-activekey="vehicle.type" data-activeval="' + i + '"><img src="/icons/' + ic + '.png"><div class="t">' + t + '</div></div>';
      return head + '<div class="wz-cards">' + c(0, 'WheelbaseTractor', 'Tractor') + c(1, 'WheelbaseHarvester', 'Harvester') + c(2, 'WheelbaseArticulated', '4WD / Articulated') + '</div>';
    }
    case 'hardware': {
      const c = (i, ic, t, s) => '<div class="wz-card" data-hw="' + i + '" data-activehw="' + i + '"><img src="/icons/' + ic + '.png"><div class="t">' + t + '</div><div class="s">' + s + '</div></div>';
      return head + '<div class="wz-cards">' + c(0, 'NavigationSettings', 'GPS Only', 'Light bar guidance only') + c(1, 'AutoSteerConf', 'AutoSteer', 'Motor/valve, WAS, IMU') + c(2, 'Settings48', 'Full Setup', 'Steering + section control') + '</div>';
    }
    case 'dimensions':
      return head + '<div class="wz-panels"><div class="wz-panel"><img class="dia" src="/icons/' + ['WheelbaseTractor', 'WheelbaseHarvester', 'WheelbaseArticulated'][ty] + '.png">' + wzNum('Wheelbase', 'Front to rear axle distance', 'vehicle.wheelbase', '0.01', 'm') + '</div>' +
        '<div class="wz-panel"><img class="dia" src="/icons/' + ['TrackWidthTractor', 'TrackWidthHarvester', 'TrackWidthArticulated'][ty] + '.png">' + wzNum('Track Width', 'Left to right wheel center', 'vehicle.trackWidth', '0.01', 'm') + '</div></div>';
    case 'antenna':
      return head + '<div class="wz-panels"><div class="wz-panel"><img class="dia" src="/icons/' + ['AntennaTractorOffset', 'AntennaHarvesterOffset', 'AntennaArticulatedOffset'][ty] + '.png">' +
        wzNum('Pivot Distance', 'Antenna to rear axle (+ = ahead)', 'vehicle.antennaPivot', '0.01', 'm') + wzNum('Antenna Height', 'Above ground level', 'vehicle.antennaHeight', '0.01', 'm') + '</div>' +
        '<div class="wz-panel"><img class="dia" src="/icons/' + ['AntennaTractorTop', 'AntennaHarvesterTop', 'AntennaArticulatedTop'][ty] + '.png"><div class="wz-fld"><label>Lateral Offset</label><div class="sub">From centerline (+ = right)</div><div class="wz-seg">' +
        '<button data-cfg="vehicle.antennaSide:left">Left</button><button data-cfg="vehicle.antennaSide:center">Center</button><button data-cfg="vehicle.antennaSide:right">Right</button></div></div></div></div>';
    case 'hwconfig':
      return head + '<div class="wz-rows">' +
        wzSeg('Steer Enable Method', null, 'autosteer.externalEnable', [['None', 0], ['Switch', 1], ['Button', 2]]) +
        wzSeg('Motor Driver', null, 'autosteer.motorDriver', [['IBT2', 0], ['Cytron', 1]]) +
        wzSeg('A/D Converter', null, 'autosteer.adConverter', [['Differential', 0], ['Single', 1]]) +
        wzTgl('Invert Steer Enable Relay', 'Inverts the relay that enables the motor', 'autosteer.invertRelays') +
        wzTgl('Danfoss Valve', 'Enable for Danfoss hydraulic steering', 'autosteer.danfossEnabled') + '</div>';
    case 'roll':
      // Reuse the map's roll gauge: a green bar that rotates around (100,40) by the
      // live roll, with pink scale marks and the degree readout.
      return head + '<div class="wz-center"><svg width="260" height="104" viewBox="0 0 200 80">' +
        '<path fill="none" stroke="#E91E90" stroke-width="2.5" stroke-linecap="round" d="M 70,12 A 22,28 0 0 0 70,68 M 59,20 L 51,20 M 52,30 L 44,30 M 48,40 L 40,40 M 52,50 L 44,50 M 59,60 L 51,60"/>' +
        '<path fill="none" stroke="#E91E90" stroke-width="2.5" stroke-linecap="round" d="M 130,12 A 22,28 0 0 1 130,68 M 141,20 L 149,20 M 148,30 L 156,30 M 152,40 L 160,40 M 148,50 L 156,50 M 141,60 L 149,60"/>' +
        '<rect id="wz-roll-bar" x="50" y="38" width="100" height="4" rx="2" fill="#2ECC71"/>' +
        '<circle cx="100" cy="40" r="3" fill="#fff"/>' +
        '<text id="wz-roll-deg" x="100" y="32" text-anchor="middle" fill="#fff" font-size="24" font-weight="bold" font-family="system-ui,sans-serif">0.0</text>' +
        '</svg></div>' +
        '<div class="wz-rows">' + wzTgl('Invert Roll', null, 'roll.isRollInvert') +
        '<div class="wz-row"><div class="lbl">Zero Roll</div><button class="wz-testbtn" data-act="ZeroRoll">Zero Roll</button></div>' +
        '<div class="wz-row"><div class="lbl">Roll Zero Offset</div><div class="lbl"><span data-live="rollzero">—</span></div></div></div>';
    case 'was':
      return head + wzLive('Live Steer Angle', 'angle') +
        '<div class="wz-rows"><div class="wz-row"><div class="lbl">Zero WAS</div><button class="wz-testbtn" data-act="ZeroWas">Zero WAS</button></div>' +
        wzTgl('Invert WAS', 'Enable if steering reads backwards', 'autosteer.invertWas') +
        '<div class="wz-row"><div class="lbl">WAS Offset</div><div class="lbl"><span data-live="wasoffset">—</span> counts</div></div></div>';
    case 'motor':
      return head + wzLive('Live Steer Angle', 'angle') + '<div class="wz-center"><button class="wz-testbtn" data-act="StartTest">Start Motor Test</button>' +
        '<div class="wz-desc" data-live="phase"></div><div class="wz-desc" data-live="result"></div></div>';
    case 'maxangle':
      return head + wzLive('Live Steer Angle', 'angle') + '<div class="wz-center"><button class="wz-testbtn" data-act="StartTest">Start Max Angle Test</button>' +
        '<div class="wz-desc" data-live="phase"></div><div class="wz-desc" data-live="result"></div></div>';
    case 'cpd':
      return head + '<div class="wz-prereq"><div class="ttl">Prerequisites</div><div class="it">GPS: <b data-live="fix">—</b></div><div class="it">Speed: <b data-live="speed">—</b> (aim for ~5 km/h)</div></div>' +
        wzLive('Live Steer Angle', 'angle') + '<div class="wz-center"><button class="wz-testbtn" data-act="StartRecording" id="wz-recbtn">Record</button></div>' +
        '<div class="wz-rows">' + wzNum('Counts Per Degree', null, 'autosteer.countsPerDegree', '1', '') + '</div>';
    case 'ackermann':
      return head + '<div class="wz-prereq"><div class="ttl">Prerequisites</div><div class="it">GPS: <b data-live="fix">—</b></div><div class="it">Speed: <b data-live="speed">—</b></div></div>' +
        wzLive('Live Steer Angle', 'angle') + '<div class="wz-center"><button class="wz-testbtn" data-act="StartRecording" id="wz-recbtn">Record</button></div>' +
        '<div class="wz-rows">' + wzNum('Ackermann', '100 = neutral', 'autosteer.ackermann', '1', '') + '</div>';
    case 'gains':
      return head + '<div class="wz-rows">' + wzTgl('Guidance Algorithm (Stanley)', 'Pure Pursuit default; Stanley more responsive at low speed', 'autosteer.isStanleyMode') +
        wzNum('Proportional Gain (Kp)', 'Start at 10, increase for faster correction', 'autosteer.proportionalGain', '1', '') +
        wzNum('Integral Gain (Ki)', 'Start at 0, only increase for drift', 'autosteer.integralGain', '0.01', '') +
        wzNum('Steer Response Hold', 'Look-ahead (higher = smoother)', 'autosteer.steerResponseHold', '0.1', '') +
        wzNum('Side Hill Compensation', 'Degrees per degree of roll (0-1.0)', 'autosteer.sideHillCompensation', '0.01', '') + '</div>';
    case 'speed':
      return head + '<div class="wz-rows">' +
        wzNum('Min Steer Speed', 'Engage above this speed', 'autosteer.minSteerSpeed', '0.1', 'km/h') +
        wzNum('Max Steer Speed', 'Safety cutoff speed', 'autosteer.maxSteerSpeed', '0.1', 'km/h') +
        wzTgl('Turn Sensor', 'Steering wheel encoder', 'autosteer.turnSensorEnabled') +
        wzTgl('Pressure Sensor', 'Hydraulic stall detection', 'autosteer.pressureSensorEnabled') +
        wzTgl('Current Sensor', 'Motor current obstruction detection', 'autosteer.currentSensorEnabled') +
        wzTgl('Steer In Reverse', 'Allow steering while reversing', 'autosteer.steerInReverse') +
        wzNum('Deadzone Heading', 'Heading error tolerance', 'autosteer.deadzoneHeading', '0.01', 'deg') + '</div>';
    case 'finish':
      return head + '<div class="wz-center"><div class="wz-okcard"><div class="ok">OK</div><div class="t">Configuration saved!</div><div class="s">Click Finish to close the wizard</div></div></div>';
    default: return head;
  }
}
function renderWizard() {
  const w = wizard;
  if (!w) return;
  // Chrome (every frame).
  asSetText('wz-step', 'Step ' + (w.stepIndex + 1) + ' of ' + w.totalSteps);
  document.getElementById('wz-progressbar').style.width = (w.totalSteps ? (w.stepIndex + 1) / w.totalSteps * 100 : 0) + '%';
  asSetText('wz-was', (w.statusWas || 0).toFixed(1) + '°'); asSetText('wz-roll', (w.statusRoll || 0).toFixed(1) + '°');
  asSetText('wz-gps', w.statusGps || '—'); asSetText('wz-speed', (w.statusSpeed || 0).toFixed(1) + ' km/h'); asSetText('wz-pwm', w.statusPwm | 0);
  const next = document.getElementById('wz-next');
  next.textContent = w.isLast ? 'Finish' : 'Next';
  next.classList.toggle('disabled', !(w.isLast || w.canNext));
  document.getElementById('wz-back').classList.toggle('disabled', !w.canBack);
  document.getElementById('wz-skip').style.display = w.canSkip ? '' : 'none';
  // Rebuild content only on step change.
  const key = w.stepKind + '|' + w.stepIndex;
  if (key !== _wzKey) { _wzKey = key; wzContent.innerHTML = buildWizardContent(w); }
  // Active states (selection cards / segments / toggles) + live values, every frame.
  for (const el of wzContent.querySelectorAll('[data-activekey]'))
    el.classList.toggle('active', String(cfgGet(el.dataset.activekey)) === el.dataset.activeval);
  for (const el of wzContent.querySelectorAll('[data-activehw]'))
    el.classList.toggle('active', w.hardwareLevel === +el.dataset.activehw);
  for (const el of wzContent.querySelectorAll('[data-tglkey]')) {
    const on = !!cfgGet(el.dataset.tglkey); el.classList.toggle('on', on); el.textContent = on ? 'On' : 'Off';
  }
  for (const el of wzContent.querySelectorAll('[data-act]')) el.classList.toggle('disabled', !iHoldControl);
  const live = (k, v) => { for (const el of wzContent.querySelectorAll('[data-live="' + k + '"]')) el.textContent = v; };
  live('angle', (w.liveAngle || 0).toFixed(1) + '°'); live('roll', (w.liveRoll || 0).toFixed(2) + '°');
  live('error', (w.liveError || 0).toFixed(1)); live('phase', w.testPhase || ''); live('result', w.testResult || '');
  live('fix', w.fixLabel || (w.statusGps || '—')); live('speed', (w.statusSpeed || 0).toFixed(1) + ' km/h');
  live('rollzero', wzVal('roll.rollZero').toFixed(2)); live('wasoffset', wzVal('autosteer.wasOffset') | 0);
  // Roll gauge (roll-calibration step): rotate the bar by the live roll, like the map.
  const rb = document.getElementById('wz-roll-bar');
  if (rb) { const rv = w.liveRoll || w.statusRoll || 0; rb.setAttribute('transform', 'rotate(' + rv.toFixed(2) + ' 100 40)'); const rd = document.getElementById('wz-roll-deg'); if (rd) rd.textContent = rv.toFixed(1); }
  const rec = document.getElementById('wz-recbtn');
  if (rec) { rec.textContent = w.testActive ? 'Stop' : 'Record'; rec.dataset.act = w.testActive ? 'StopRecording' : 'StartRecording'; }
}

function renderSettings() {
  // Re-read the open config panel(s) when a fresh config frame arrives.
  if (configDirty) {
    configDirty = false;
    if (vcPanel.classList.contains('open')) populateVehicleCfg(false);
    if (tcPanel.classList.contains('open')) populateToolCfg(false);
    if (saPanel.classList.contains('open')) populateScreenAlerts();
    if (asPanel.classList.contains('open')) populateAutoSteer(false);
    if (document.getElementById('viewsettings').classList.contains('open')) renderViewSettings();
  }
  // AutoSteer live telemetry rides every status frame (not just config changes).
  if (asPanel.classList.contains('open')) renderAutoSteerLive();
  // Smart-WAS stats refresh while its modal is open.
  if (swPanel.classList.contains('open')) populateSmartWas();
  // Steer Wizard: re-render while open (host-driven; live every frame).
  wizardDirty = false;
  if (wzOverlay.classList.contains('open')) renderWizard();
  // Re-read the hub when a fresh profiles frame arrives.
  if (profilesDirty) { profilesDirty = false; if (document.getElementById('vehtoolhub').classList.contains('open')) refreshHub(); }
  // Network IO panel: module/NTRIP readouts ride the Status frame → refresh each frame.
  if (nioPanel.classList.contains('open')) renderNetworkIo();
  // NTRIP test result rides the Status frame while a test is in flight.
  if (_nteTestActive && document.getElementById('ntripeditor').classList.contains('open') && statusBar)
    document.getElementById('nte-teststatus').textContent = statusBar.ntripTestStatus || '';
  // Re-read the NTRIP list/editor fields when a fresh profiles frame arrives.
  if (ntripDirty) {
    ntripDirty = false;
    if (document.getElementById('ntripprofiles').classList.contains('open')) renderNtripList();
    if (document.getElementById('ntripeditor').classList.contains('open')) renderNteFields();
  }
  // Field Operations: status pill rides scene/status; Fields-and-Jobs lists re-read on a
  // fresh FieldOps frame.
  if (document.getElementById('fieldops').classList.contains('open')) renderFieldOps();
  if (document.getElementById('newfield').classList.contains('open')) renderNewField();
  if (fieldOpsDirty) {
    fieldOpsDirty = false;
    if (document.getElementById('fieldsandjobs').classList.contains('open')) renderFieldsAndJobs();
    if (document.getElementById('resumejob').classList.contains('open')) renderResumeJob();
  }
  if (agShareDirty) {
    agShareDirty = false;
    if (document.getElementById('agsettings').classList.contains('open')) renderAgSettings();
    if (document.getElementById('agupload').classList.contains('open')) renderAgUpload();
    if (document.getElementById('agdownload').classList.contains('open')) renderAgDownload();
  }
  // App Settings units/toggles ride config+status (every frame while open).
  if (document.getElementById('appsettings').classList.contains('open')) renderAppSettings();
  if (appInfoDirty) {
    appInfoDirty = false;
    if (document.getElementById('language').classList.contains('open')) renderLanguage();
    if (document.getElementById('logviewer').classList.contains('open')) renderLogViewer();
    if (document.getElementById('hotkeys').classList.contains('open')) renderHotkeys();
    if (document.getElementById('about').classList.contains('open')) renderAbout();
    if (document.getElementById('bugreport').classList.contains('open')) renderBugReport();
  }
}

// ---- Vehicle & Tool picker hub (Phase 9) — mirrors LoadVehicleToolDialogViewModel ----
const HUB = {
  summary: document.getElementById('vth-summary'),
  curVeh: document.getElementById('vth-curveh'), curTool: document.getElementById('vth-curtool'),
  vehList: document.getElementById('vth-vehlist'), toolList: document.getElementById('vth-toollist'),
  vehPrev: document.getElementById('vth-vehprev'), toolPrev: document.getElementById('vth-toolprev'),
  vehName: document.getElementById('vth-vehname'), toolName: document.getElementById('vth-toolname'),
  status: document.getElementById('vth-status'), load: document.getElementById('vth-load'),
};
function hubSend(cmd, arg) { transport.send(arg == null ? cmd : cmd + '|' + arg); }
function fillHubList(sel, entries, activeName) {
  const prev = sel.value;
  sel.innerHTML = '';
  for (const e of entries) {
    const o = document.createElement('option');
    o.value = e.name; o.textContent = e.name + (e.name === activeName ? '  ●' : '');
    sel.appendChild(o);
  }
  sel.value = entries.some(e => e.name === prev) ? prev : activeName;
}
function hubPreview(entries, name) { const e = entries.find(x => x.name === name); return e ? e.preview : ''; }
function hubUpdatePreviews() {
  if (!profiles) return;
  HUB.vehPrev.textContent = hubPreview(profiles.vehicles, HUB.vehList.value);
  HUB.toolPrev.textContent = hubPreview(profiles.tools, HUB.toolList.value);
  HUB.summary.textContent = 'Selected: ' + (HUB.vehList.value || profiles.activeVehicle) +
    ' / ' + (HUB.toolList.value || profiles.activeTool);
  HUB.load.disabled = (HUB.vehList.value === profiles.activeVehicle && HUB.toolList.value === profiles.activeTool);
}
function refreshHub() {
  if (!profiles) return;
  HUB.curVeh.textContent = 'Current: ' + profiles.activeVehicle;
  HUB.curTool.textContent = 'Current: ' + profiles.activeTool;
  fillHubList(HUB.vehList, profiles.vehicles, profiles.activeVehicle);
  fillHubList(HUB.toolList, profiles.tools, profiles.activeTool);
  hubUpdatePreviews();
}
HUB.vehList.addEventListener('change', () => { HUB.vehName.value = HUB.vehList.value; hubUpdatePreviews(); });
HUB.toolList.addEventListener('change', () => { HUB.toolName.value = HUB.toolList.value; hubUpdatePreviews(); });
for (const b of document.querySelectorAll('#vehtoolhub .hub-cbtn'))
  b.addEventListener('pointerdown', e => {
    e.stopPropagation();
    const kind = b.dataset.kind, act = b.dataset.act;
    const list = kind === 'vehicle' ? HUB.vehList : HUB.toolList;
    const nameInput = kind === 'vehicle' ? HUB.vehName : HUB.toolName;
    if (act === 'new') {
      const nm = (nameInput.value || '').trim();
      if (!nm) { HUB.status.textContent = 'Type a name, then New'; return; }
      hubSend('profile.new', kind + '\t' + nm); HUB.status.textContent = 'Creating ' + nm + '…';
    } else if (act === 'delete') {
      const nm = list.value;
      if (nm) showConfirm('Delete Profile', 'Delete ' + kind + " profile '" + nm + "'?",
        () => hubSend('profile.delete', kind + '\t' + nm));
    } else if (act === 'rename') {
      const old = list.value, nw = (nameInput.value || '').trim();
      if (!old || !nw) { HUB.status.textContent = 'Select a profile, type a new name, then Rename'; return; }
      hubSend('profile.rename', kind + '\t' + old + '\t' + nw);
    } else if (act === 'reset') {
      showConfirm('Reset Profile', 'Reset Default ' + kind + ' profile?', () => hubSend('profile.reset', kind));
    }
  });
HUB.load.addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (HUB.load.disabled || !profiles) return;
  hubSend('profile.load', (HUB.vehList.value || profiles.activeVehicle) + '\t' + (HUB.toolList.value || profiles.activeTool));
  lnCloseAll(); // native closes the picker on load
});
document.getElementById('vth-vehconfig').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (HUB.vehList.value) hubSend('profile.configureVehicle', HUB.vehList.value);
  lnOpen('vehiclecfg', 'ln-vehicle', () => populateVehicleCfg(true));
});
document.getElementById('vth-toolconfig').addEventListener('pointerdown', e => {
  e.stopPropagation();
  if (HUB.toolList.value) hubSend('profile.configureTool', HUB.toolList.value);
  lnOpen('toolcfg', 'ln-vehicle', () => populateToolCfg(true));
});

// ---- remote actuation control (Phase 2 safety layer) ----
// Take/Release single-holder control + a Tier-2 stub. Only the holder may
// actuate; the holder must heartbeat (presence) or the host revokes it (deadman).
// Control is implicit and by connection order: the first browser to connect is the
// controller (server-assigned); others observe. No take/release UI. A status line
// shows which role this browser has.
// Status-bar role indicator (left of Modules). Reflects both connection state
// (Disconnected when the socket is down) and the implicit control role.
function renderRole() {
  const t = document.getElementById('sb-roletext'), d = document.getElementById('sb-roledot');
  if (!t) return;
  let label, color;
  if (connState !== 'connected') { label = 'Disconnected'; color = '#ff5a5a'; }
  else if (iHoldControl)         { label = 'Operator';     color = '#39FF6A'; }
  else if (lastControl.held)     { label = 'Observer';     color = '#ff7a3d'; }
  else                           { label = 'No operator';  color = '#9fb3cc'; }
  t.textContent = label; t.style.color = color; d.style.background = color;
  // Observer = another client (usually the launcher's own WebView) holds the seat.
  // The badge doubles as the takeover affordance.
  const canTake = connState === 'connected' && !iHoldControl;
  t.style.cursor = canTake ? 'pointer' : 'default';
  t.title = canTake ? 'Tap to take operator control' : '';
}
for (const id of ['sb-roletext', 'sb-roledot']) {
  const el = document.getElementById(id);
  if (el) el.addEventListener('pointerdown', e => {
    e.stopPropagation();
    if (connState !== 'connected' || iHoldControl) return;
    showConfirm('Take control', 'Take operator control on this device? The current operator drops to Observer.',
      () => transport.send('control.takeover|Browser'));
  });
}
function updateControlUi() {
  iHoldControl = lastControl.held && lastControl.holderId === myClientId;
  if (typeof updateAsGated === 'function') updateAsGated(); // re-gate AutoSteer actions
  if (document.getElementById('recpath').classList.contains('open')) renderRecPath(); // re-gate Play
  renderRole();
}
// Reclaim the operator seat when it is free. Control is keyed to the WebSocket
// connection server-side and acquired once, implicitly, at connect. A browser
// REFRESH opens a new connection while the old one is still registered as holder,
// so the new connection's implicit acquire is denied; once the stale holder is
// dropped/deadman-swept the seat goes empty but the new connection never retried —
// leaving the Tier-2 right-nav menu permanently inop until a server restart. Claiming
// the empty seat here (driven by the control-state frame the drop/sweep emits, plus
// connect/hello) makes the operator role survive a refresh. Guarded on !held so it
// never takes the seat from a live operator — only fills a vacant one (deadman model).
function claimSeatIfFree() {
  if (connState === 'connected' && myClientId && !iHoldControl && !lastControl.held)
    transport.send('control.acquire|Browser');
}
// Heartbeat keeps our hold alive while we control; a lapse trips the host deadman
// (disengage autosteer + sections).
setInterval(() => { if (iHoldControl) transport.send('control.presence'); }, 500);
updateControlUi();

// Right-nav toolbar actuation (Phase 3b). Each button sends its Tier-2 command,
// but only while we hold control — the host hub enforces the same gate. No
// per-action confirm: holding control IS the deliberate gate (matches the native
// single-tap), and the deadman + failsafe cover a lost controller.
// A silently-dropped press on a live control reads as "the button is broken" —
// if this client is an Observer, say so instead of doing nothing.
function rnSend(cmd) {
  if (iHoldControl) { transport.send(cmd); return; }
  flashHint('Observer — tap the role badge (top) to take control');
}
// NB: use a direct lookup here, not the RN object — RN is a `const` defined later
// in the render section, so referencing it here would hit the temporal dead zone
// and throw at load (stuck "connecting…").
const rnRoot = document.getElementById('rightnav');
if (rnRoot) {
  rnRoot.addEventListener('pointerdown', e => e.stopPropagation()); // don't pan the map
  const wireRn = (id, cmd) => { const el = document.getElementById(id); if (el) el.addEventListener('click', () => rnSend(cmd)); };
  wireRn('rn-contour', 'contour.toggle');
  wireRn('rn-manual', 'section.manual');
  wireRn('rn-auto', 'section.master');
  wireRn('rn-youturn', 'youturn.toggle');
  wireRn('rn-steer', 'autosteer.toggle');
}

// ---- render ----
// ONE projection: the CanvasKit M44 perspective matrix, always — top-down is just
// pitch 0 on the same matrix (distance is chosen so pitch 0 matches the ortho scale
// exactly, see buildScreenMatrix). There is no separate 2D renderer; perspM is non-null
// for the entire Skia render path (it's only null before CanvasKit finishes loading, and
// nothing is drawn then). The render helpers (w2s, drawGridSk, drawImagerySk,
// drawCoverageSk, …) assume perspM and no longer branch on it — that old per-pitch
// branch dropped top-down into an axis-aligned raster path that couldn't rotate, so
// imagery/coverage skewed away under HeadingUp/Map as heading changed.
function active3D() { return !!CK; }
// Build the world→CSS-px screen matrix, mirroring the native camera model
// (SkiaMapControl.BuildPerspectiveScreenMatrix): view = T(0,0,-distance)·Rx(-pitch)
// ·T(-cam), a FOV-PERSP_FOV perspective, then an NDC(-1..1, y-up)→pixel(y-down)
// viewport. distance is derived from pxPerM so pitch=0 matches the ortho scale
// exactly (no jump when tilting in). CanvasKit M44 is column-vector / row-major /
// radians, so multiply right-to-left: T(-cam) acts first.
function buildScreenMatrix() {
  if (!CK) return null;
  const halfW = vw / 2, halfH = vh / 2;
  const tanHalf = Math.tan(PERSP_FOV / 2);
  const distance = vh / (2 * tanHalf * pxPerM); // scale-continuity with ortho at pitch 0
  const near = 1, far = Math.max(5000, distance * 4);
  // Camera-relative: the camera translation is deliberately NOT baked into the matrix.
  // Baking absolute -camE,-camN into the f32 M44 forces it to subtract two field-scale
  // numbers each frame → the result steps in f32 ULPs as the camera moves → the whole
  // world jitters a sub-mm fraction ("floating-origin" jitter, visible on high-frequency
  // imagery). Instead w2s / pw / the raster sites subtract camE,camN in f64 (JS) and feed
  // only small relative coords here. Mirrors native (SkiaMapControl: double relX =
  // _vehicleX - _cameraX before its f32 SKMatrix44).
  const view = CK.M44.multiply(
    CK.M44.translated([0, 0, -distance]),
    CK.M44.rotated([1, 0, 0], -pitch),
    CK.M44.rotated([0, 0, 1], -mapRotation)); // map rotation (HeadingUp etc.)
  // Perspective (column-vector, row-major) — transpose of System.Numerics
  // CreatePerspectiveFieldOfView, so w' = -z.
  const yS = 1 / tanHalf, xS = yS / (vw / vh), nf = 1 / (near - far);
  const proj = [
    xS, 0, 0, 0,
    0, yS, 0, 0,
    0, 0, far * nf, near * far * nf,
    0, 0, -1, 0,
  ];
  const viewport = [
    halfW, 0, 0, halfW,
    0, -halfH, 0, halfH,
    0, 0, 1, 0,
    0, 0, 0, 1,
  ];
  return CK.M44.multiply(viewport, proj, view);
}
function updatePerspective() {
  perspM = active3D() ? buildScreenMatrix() : null;
  // Cache the inverse once per frame (Phase MT s2w foundation). perspM is row-major
  // (CanvasKit M44); the inverse is the screen-px→world-ray map used by s2w.
  perspMInv = perspM ? invertM44(perspM) : null;
}
// Near-plane clip a ground segment for the perspective path. perspM's w (bottom row,
// z=0) is positive in front of the camera; CanvasKit's drawLine doesn't clip the
// behind part (it perspective-divides by a negative w → a mirrored ghost line), so
// we clip in world space first. Returns [e1,n1,e2,n2] fully in front, or null.
function clipNear(e1, n1, e2, n2) {
  const EPS = 1.0; // matches the projection near plane
  const w1 = pw(e1, n1);
  const w2 = pw(e2, n2);
  if (w1 >= EPS && w2 >= EPS) return [e1, n1, e2, n2];
  if (w1 < EPS && w2 < EPS) return null;
  const t = (EPS - w1) / (w2 - w1);
  const ce = e1 + (e2 - e1) * t, cn = n1 + (n2 - n1) * t;
  return w1 < EPS ? [ce, cn, e2, n2] : [e1, n1, ce, cn];
}
// Map-mode auto-pan: keep the vehicle inside a centred safe zone, smoothing the
// camera toward it (snap if it leaves the viewport). Mirrors native ApplyAutoPan
// (rotation 0 in Map mode). World units; rotation 0.
function applyAutoPan(rp) {
  const viewHalfW = (vw / 2) / pxPerM, viewHalfH = (vh / 2) / pxPerM;
  const relE = rp.e - camE, relN = rp.n - camN;
  if (Math.abs(relE) > viewHalfW || Math.abs(relN) > viewHalfH) { camE = rp.e; camN = rp.n; return; }
  const safeW = viewHalfW * AUTO_PAN_SAFE, safeH = viewHalfH * AUTO_PAN_SAFE;
  let panE = 0, panN = 0;
  if (relE > safeW) panE = relE - safeW; else if (relE < -safeW) panE = relE + safeW;
  if (relN > safeH) panN = relN - safeH; else if (relN < -safeH) panN = relN + safeH;
  camE += panE * AUTO_PAN_SMOOTH; camN += panN * AUTO_PAN_SMOOTH;
}
// Per-frame camera update (run once, from skFrame()): apply the follow mode (camera
// position + map rotation), precompute the screen rotation, rebuild perspM.
function updateCamera() {
  const rp = renderPose(); // interpolated pose (position + heading both smooth now)
  let target = mapRotation; // Free (mode 2): hold rotation
  if (rp) {
    if (cameraMode === 0) { camE = rp.e; camN = rp.n; target = 0; }                // NorthUp
    else if (cameraMode === 1) { camE = rp.e; camN = rp.n; target = -rp.heading; } // HeadingUp
    else if (cameraMode === 3) { target = 0; applyAutoPan(rp); }                   // Map
  } else if (cameraMode !== 2) target = 0;
  // Ease toward the target along the shortest angular path — turns the 10 Hz heading
  // steps (HeadingUp) into continuous rotation. Adaptive alpha: heavy damping for small
  // errors (line-holding dither) → no map wobble; responsive for large errors → crisp turns.
  let d = target - mapRotation;
  d -= 2 * Math.PI * Math.round(d / (2 * Math.PI));
  // QUADRATIC ramp: stays near MIN for tiny errors (line-holding dither ≈0.005 rad → the
  // map barely follows it) but rises sharply toward MAX as the error approaches a real turn.
  // A linear ramp lifted alpha too much at the dither scale, leaving residual wobble.
  const rotFrac = Math.min(1, Math.abs(d) / ROT_SMOOTH_FULL);
  const rotAlpha = ROT_SMOOTH_MIN + (ROT_SMOOTH_MAX - ROT_SMOOTH_MIN) * rotFrac * rotFrac;
  mapRotation += d * rotAlpha;
  const rR = -mapRotation;
  _cosRR = Math.cos(rR); _sinRR = Math.sin(rR);
  updatePerspective();
  return rp;
}
// Apply perspM (row-major, M·v) to a ground point (e,n,0,1); perspective divide.
function applyM(M, e, n) {
  const x = M[0] * e + M[1] * n + M[3];
  const y = M[4] * e + M[5] * n + M[7];
  const w = M[12] * e + M[13] * n + M[15];
  return [x / w, y / w];
}
// World (e,n) → CSS px through the one perspective matrix (top-down is pitch 0). Only
// ever called from the Skia render path, where perspM is guaranteed set (CanvasKit up).
function w2s(e, n) {
  return applyM(perspM, e - camE, n - camN);
}
// Homogeneous w of a WORLD point through the camera-relative perspM (w < 1 ⇒ behind the
// near plane; used for cull/clip). The camE,camN subtraction happens here in f64 so the
// f32 matrix only ever sees small relative coords (see buildScreenMatrix's note).
function pw(e, n) {
  return perspM[12] * (e - camE) + perspM[13] * (n - camN) + perspM[15];
}
// ---- screen→world unprojection (Phase MT foundation) ----
// w2s only ever does world→screen; map-tap features need the inverse: turn a CSS-px tap
// into a field coordinate (E,N) on the ground plane z=0. perspM bakes the viewport in, so
// a tap (px,py) is already in perspM's output space — no NDC pre-step. We unproject the
// tap at two clip depths (the near/far representatives where the homogeneous w=1), giving
// two world points on the viewing ray, then intersect that ray with z=0.
//
// Full 4×4 row-major Gauss-Jordan inverse (perspM is row-major: a[r][c]=m[r*4+c]).
// Returns null if singular. Robust to any pitch — no special-casing top-down.
function invertM44(m) {
  const a = [];
  for (let r = 0; r < 4; r++) {
    a.push([m[r*4], m[r*4+1], m[r*4+2], m[r*4+3],
            r===0?1:0, r===1?1:0, r===2?1:0, r===3?1:0]);
  }
  for (let col = 0; col < 4; col++) {
    let piv = col, best = Math.abs(a[col][col]);
    for (let r = col + 1; r < 4; r++) {
      const v = Math.abs(a[r][col]); if (v > best) { best = v; piv = r; }
    }
    if (best < 1e-12) return null;
    if (piv !== col) { const t = a[piv]; a[piv] = a[col]; a[col] = t; }
    const pv = a[col][col];
    for (let j = 0; j < 8; j++) a[col][j] /= pv;
    for (let r = 0; r < 4; r++) {
      if (r === col) continue;
      const f = a[r][col];
      if (f !== 0) for (let j = 0; j < 8; j++) a[r][j] -= f * a[col][j];
    }
  }
  const out = new Array(16);
  for (let r = 0; r < 4; r++) for (let c = 0; c < 4; c++) out[r*4+c] = a[r][c+4];
  return out;
}
// Row-major 4×4 · column vector (x,y,z,w) → [4].
function m44vec(M, x, y, z, w) {
  return [
    M[0]*x + M[1]*y + M[2]*z + M[3]*w,
    M[4]*x + M[5]*y + M[6]*z + M[7]*w,
    M[8]*x + M[9]*y + M[10]*z + M[11]*w,
    M[12]*x + M[13]*y + M[14]*z + M[15]*w,
  ];
}
// Screen (CSS px) → world (E,N) on the ground plane z=0. Returns {e,n} or null (no matrix
// yet, or the viewing ray is parallel to the ground — e.g. a tap on/above the horizon under
// extreme tilt). Reuse everywhere a tap must become a field coordinate.
function s2w(px, py) {
  if (!perspMInv) return null;
  const A = m44vec(perspMInv, px, py, 0, 1); // near-plane representative
  const B = m44vec(perspMInv, px, py, 1, 1); // far-plane representative
  if (A[3] === 0 || B[3] === 0) return null;
  const ax = A[0]/A[3], ay = A[1]/A[3], az = A[2]/A[3];
  const bx = B[0]/B[3], by = B[1]/B[3], bz = B[2]/B[3];
  const dz = bz - az;
  if (Math.abs(dz) < 1e-9) return null;       // ray parallel to ground (horizon)
  const t = -az / dz;
  // perspMInv maps screen → camera-relative world; add the camera origin back for world E/N.
  return { e: ax + (bx - ax) * t + camE, n: ay + (by - ay) * t + camN };
}
// Dev round-trip check: project a grid of world points with w2s, unproject with s2w,
// report max error. Call from the console at pitch 0 AND tilted (press 3) — expect
// sub-millimetre error. Skips points behind the camera (w<1), which w2s can't show anyway.
window._s2wTest = function () {
  if (!perspM) { console.log('s2w test: no perspM (CanvasKit not ready)'); return; }
  let maxErr = 0, n = 0;
  for (let de = -200; de <= 200; de += 50) for (let dn = -200; dn <= 200; dn += 50) {
    const e = camE + de, nn = camN + dn;
    if (pw(e, nn) < 1.0) continue; // behind camera
    const s = w2s(e, nn);
    const b = s2w(s[0], s[1]);
    if (!b) continue;
    maxErr = Math.max(maxErr, Math.hypot(b.e - e, b.n - nn));
    n++;
  }
  console.log(`s2w round-trip: pitch=${(pitch*180/Math.PI).toFixed(0)}° samples=${n} maxErr=${maxErr.toExponential(3)} m`);
  return maxErr;
};
// On-screen heading at a ground point: the heading direction projected through the
// current matrix (so the vehicle marker aligns with the tilted ground, not screen
// up). Returns the world heading unchanged when top-down.
function screenHeading(e, n, h) {
  // Project a short step along the heading and read the on-screen angle. Works for
  // 2D (now rotation-aware via w2s) and 3D; returns h when north-up + top-down.
  const a = w2s(e, n), b = w2s(e + Math.sin(h) * 3, n + Math.cos(h) * 3);
  return Math.atan2(b[0] - a[0], -(b[1] - a[1])); // 0 = up, clockwise
}
// Section display palette by ColorCode — matches the native
// SectionColorCodeToBackgroundConverter exactly.
const SECTION_COLORS = [
  'rgba(242,51,51,0.85)',  // 0 off          (red)
  'rgba(247,247,0,0.9)',   // 1 manual on    (yellow)
  'rgba(0,242,0,0.85)',    // 2 auto on      (green)
  'rgba(0,222,222,0.85)',  // 3 turning off  (cyan)
  'rgba(255,165,0,0.9)',   // 4 turning on   (orange)
  'rgba(150,150,150,0.5)', // 5 auto off     (gray)
];
// Tool/section footprint: each section is a bar from its left to right edge,
// perpendicular to the tool heading, green when on / grey when off. Section
// spans come from the Scene (static layout); the tool pose comes from the Tick.
// Transform matches SectionControlService.GetSectionWorldPosition:
//   edge = (toolE,toolN) + (sin,cos)(toolHeading + π/2) * span.
// Dead-reckon the tool pose between ticks (same scheme as renderPose for the
// vehicle), so the footprint glides with the tractor instead of snapping to each
// 10 Hz tick. Extrapolate along the tool heading at the reported speed.
function renderTool() {
  const s = sample();
  if (!s || !s.b.tool) return null;
  const pt = s.a.tool || s.b.tool, qt = s.b.tool, f = s.f;
  return {
    e: pt.e + (qt.e - pt.e) * f,
    n: pt.n + (qt.n - pt.n) * f,
    heading: lerpAngle(pt.heading, qt.heading, f),
  };
}
// Shortest-path angular lerp (radians).
function lerpAngle(a, b, f) {
  let d = b - a; d -= 2 * Math.PI * Math.round(d / (2 * Math.PI));
  return a + d * f;
}
// Pick the two buffered poses bracketing the playback head (RENDER_DELAY in the PAST)
// and the fraction between them. The playhead runs on the HOST timeline (client clock
// − clockOffset), so its position relative to the poses' host build-times is jitter-free —
// WiFi arrival jitter only changes WHICH poses are buffered, never the interp rate (that
// rate-warp was the "wiggle"). A 2-pose buffer judders once RENDER_DELAY > the pose
// interval (playhead falls off the old end → clamps); the multi-pose buffer always has a
// bracketing pair. Returns { a, b, f } or null; holds oldest/newest outside the buffer.
function sample() {
  const m = poseBuf.length;
  if (m === 0) return null;
  if (m === 1) return { a: poseBuf[0], b: poseBuf[0], f: 1 };
  const useHost = clockOffset !== null && typeof poseBuf[m - 1].hostT === 'number';
  const key = useHost ? 'hostT' : 't';
  const playhead = useHost
    ? performance.now() - clockOffset - RENDER_DELAY
    : performance.now() - RENDER_DELAY; // legacy receipt timeline (host sent no timestamp)
  if (playhead <= poseBuf[0][key]) return { a: poseBuf[0], b: poseBuf[0], f: 0 };
  const nw = poseBuf[m - 1];
  if (playhead >= nw[key]) {
    // Buffer underrun (WiFi spike / late pose): EXTRAPOLATE the last segment's velocity
    // forward (f>1 → renderPose/renderTool extend prev→nw linearly) instead of HOLDING,
    // which would freeze a frame → stutter. Capped at EXTRAP_CAP_MS so a real dropout
    // settles to a hold rather than flying off.
    const prev = poseBuf[m - 2];
    const span = nw[key] - prev[key];
    if (span <= 0) return { a: nw, b: nw, f: 1 };
    const over = Math.min(playhead - nw[key], EXTRAP_CAP_MS);
    return { a: prev, b: nw, f: 1 + over / span };
  }
  for (let i = 0; i < m - 1; i++) {
    const a = poseBuf[i], b = poseBuf[i + 1];
    if (playhead >= a[key] && playhead < b[key]) {
      const span = b[key] - a[key];
      return { a, b, f: span > 0 ? (playhead - a[key]) / span : 1 };
    }
  }
  return { a: nw, b: nw, f: 1 };
}
function renderPose() {
  const s = sample();
  if (!s) return null;
  return {
    e: s.a.e + (s.b.e - s.a.e) * s.f,
    n: s.a.n + (s.b.n - s.a.n) * s.f,
    heading: lerpAngle(s.a.heading, s.b.heading, s.f),
    speed: s.b.speed,
  };
}
// Cross-track error as "12 cm R" (R = right of line, +xte). Centimetres so the
// magnitude reads at a glance; the lightbar carries the live feel.
function xteText(xte) {
  if (xte == null) return '—';
  const cm = Math.abs(xte) * 100;
  const side = xte > 0.005 ? 'R' : xte < -0.005 ? 'L' : '·';
  return `${cm.toFixed(0)} cm ${side}`;
}

// Lightbar readout text → DOM overlay (the LED strip itself is drawn by lightbarSk).
// Updated every frame from the latest tick; hidden when guidance is off.
const lbEl = document.getElementById('lb');
function updateLightbarText() {
  const cfg = config && config.autosteer;
  if (!tick || !tick.guidanceActive || !cfg || !cfg.guidanceBarOn) { lbEl.style.display = 'none'; return; }
  if (cfg.steerBarEnabled) {
    // Steer bar: steer-angle error (deg) with dead-zone.
    let err = tick.steerAngleError || 0;
    const dz = (tick.op && tick.op.autoSteer) ? 0.5 : 0.2;
    if (Math.abs(err) < dz) err = 0;
    const arrow = err === 0 ? '> 0 <' : err > 0 ? '◀' : '▶';
    lbEl.textContent = err === 0 ? '> 0 <' : `${arrow} ${Math.abs(err).toFixed(1)}°`;
  } else {
    // Light bar: cross-track distance.
    const xte = tick.crossTrackError || 0;
    const onLine = Math.abs(xte) < 0.05;
    const arrow = onLine ? '●' : xte > 0 ? '◀' : '▶'; // arrow = steer direction
    // 1-based pass label (human counting): the reference AB line is "Pass 1", one over is
    // "Pass 2", etc. — magnitude only (the arrow already shows which way to steer).
    const pass = (tick.op ? tick.op.passNumber : 0) | 0;
    lbEl.textContent = `${arrow} ${(Math.abs(xte) * 100).toFixed(0)} cm   Pass ${Math.abs(pass) + 1}`;
  }
  lbEl.style.display = 'block';
}

// Headland-distance HUD → DOM overlay (mirrors the native SkiaMapControl HUD).
// Shown only when Display.HeadlandDistanceVisible is on and a live distance ≥ 0
// (−1 = no headland / not driving). Metric/imperial follows the status bar.
const hlHud = document.getElementById('headland-hud');
function updateHeadlandHud() {
  const on = config && config.display && config.display.headlandDistanceVisible;
  const d = tick ? tick.headlandDist : -1;
  // Hide the HUD while a map-tap creation flow is active (Quick AB / boundary curve / draw / …):
  // the boundary distance isn't needed while placing points, and it would clutter the top-centre
  // instruction pill + toolbar. body.maptap is set by startMapTap for the duration of the flow.
  const creating = document.body.classList.contains('maptap');
  if (!on || creating || d == null || d < 0) { hlHud.style.display = 'none'; return; }
  const metric = !statusBar || statusBar.isMetric;
  hlHud.textContent = metric ? d.toFixed(1) + ' m' : (d * 3.28084).toFixed(0) + ' ft';
  hlHud.classList.toggle('warn', !!(tick && tick.headlandWarn));
  hlHud.style.display = 'block';
}

// Top status bar — DOM overlay mirroring the native StatusBarPanel: a two-line
// left stack (Fix dot + text + Age on top, a rotating Field/Stats/AB-line below
// with a pause toggle), and a right cluster (aggregate Modules dot + per-module
// popup, then big Speed + unit). Updated each frame from the latest StatusDto plus
// the live tick. Element refs cached once.
const SB = {
  bar: document.getElementById('statusbar'),
  pause: document.getElementById('sb-pause'),
  fixDot: document.getElementById('sb-fixdot'), fix: document.getElementById('sb-fix'),
  age: document.getElementById('sb-age'), rot: document.getElementById('sb-rot'),
  speed: document.getElementById('sb-speed'), unit: document.getElementById('sb-unit'),
  hdg: document.getElementById('sb-hdg'),
  modAgg: document.getElementById('sb-modagg'), modBtn: document.getElementById('sb-modbtn'),
  modPop: document.getElementById('sb-modpop'),
  mGps: document.getElementById('sb-gps'), mImu: document.getElementById('sb-imu'),
  mAs: document.getElementById('sb-as'), mMa: document.getElementById('sb-ma'),
  dGps: document.getElementById('sb-gps-d'), dImu: document.getElementById('sb-imu-d'),
  dAs: document.getElementById('sb-as-d'), dMa: document.getElementById('sb-ma-d'),
  // GPS-detail card (Phase 5).
  fixBtn: document.getElementById('sb-fixbtn'), gpsCard: document.getElementById('sb-gpscard'),
  gcLat: document.getElementById('gc-lat'), gcLon: document.getElementById('gc-lon'),
  gcElev: document.getElementById('gc-elev'), gcSats: document.getElementById('gc-sats'),
  gcHdop: document.getElementById('gc-hdop'), gcFix: document.getElementById('gc-fix'),
  gcAge: document.getElementById('gc-age'), gcHdg: document.getElementById('gc-hdg'),
  gcRoll: document.getElementById('gc-roll'), gcFps: document.getElementById('gc-fps'),
  // Dev diagnostics line (marker-gated by .show_dev_overlay).
  diagRow: document.getElementById('sb-diag'),
  sbdFps: document.getElementById('sbd-fps'), sbdLink: document.getElementById('sbd-link'),
  sbdCtrl: document.getElementById('sbd-ctrl'),
};
// GPS fix-quality → dot colour (matches the native FixQualityToColor intent).
function fixColor(q) {
  switch (q) {
    case 4: return '#22c55e';  // RTK fixed — green
    case 5: return '#f59e0b';  // RTK float — amber
    case 2: return '#38bdf8';  // DGPS — blue
    case 1: return '#eab308';  // GPS — yellow
    default: return '#ef4444'; // no/invalid fix — red
  }
}
// Aggregate module colour — green only when every CONFIGURED module is present,
// amber if some, red if none (mirrors MainViewModel.ModuleStatusKind).
function moduleAggColor(s) {
  let conf = 0, pres = 0;
  const tally = (c, ok) => { if (c) { conf++; if (ok) pres++; } };
  tally(s.gpsConf, s.gpsOk); tally(s.imuConf, s.imuOk);
  tally(s.autoSteerConf, s.autoSteerOk); tally(s.machineConf, s.machineOk);
  if (conf === 0 || pres === 0) return '#ef4444';
  return pres === conf ? '#22c55e' : '#f59e0b';
}
// Area/rate formatting — matches MainViewModel.FormatArea / WorkRateDisplay.
function fmtArea(m2, metric) {
  const ha = m2 * 0.0001;
  return metric ? ha.toFixed(2) + ' ha' : (ha * 2.47105).toFixed(2) + ' ac';
}
function fmtRate(haPerHr, metric) {
  return metric ? haPerHr.toFixed(1) + ' ha/hr' : (haPerHr * 2.47105).toFixed(1) + ' ac/hr';
}
// Workable area (m²) from the Scene the client already has: headland polygon if
// present, else the outer boundary (shoelace) — matches WorkableAreaSqM.
function shoelace(pts) {
  let a = 0;
  for (let i = 0; i < pts.length; i++) { const j = (i + 1) % pts.length; a += pts[i].e * pts[j].n - pts[j].e * pts[i].n; }
  return Math.abs(a / 2);
}
function workableAreaSqM() {
  if (scene && scene.headland && scene.headland.length >= 3) return shoelace(scene.headland);
  if (scene && scene.boundaries && scene.boundaries.length && scene.boundaries[0].length >= 3)
    return shoelace(scene.boundaries[0]);
  return 0;
}
// Active track heading (deg, 0 = north) from its first two Scene points.
function activeTrackHeadingDeg() {
  if (!scene || !tick || !tick.activeTrackName) return null;
  const tr = scene.tracks.find(t => t.name === tick.activeTrackName);
  if (!tr || tr.points.length < 2) return null;
  const a = tr.points[0], b = tr.points[1];
  return Math.atan2(b.e - a.e, b.n - a.n) * 180 / Math.PI;
}
// Rotating bottom line — the three native pages (Field / Stats / AB-line).
let sbPage = 0, sbPaused = false;
function rotatingLineText() {
  const s = statusBar; if (!s) return '';
  if (sbPage === 0) { // Field
    if (!scene || !scene.hasField) return 'No field';
    return 'Field: ' + (scene.fieldName || '—') + (s.jobName ? '  Job: ' + s.jobName : '');
  }
  if (sbPage === 1) { // Stats
    if (!scene || !scene.hasField) return '—';
    const workable = workableAreaSqM(), worked = s.workedAreaSqM || 0;
    const leftPct = workable > 0 ? ((workable - worked) * 100 / workable) : 100;
    const haPerHr = (lastTick ? lastTick.speed : 0) * 3600 * toolWidthM() / 10000;
    const eta = routeEtaText();
    return 'Done ' + fmtArea(worked, s.isMetric) + '  Left ' + leftPct.toFixed(0) + '%  Rate ' + fmtRate(haPerHr, s.isMetric) + (eta ? '  ' + eta : '');
  }
  const name = tick && tick.activeTrackName; // AB-line page
  if (!name) return 'No AB Line';
  const h = activeTrackHeadingDeg();
  if (h == null) return 'AB Line: ' + name;
  const primary = ((h % 360) + 360) % 360, recip = (primary + 180) % 360;
  return 'AB Line: ' + name + '  ' + primary.toFixed(1) + '°, ' + recip.toFixed(1) + '°';
}
setInterval(() => { if (!sbPaused) sbPage = (sbPage + 1) % 3; }, 5000); // native 5 s cycle
SB.pause.addEventListener('click', () => { sbPaused = !sbPaused; SB.pause.textContent = sbPaused ? '▶' : '❚❚'; });
// Bar interaction must not pan the map (the camera listens on window pointerdown).
SB.bar.addEventListener('pointerdown', e => e.stopPropagation());
// Fullscreen toggle — hides the browser tabs/URL bar on tablets. Works on a user
// gesture over plain HTTP (no PWA install needed). Prefixed fallback for older Android.
const fsBtn = document.getElementById('sb-fs');
if (fsBtn) {
  const fsEl = () => document.fullscreenElement || document.webkitFullscreenElement;
  // Use 'click' (not pointerdown): requestFullscreen needs a durable user activation
  // that pointerdown doesn't reliably grant on Android Chrome (entering took 2–3 taps).
  // exitFullscreen needs no activation, so that always worked first tap.
  fsBtn.addEventListener('click', () => {
    if (fsEl()) { (document.exitFullscreen || document.webkitExitFullscreen).call(document); return; }
    const el = document.documentElement, req = el.requestFullscreen || el.webkitRequestFullscreen;
    if (req) { try { const p = req.call(el); if (p && p.catch) p.catch(() => {}); } catch (_) {} }
  });
  fsBtn.addEventListener('pointerdown', e => e.stopPropagation()); // don't also pan the map
  const sync = () => {
    const fs = !!fsEl();
    fsBtn.textContent = fs ? '🗗' : '⛶';
    // In fullscreen the OS draws an exit "✕" pill at the top-left (e.g. iPad Safari's
    // Fullscreen-API control) that sits over the RTK fix + rotating info fields. We can't
    // hide OS chrome, so nudge that left stack clear of it — only while fullscreen.
    document.body.classList.toggle('fs-on', fs);
  };
  document.addEventListener('fullscreenchange', sync);
  document.addEventListener('webkitfullscreenchange', sync);
}
SB.modBtn.addEventListener('pointerdown', e => {
  e.stopPropagation();
  SB.modPop.style.display = SB.modPop.style.display === 'block' ? 'none' : 'block';
});
// Fix dot toggles the GPS-detail card (mutually exclusive with the modules popup).
SB.fixBtn.addEventListener('pointerdown', e => {
  e.stopPropagation();
  const show = SB.gpsCard.style.display !== 'block';
  SB.gpsCard.style.display = show ? 'block' : 'none';
  if (show) SB.modPop.style.display = 'none';
});
addEventListener('pointerdown', () => {
  if (SB.modPop) SB.modPop.style.display = 'none';
  if (SB.gpsCard) SB.gpsCard.style.display = 'none';
  // Close bottom-nav flyouts on any outside click (clicks inside #bottomnav
  // stopPropagation, so they don't reach here).
  const ff = document.getElementById('bn-flyout-flags'), af = document.getElementById('bn-flyout-ab');
  if (ff) ff.classList.remove('open');
  if (af) af.classList.remove('open');
  document.getElementById('bn-flags')?.classList.remove('menuopen');
  document.getElementById('bn-abmenu')?.classList.remove('menuopen');
  // Left-nav panels now close via the transparent #ln-scrim (which consumes the tap),
  // not this global handler — so a panel dismiss no longer also pans the map.
});

function renderStatusBar() {
  const s = statusBar;
  if (!s) { SB.bar.style.display = 'none'; if (SB.diagRow) SB.diagRow.style.display = 'none'; return; }
  SB.bar.style.display = 'flex';
  SB.fixDot.style.background = fixColor(s.fixQuality);
  SB.fix.textContent = s.fixText || '—';
  SB.age.textContent = 'Age ' + (s.age != null ? s.age.toFixed(1) : '—');
  SB.rot.textContent = rotatingLineText();
  // Speed from the live tick (m/s), formatted per the host's unit preference.
  const mps = lastTick ? lastTick.speed : 0;
  SB.speed.textContent = (mps * (s.isMetric ? 3.6 : 2.236936)).toFixed(1);
  SB.unit.textContent = s.isMetric ? 'km/h' : 'mph';
  // Modules: aggregate dot + per-module popup rows.
  SB.modAgg.style.background = moduleAggColor(s);
  const dot = (el, ok) => { el.style.background = ok ? '#22c55e' : '#6b7280'; };
  dot(SB.mGps, s.gpsOk); dot(SB.mImu, s.imuOk); dot(SB.mAs, s.autoSteerOk); dot(SB.mMa, s.machineOk);
  SB.dGps.textContent = (s.fixText || '—') + (s.sats ? ' (' + s.sats + ' sats)' : '');
  SB.dImu.textContent = s.imuIp || 'Not detected';
  SB.dAs.textContent = s.autoSteerIp || 'Not detected';
  SB.dMa.textContent = s.machineIp || 'Not detected';
  // Heading readout (right of speed) — Tick heading is radians (0 = N, CW).
  const hdgDeg = lastTick ? (lastTick.heading * 180 / Math.PI) : null;
  SB.hdg.textContent = hdgDeg != null ? fmtHdg(hdgDeg) : '—';
  // GPS-detail card — populated only while open (the fix dot toggles it).
  if (SB.gpsCard.style.display === 'block') {
    SB.gcLat.textContent = s.lat != null ? s.lat.toFixed(7) : '—';
    SB.gcLon.textContent = s.lon != null ? s.lon.toFixed(7) : '—';
    SB.gcElev.textContent = s.altitude != null ? s.altitude.toFixed(1) : '—';
    SB.gcSats.textContent = s.sats != null ? s.sats : '—';
    SB.gcHdop.textContent = s.hdop != null ? s.hdop.toFixed(2) : '—';
    SB.gcFix.textContent = s.fixText || '—';
    SB.gcAge.textContent = s.age != null ? s.age.toFixed(1) : '—';
    SB.gcHdg.textContent = hdgDeg != null ? (((hdgDeg % 360) + 360) % 360).toFixed(1) + '°' : '—';
    SB.gcRoll.textContent = (tick && typeof tick.roll === 'number') ? tick.roll.toFixed(1) + '°' : '—';
    SB.gcFps.textContent = fps.toFixed(0);
  }
  // Dev diagnostics line (marker-gated). DOM text only — no canvas overlay — so reading FPS
  // doesn't perturb the frame loop (an overlay/dialog did, hence the status-line approach).
  // Throttled to ~4 Hz; that tick also fires the link probe. FPS = client render rate; LINK =
  // server↔client round-trip (ms, from diag.ping/PONG); CTRL = backend→AiO control-loop
  // latency (GPS receive → PGN send, ms).
  if (s.devOverlay) {
    if (SB.diagRow.style.display !== 'flex') SB.diagRow.style.display = 'flex';
    const nowMs = performance.now();
    if (nowMs - _diagT0 >= 250) {
      _diagT0 = nowMs;
      transport.send('diag.ping|' + nowMs); // link probe; the PONG reply updates linkRttMs
      SB.sbdFps.textContent = fps.toFixed(0);
      SB.sbdLink.textContent = linkRttMs != null ? linkRttMs.toFixed(1) + ' ms' : '—';
      SB.sbdCtrl.textContent = (s.gpsToPgnLatencyMs || 0).toFixed(1) + ' ms';
    }
  } else if (SB.diagRow.style.display !== 'none') {
    SB.diagRow.style.display = 'none';
  }
}
// Heading in AgOpen 000.0° form (constant width). Input degrees, any sign.
function fmtHdg(deg) {
  const d = ((deg % 360) + 360) % 360;
  return d.toFixed(1).padStart(5, '0') + '°';
}
// Simulator bar (Phase 6) — reflect host sim state onto the bar each frame.
function renderSimBar() {
  const s = statusBar;
  if (!s || s.simPanelVisible === false) return;
  SIM.enable.classList.toggle('on', !!s.simEnabled);
  SIM.tenx.classList.toggle('on', !!s.sim10x);
  // Steer: reflect the host value unless the user is mid-drag (don't fight them).
  if (!_steerDragging) {
    const deg = s.simSteerAngle || 0;
    SIM.steer.value = deg;
    SIM.steerVal.textContent = deg.toFixed(1) + '°';
  }
  // Speed readout: apply 10× then format per units (mirrors SimulatorSpeedDisplay).
  const eff = (s.simSpeedKph || 0) * (s.sim10x ? 10 : 1);
  SIM.speedVal.textContent = s.isMetric ? eff.toFixed(1) + ' kph' : (eff * 0.621371).toFixed(1) + ' mph';
  // GPS (teleport) only while sim disabled — matches the native IsEnabled binding.
  SIM.gps.disabled = !!s.simEnabled;
  SIM.gps.style.opacity = s.simEnabled ? '0.4' : '1';
}
// Section bar (Phase 7). Rebuild rows when the count changes; split rows like the
// native RebuildSectionRows: ceil(n/16) rows, top rows get the extra when uneven.
let _secCount = -1;
function buildSectionRows(n) {
  sectionBar.innerHTML = '';
  const rows = Math.ceil(n / 16);             // 1..4
  const base = Math.floor(n / rows), rem = n % rows; // first `rem` rows get +1
  let idx = 0;
  for (let r = 0; r < rows; r++) {
    const count = base + (r < rem ? 1 : 0);
    const row = document.createElement('div');
    row.className = 'sec-row';
    for (let k = 0; k < count; k++, idx++) {
      const b = document.createElement('button');
      b.className = 'sec-btn';
      b.dataset.idx = idx;
      b.textContent = idx + 1;                // 1-based section number
      row.appendChild(b);
    }
    sectionBar.appendChild(row);
  }
}
function renderSectionBar() {
  // Native gate: field open AND a master (auto or manual) engaged.
  const secs = tick && tick.sections;
  const visible = !!(scene && scene.hasField && tick && tick.op
    && (tick.op.sectionManual || tick.op.sectionAuto) && secs && secs.length);
  sectionBar.classList.toggle('open', visible);
  if (!visible) { _secCount = -1; return; } // force a rebuild when it reappears
  if (secs.length !== _secCount) { buildSectionRows(secs.length); _secCount = secs.length; }
  for (const b of sectionBar.querySelectorAll('button[data-idx]'))
    b.style.background = SECTION_COLORS[secs[+b.dataset.idx]] || SECTION_COLORS[0];
  sectionBar.classList.toggle('locked', !iHoldControl); // dim when we can't actuate
}
// Bottom nav (Phase 8). Swap icons only on change; reflect Tick toggle state.
function bnIcon(img, name) { const p = '/icons/' + name; if (img && !img.src.endsWith(p)) img.src = p; }
const TRAM_ICONS = ['TramOff.png', 'TramAll.png', 'TramLines.png', 'TramOuter.png'];
function renderBottomNav() {
  applyOnScreenButtons(); // U-Turn / Lateral glyphs follow field-open + display toggles
  const visible = !!(scene && scene.hasField); // native gate: IsFieldOpen
  bottomNav.classList.toggle('open', visible);
  if (!visible) { bnFlags.classList.remove('open'); bnAb.classList.remove('open'); return; }
  // AB-line-dependent buttons appear only with an active track.
  const hasTrack = !!(tick && tick.activeTrackName);
  for (const el of bottomNav.querySelectorAll('.bn-abdep')) el.classList.toggle('hide', !hasTrack);
  const t = (tick && tick.tools) || {};
  document.getElementById('bn-skipnum').textContent = t.skipRows || 0;
  bnIcon(document.getElementById('bn-skip-ic'), t.skipRowsOn ? 'YouSkipOn.png' : 'YouSkipOff.png');
  // Icon shows the PAINTING state, not the raw flag: sectionInHeadland (=Tool.
  // IsHeadlandSectionControl) true means sections AUTO-OFF in the headland (NOT
  // painted) → the "Off" icon; false means sections paint the headland → "On".
  bnIcon(document.getElementById('bn-secheadland-ic'), t.sectionInHeadland ? 'HeadlandSectionOff.png' : 'HeadlandSectionOn.png');
  bnIcon(document.getElementById('bn-headland-ic'), t.headlandOn ? 'HeadlandOn.png' : 'HeadlandOff.png');
  bnIcon(document.getElementById('bn-tram-ic'), TRAM_ICONS[t.tramMode || 0]);
  document.getElementById('bn-autotrack').classList.toggle('active', !!t.autoTrack);
  bottomNav.classList.toggle('locked', !iHoldControl);
}

// Right-nav operational toolbar (Phase 3a: read-only live indicators; 3b wires the
// Tier-2 commands). Colour-coded from the Tick's operational state.
const RN = {
  root: document.getElementById('rightnav'),
  contourI: document.getElementById('rn-contour-i'), manualI: document.getElementById('rn-manual-i'),
  autoI: document.getElementById('rn-auto-i'), youturnI: document.getElementById('rn-youturn-i'),
  steerI: document.getElementById('rn-steer-i'), readonly: document.getElementById('rn-readonly'),
};
// Swap an icon only when it actually changes (no per-frame churn).
function rnIcon(img, name) { if (img && !img.src.endsWith(name)) img.src = '/icons/' + name; }
function renderRightNav() {
  if (!RN.root) return;
  const op = tick && tick.op;
  if (!op || !scene || !scene.hasField) { RN.root.style.display = 'none'; return; }
  RN.root.style.display = 'flex';
  // Dim + hint when we don't hold control (buttons are no-ops then; the host also gates).
  RN.root.classList.toggle('locked', !iHoldControl);
  if (RN.readonly) RN.readonly.textContent = iHoldControl ? '' : 'observing';
  // State carried by the icon image (native uses the same On/Off/Gray PNGs).
  rnIcon(RN.contourI, op.contour ? 'ContourOn.png' : 'ContourOff.png');
  rnIcon(RN.manualI, op.sectionManual ? 'ManualOn.png' : 'ManualOff.png');
  rnIcon(RN.autoI, op.sectionAuto ? 'SectionMasterOn.png' : 'SectionMasterOff.png');
  rnIcon(RN.youturnI, op.youturn ? 'YouTurnYes.png' : 'YouTurnNo.png');
  // U-turn direction + distance-to-trigger now render on-screen (see renderUTurnIndicator).
  // AutoSteer 3-state icon: grey (no track) / off-ready / on-engaged.
  rnIcon(RN.steerI, !op.autoSteerAvail ? 'AutoSteerGray.png' : op.autoSteer ? 'AutoSteerOn.png' : 'AutoSteerOff.png');
}

// Auto-U-turn approach indicator (upper-right): green direction arrow + distance to
// trigger + turn-type glyph. Mirrors native's on-map readout; shown while auto U-turn is
// armed with a pending turn. Turn type comes from config (Omega / Sagitta); distance honours
// the metric/imperial unit like the headland HUD.
const UTI = {
  root: document.getElementById('uturn-indicator'),
  arrow: document.getElementById('uti-arrow'),
  dist: document.getElementById('uti-distval'),
  typePath: document.getElementById('uti-typepath'),
};
// Two tap zones, mirroring old AgOpenGPS's pair of AB-driving buttons:
// the ARROW side flips the pending turn's direction (Tier-2; host re-arms
// the rendered path), the TYPE glyph toggles U (sagitta) ↔ K turn — the
// K needs a reverse leg, so it's the pick for linkage-mounted tools on
// narrow headlands. Config applies live; the next armed turn uses it.
if (UTI.root) UTI.root.addEventListener('pointerdown', e => {
  e.stopPropagation();
  rnSend('youturn.direction');
});
const utiType = document.getElementById('uti-type');
if (utiType) utiType.addEventListener('pointerdown', e => {
  e.stopPropagation();   // don't also flip direction
  const cur = (config && config.uturn && config.uturn.style) || 0;
  cfgSend('uturn.style', cur === 1 ? 0 : 1);
});
const UTURN_GLYPH = {
  0: 'M7 34 Q7 20 20 20 Q33 20 33 34',         // Sagitta (default) — flatter rounded arch
  1: 'M9 34 L9 22 A11 11 0 0 1 31 22 L31 34',  // K-turn — squared/tighter inverted-U
};
function renderUTurnIndicator() {
  if (!UTI.root) return;
  const op = tick && tick.op;
  const show = !!(scene && scene.hasField && op && op.youturn && op.distToTrigger > 0);
  UTI.root.hidden = !show;
  if (!show) return;
  const metric = !statusBar || statusBar.isMetric;
  UTI.dist.textContent = metric ? op.distToTrigger.toFixed(0) + ' m'
                                : (op.distToTrigger * 3.28084).toFixed(0) + ' ft';
  UTI.arrow.classList.toggle('left', !!op.turnLeft);
  const style = (config && config.uturn && config.uturn.style) || 0;
  UTI.typePath.setAttribute('d', UTURN_GLYPH[style] || UTURN_GLYPH[0]);
  const tl = document.getElementById('uti-typelabel');
  if (tl) tl.textContent = style === 1 ? 'K' : 'U';
}
// ---- Lower-right cluster (Phase 4): roll gauge + camera/mode pad + clock ----
const rollBar = document.getElementById('roll-bar');
const rollDeg = document.getElementById('roll-deg');
function renderRoll() {
  const r = (tick && typeof tick.roll === 'number') ? tick.roll : 0;
  if (rollBar) rollBar.setAttribute('transform', 'rotate(' + r.toFixed(2) + ' 100 40)');
  if (rollDeg) rollDeg.textContent = r.toFixed(1);
}
const cpMode = document.getElementById('cp-mode');
function camModeLabel() { return cameraMode === 1 ? 'H' : cameraMode === 0 ? 'N' : cameraMode === 3 ? 'M' : 'C'; }
function renderCampad() {
  if (!cpMode) return;
  cpMode.textContent = camModeLabel();          // H / N / M / C (native labels)
  cpMode.classList.toggle('free', cameraMode === 2);
}
// Camera pad drives the client-owned camera (pitch / zoom / follow) — no host commands.
(function wireCampad() {
  const pad = document.getElementById('campad');
  if (!pad) return;
  pad.addEventListener('pointerdown', e => e.stopPropagation()); // don't pan the map
  // Bind on POINTERDOWN, not click: the NativeWebView launcher (touch) doesn't reliably fire
  // `click`, so these camera buttons — notably the mode/heading cycle — did nothing when tapped
  // on the launcher build (issue #56). pointerdown is the same event the rest of the touch UI
  // uses; the pad's stopPropagation above already keeps it from panning the map.
  document.getElementById('cp-tiltup').addEventListener('pointerdown', () => { pitch = Math.max(0, pitch - PITCH_STEP); });
  document.getElementById('cp-tiltdown').addEventListener('pointerdown', () => {
    pitch = Math.min(MAX_PITCH, pitch + PITCH_STEP);
  });
  document.getElementById('cp-zoomin').addEventListener('pointerdown', () => { pxPerM = Math.min(200, pxPerM * 1.2); });
  document.getElementById('cp-zoomout').addEventListener('pointerdown', () => { pxPerM = Math.max(0.2, pxPerM * 0.83); });
  // Center: cycle the four native modes H → N → M → C → H; recenter on follow modes.
  document.getElementById('cp-mode').addEventListener('pointerdown', () => {
    cameraMode = cameraMode === 1 ? 0 : cameraMode === 0 ? 3 : cameraMode === 3 ? 2 : 1;
    if (cameraMode !== 2) { const rp = renderPose(); if (rp) { camE = rp.e; camN = rp.n; } }
  });
})();
// ── Diagnostic charts (Tools panel) ─────────────────────────────────────────
// Thin-client port of the native ChartControl: the host streams the scalar
// series sources on each Tick (see TickDto chart fields); we keep a rolling
// display buffer per series and redraw the open chart cards each frame to a 2D
// canvas. Configs mirror the native *ChartPanel.ConfigureChart() calls 1:1.
const CHART_WINDOW = 20; // seconds (IChartDataService.TimeWindowSeconds default)
const CHARTS = {
  steer: {
    title: 'Steer', yLabel: 'deg', minY: -40, maxY: 40, step: 10, auto: false,
    series: [
      { name: 'Set Angle', color: '#E05020', pts: [] },
      { name: 'Actual Angle', color: '#2080E0', pts: [] },
      { name: 'PWM', color: '#00A080', pts: [] },
    ],
  },
  heading: {
    title: 'Heading', yLabel: 'deg', minY: 0, maxY: 360, step: 45, auto: true,
    series: [
      { name: 'Heading Error', color: '#DD3333', pts: [] },
      { name: 'IMU Heading', color: '#D07020', pts: [] },
      { name: 'GPS Heading', color: '#0088AA', pts: [] },
    ],
  },
  xte: {
    title: 'XTE', yLabel: 'm', minY: -2, maxY: 2, step: 0.5, auto: true,
    series: [{ name: 'XTE', color: '#C020C0', pts: [] }],
  },
};
const chartOpen = { steer: false, heading: false, xte: false };

// Buffer the latest Tick into every series (always, even while a card is closed,
// so opening mid-session shows recent history — matches ChartDataService.Start()).
function pushChartData(t) {
  const now = performance.now() / 1000;
  const hdgDeg = (((t.pose ? t.pose.heading : 0) * 180 / Math.PI) % 360 + 360) % 360;
  const vals = {
    steer: [t.chartSetSteer, t.chartActualSteer, t.chartPwm],
    // HeadingError mirrors the native quirk (ComputeHeadingError == set steer angle).
    heading: [t.chartSetSteer, t.chartImuHeading, hdgDeg],
    xte: [t.crossTrackError],
  };
  const trim = now - CHART_WINDOW - 2;
  for (const key in CHARTS) {
    const arr = vals[key];
    const series = CHARTS[key].series;
    for (let i = 0; i < series.length; i++) {
      const pts = series[i].pts;
      pts.push({ t: now, v: arr[i] });
      let cut = 0; while (cut < pts.length && pts[cut].t < trim) cut++;
      if (cut) pts.splice(0, cut);
    }
  }
}

function setChartOpen(key, open) {
  chartOpen[key] = open;
  document.getElementById('chart-' + key).style.display = open ? 'block' : 'none';
  const btn = document.querySelector('.tl-chartbtn[data-chart="' + key + '"]');
  if (btn) btn.classList.toggle('active', open);
}
function toggleChart(key) { setChartOpen(key, !chartOpen[key]); }
// Reflect open/closed state on the Tools fly-out chart buttons when it opens.
function renderToolsPanel() {
  for (const key in chartOpen) {
    const btn = document.querySelector('.tl-chartbtn[data-chart="' + key + '"]');
    if (btn) btn.classList.toggle('active', chartOpen[key]);
  }
}

function chartNiceStep(range, targetLines) {
  const raw = range / targetLines;
  const mag = Math.pow(10, Math.floor(Math.log10(raw)));
  const norm = raw / mag;
  const nice = norm <= 1.5 ? 1 : norm <= 3.5 ? 2 : norm <= 7.5 ? 5 : 10;
  return nice * mag;
}

function drawChart(cv, c) {
  const ctx = cv.getContext('2d');
  const dpr = window.devicePixelRatio || 1;
  const W = 400, H = 150;
  if (cv.width !== Math.round(W * dpr)) { cv.width = Math.round(W * dpr); cv.height = Math.round(H * dpr); }
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, W, H);
  ctx.font = '10px system-ui,sans-serif';
  ctx.textBaseline = 'alphabetic';

  // Background + frame.
  ctx.fillStyle = '#10141d';
  ctx.fillRect(0, 0, W, H);

  const L = 50, R = 10, T = 8, B = 20;
  const cl = L, cr = W - R, ctop = T, cbot = H - B;
  const cw = cr - cl, ch = cbot - ctop;
  if (cw <= 0 || ch <= 0) return;

  // Y range (auto-scale mirrors native ComputeAutoScale: 10% pad + nice step).
  let minY = c.minY, maxY = c.maxY, step = c.step;
  if (c.auto) {
    let dmin = Infinity, dmax = -Infinity;
    for (const s of c.series) for (const p of s.pts) { if (p.v < dmin) dmin = p.v; if (p.v > dmax) dmax = p.v; }
    if (dmin !== Infinity) {
      let range = dmax - dmin; if (range < 1) range = 1;
      const pad = range * 0.1;
      minY = dmin - pad; maxY = dmax + pad;
      step = chartNiceStep(maxY - minY, 5);
    }
  }
  let yRange = maxY - minY; if (yRange <= 0) yRange = 1;

  const now = performance.now() / 1000;
  const timeStart = now - CHART_WINDOW;

  // Horizontal grid + Y labels.
  ctx.strokeStyle = 'rgba(140,155,180,0.25)'; ctx.lineWidth = 0.5;
  ctx.fillStyle = '#aeb8c8'; ctx.textAlign = 'right';
  const fmt = step >= 1 ? 0 : step >= 0.1 ? 1 : 2;
  const firstY = Math.ceil(minY / step) * step;
  for (let val = firstY; val <= maxY + 1e-9; val += step) {
    const y = cbot - ((val - minY) / yRange * ch);
    ctx.beginPath(); ctx.moveTo(cl, y); ctx.lineTo(cr, y); ctx.stroke();
    ctx.fillText(val.toFixed(fmt), cl - 4, y + 3);
  }

  // Zero line.
  if (minY < 0 && maxY > 0) {
    const zy = cbot - ((-minY) / yRange * ch);
    ctx.strokeStyle = 'rgba(206,214,230,0.5)'; ctx.lineWidth = 1;
    ctx.beginPath(); ctx.moveTo(cl, zy); ctx.lineTo(cr, zy); ctx.stroke();
  }

  // Series polylines (clipped to chart area).
  ctx.save();
  ctx.beginPath(); ctx.rect(cl, ctop, cw, ch); ctx.clip();
  ctx.lineWidth = 1.5;
  for (const s of c.series) {
    if (s.pts.length < 2) continue;
    ctx.strokeStyle = s.color;
    ctx.beginPath();
    let started = false;
    for (const p of s.pts) {
      if (p.t < timeStart) continue;
      const x = cl + ((p.t - timeStart) / CHART_WINDOW * cw);
      const y = cbot - ((p.v - minY) / yRange * ch);
      if (!started) { ctx.moveTo(x, y); started = true; } else ctx.lineTo(x, y);
    }
    if (started) ctx.stroke();
  }
  ctx.restore();

  // Border.
  ctx.strokeStyle = 'rgba(140,155,180,0.4)'; ctx.lineWidth = 1;
  ctx.strokeRect(cl, ctop, cw, ch);

  // Title (top-left of chart area).
  ctx.fillStyle = '#aeb8c8'; ctx.textAlign = 'left';
  ctx.font = '11px system-ui,sans-serif';
  ctx.fillText(c.title, cl + 4, ctop + 11);

  // Legend (top-right, right-to-left).
  ctx.font = '9px system-ui,sans-serif';
  let lx = cr - 8;
  for (let i = c.series.length - 1; i >= 0; i--) {
    const s = c.series[i];
    const tw = ctx.measureText(s.name).width;
    lx -= tw;
    ctx.fillStyle = s.color;
    ctx.textAlign = 'left';
    ctx.fillText(s.name, lx, ctop + 9);
    ctx.strokeStyle = s.color; ctx.lineWidth = 2;
    ctx.beginPath(); ctx.moveTo(lx - 14, ctop + 6); ctx.lineTo(lx - 4, ctop + 6); ctx.stroke();
    lx -= 22;
  }

  // Time labels (relative seconds, −window … 0).
  ctx.fillStyle = '#aeb8c8'; ctx.textAlign = 'center'; ctx.font = '9px system-ui,sans-serif';
  const labelCount = 5;
  for (let i = 0; i <= labelCount; i++) {
    const frac = i / labelCount;
    const x = cl + frac * cw;
    const rel = Math.round(-CHART_WINDOW + frac * CHART_WINDOW);
    ctx.fillText(rel + 's', x, cbot + 13);
    ctx.strokeStyle = 'rgba(140,155,180,0.25)'; ctx.lineWidth = 0.5;
    ctx.beginPath(); ctx.moveTo(x, ctop); ctx.lineTo(x, cbot); ctx.stroke();
  }
}

function renderCharts() {
  for (const key in chartOpen) {
    if (!chartOpen[key]) continue;
    const cv = document.getElementById('chart-' + key).querySelector('.chart-cv');
    drawChart(cv, CHARTS[key]);
  }
}

// Roll Correction panel: live gauge + offset/invert readouts (only when open).
// Mirrors the wizard roll-cal step; the bar rotates by the live (post-calibration)
// roll the same way the map roll gauge does.
function renderRollCorr() {
  const panel = document.getElementById('rollcorr');
  if (!panel.classList.contains('open')) return;
  const rv = (tick && typeof tick.roll === 'number') ? tick.roll : 0;
  const bar = document.getElementById('rc-roll-bar');
  if (bar) bar.setAttribute('transform', 'rotate(' + rv.toFixed(2) + ' 100 40)');
  const deg = document.getElementById('rc-roll-deg');
  if (deg) deg.textContent = rv.toFixed(1);
  const roll = (config && config.roll) || {};
  const off = document.getElementById('rc-offset');
  if (off) off.textContent = (typeof roll.rollZero === 'number' ? roll.rollZero : 0).toFixed(2) + '°';
  const inv = document.getElementById('rc-invert');
  if (inv) { const on = !!roll.isRollInvert; inv.classList.toggle('on', on); inv.textContent = on ? 'On' : 'Off'; }
}

// Offset Fix: reflect the live drift offset into the manual inputs (unless the user is
// editing that field). Drift rides the Status frame.
function renderOffsetFix() {
  if (!document.getElementById('offsetfix').classList.contains('open')) return;
  if (!statusBar) return;
  const ns = document.getElementById('of-ns-in'), ew = document.getElementById('of-ew-in');
  if (document.activeElement !== ns) ns.value = (statusBar.driftNorthing || 0).toFixed(3);
  if (document.activeElement !== ew) ew.value = (statusBar.driftEasting || 0).toFixed(3);
}

// Import Tracks: list the other fields that have saved tracks; tap one to copy its
// tracks into the open field (host ImportTracksFromFieldCommand transforms origins).
function renderImportTracks() {
  const list = document.getElementById('it-list'); list.innerHTML = '';
  const fields = fieldTools ? fieldTools.importFields : [];
  if (!fields || !fields.length) {
    list.innerHTML = '<div class="fj-empty">No other fields with tracks found.</div>';
    return;
  }
  for (const name of fields) {
    const row = document.createElement('div');
    row.className = 'fj-jrow';
    row.innerHTML = '<div class="fj-jtop"><span class="fj-jname"></span></div>';
    row.querySelector('.fj-jname').textContent = name;
    row.addEventListener('pointerdown', ev => {
      ev.stopPropagation();
      transport.send('field.importTracks|' + name);
      lnCloseAll();
    });
    list.appendChild(row);
  }
}

// Recorded Path: GPS-driven record (Tier-1) + playback (Play is gated). All state
// comes from the host-driven RecordedPath frame; the name field is local input.
function renderRecPath() {
  const r = recPath || { recFiles: [], isRecording: false, isPlaying: false, hasUnsaved: false, recordedPathInfo: '', resumeModeLabel: 'Start' };
  document.getElementById('rp-tab-rec').classList.toggle('active', recPathTab === 0);
  document.getElementById('rp-tab-play').classList.toggle('active', recPathTab === 1);
  document.getElementById('rp-record').classList.toggle('active', recPathTab === 0);
  document.getElementById('rp-play').classList.toggle('active', recPathTab === 1);
  // Record tab.
  document.getElementById('rp-start').style.display = r.isRecording ? 'none' : '';
  document.getElementById('rec-stop').style.display = r.isRecording ? '' : 'none';
  document.getElementById('rp-recind').style.display = r.isRecording ? 'block' : 'none';
  document.getElementById('rp-saverow').style.display = r.hasUnsaved ? 'flex' : 'none';
  // Auto-fill the Save-as name from the host's generated default (native parity), but
  // don't clobber the user mid-edit.
  const nameIn = document.getElementById('rp-name');
  if (document.activeElement !== nameIn && r.recordedPathName) nameIn.value = r.recordedPathName;
  // Playback tab.
  document.getElementById('rp-playimg').src = r.isPlaying ? '/icons/RecPathStop.png' : '/icons/RecPathPlay.png';
  document.getElementById('rp-playlbl').textContent = r.isPlaying ? 'Stop' : 'Play';
  document.getElementById('rp-playbtn').classList.toggle('disabled', !iHoldControl);
  document.getElementById('rp-resumelbl').textContent = r.resumeModeLabel || 'Start';
  const list = document.getElementById('rp-list'); list.innerHTML = '';
  if (!r.recFiles || !r.recFiles.length) {
    list.innerHTML = '<div class="fj-empty">No saved paths.</div>';
  } else {
    for (const name of r.recFiles) {
      const row = document.createElement('div');
      row.className = 'fj-jrow rp-frow' + (name === recSelFile ? ' sel' : '');
      row.innerHTML = '<span class="fj-jname"></span><button class="rp-del" title="Delete">🗑</button>';
      row.querySelector('.fj-jname').textContent = name;
      row.querySelector('.fj-jname').addEventListener('pointerdown', ev => {
        ev.stopPropagation(); recSelFile = name; transport.send('recpath.selectFile|' + name); renderRecPath();
      });
      row.querySelector('.rp-del').addEventListener('pointerdown', ev => {
        ev.stopPropagation();
        showConfirm('Delete Recorded Path', 'Delete "' + name + '"? This cannot be undone.', () => {
          if (recSelFile === name) recSelFile = null;
          transport.send('recpath.delete|' + name);
        });
      });
      list.appendChild(row);
    }
  }
  document.getElementById('rp-info').textContent = r.recordedPathInfo || '';
}

// Boundary recording menu: the field's boundaries (Outer / Inner N) with area + the
// Drive-Thru / Hard flags. Tap a row to select; tap a flag cell to select + toggle it.
function renderBoundaryMenu() {
  const b = boundary;
  const list = document.getElementById('bm-list'); list.innerHTML = '';
  const items = b ? b.items : [];
  if (!items.length) { list.innerHTML = '<div class="fj-empty">No boundaries yet — drive around the field to record one.</div>'; return; }
  for (const it of items) {
    const row = document.createElement('div');
    row.className = 'bm-row' + (b && it.index === b.selectedIndex ? ' sel' : '');
    // Coverage flag: default true = "Stop" (coverage stops at the edge). The notable
    // state is false = "Spread" (product allowed beyond, e.g. broadcast over a fence),
    // so highlight 'on' when it's OFF.
    row.innerHTML = '<span class="bm-name"></span><span class="bm-area"></span>'
      + '<span class="bm-flag ' + (it.driveThru ? 'on' : 'off') + '" data-flag="driveThru"></span>'
      + '<span class="bm-flag ' + (it.hard ? 'on' : 'off') + '" data-flag="hard"></span>'
      + '<span class="bm-flag ' + (it.stopCoverage ? 'off' : 'on') + '" data-flag="stopCoverage"></span>';
    row.querySelector('.bm-name').textContent = it.boundaryType;
    row.querySelector('.bm-area').textContent = it.areaDisplay;
    row.querySelector('[data-flag="driveThru"]').textContent = it.driveThru ? 'Yes' : '--';
    row.querySelector('[data-flag="hard"]').textContent = it.hard ? 'Hard' : 'Soft';
    row.querySelector('[data-flag="stopCoverage"]').textContent = it.stopCoverage ? 'Stop' : 'Spread';
    row.addEventListener('pointerdown', ev => { ev.stopPropagation(); transport.send('boundary.select|' + it.index); });
    row.querySelector('[data-flag="driveThru"]').addEventListener('pointerdown', ev => {
      ev.stopPropagation(); transport.send('boundary.select|' + it.index); transport.send('boundary.driveThru');
    });
    row.querySelector('[data-flag="hard"]').addEventListener('pointerdown', ev => {
      ev.stopPropagation(); transport.send('boundary.select|' + it.index); transport.send('boundary.hard');
    });
    row.querySelector('[data-flag="stopCoverage"]').addEventListener('pointerdown', ev => {
      ev.stopPropagation(); transport.send('boundary.select|' + it.index); transport.send('boundary.stopCoverage');
    });
    list.appendChild(row);
  }
}

// Boundary player: live drive-around recording controls + point count / area.
function renderBoundaryPlayer() {
  const b = boundary; if (!b) return;
  const off = document.getElementById('bp-offset');
  if (document.activeElement !== off) off.value = (b.offsetCm || 0).toFixed(0);
  document.getElementById('bp-section').classList.toggle('on', b.sectionControlOn);
  document.getElementById('bp-lrimg').src = b.drawRightSide ? '/icons/BoundaryRight.png' : '/icons/BoundaryLeft.png';
  document.getElementById('bp-atimg').src = b.drawAtPivot ? '/icons/BoundaryRecordPivot.png' : '/icons/BoundaryRecordTool.png';
  document.getElementById('bp-points').textContent = b.pointCount;
  document.getElementById('bp-area').textContent = (b.areaHa || 0).toFixed(2) + ' Ha';
  document.getElementById('bp-recimg').src = b.isRecording ? '/icons/boundaryPause.png' : '/icons/BoundaryRecord.png';
}

// Drag chart cards by the header (web docks ln-panels, but charts are free overlays
// like the native FloatingPanel so the operator can move them out of the way).
(function wireChartDrag() {
  for (const key of ['steer', 'heading', 'xte']) {
    const card = document.getElementById('chart-' + key);
    const hdr = card.querySelector('.chart-hdr');
    let drag = false, sx = 0, sy = 0, ox = 0, oy = 0;
    card.addEventListener('pointerdown', e => e.stopPropagation()); // don't pan the map
    hdr.addEventListener('pointerdown', e => {
      if (e.target.classList.contains('chart-x')) return;
      e.stopPropagation();
      const r = card.getBoundingClientRect();
      drag = true; sx = e.clientX; sy = e.clientY; ox = r.left; oy = r.top;
      card.style.left = ox + 'px'; card.style.top = oy + 'px'; card.style.right = 'auto';
      try { hdr.setPointerCapture(e.pointerId); } catch (_) {}
    });
    hdr.addEventListener('pointermove', e => {
      if (!drag) return;
      card.style.left = (ox + e.clientX - sx) + 'px';
      card.style.top = (oy + e.clientY - sy) + 'px';
    });
    const end = e => { drag = false; try { hdr.releasePointerCapture(e.pointerId); } catch (_) {} };
    hdr.addEventListener('pointerup', end);
    hdr.addEventListener('pointercancel', end);
  }
})();

// Clock — browser-local 24h HH:MM:SS.
const clockEl = document.getElementById('clock');
function tickClock() {
  if (!clockEl) return;
  const d = new Date(), p = n => String(n).padStart(2, '0');
  clockEl.textContent = p(d.getHours()) + ':' + p(d.getMinutes()) + ':' + p(d.getSeconds());
}
setInterval(tickClock, 1000); tickClock();

// Reference grid + origin axes (procedural, client-side — no wire). Spacing is a
// tool-width multiple on a 1-2-5 series keyed to zoom, matching the native grid
// (#417). Axes through the field origin (local 0,0): X (N=0) red, Y (E=0) green.
// Matches native NiceStep125 exactly: ≤1 → 1, then 2, 5, 10, 20 … (no 1.5 step).
function niceStep125(x) {
  if (x <= 1) return 1;
  const pow = Math.pow(10, Math.floor(Math.log10(x)));
  const f = x / pow;
  return (f <= 2 ? 2 : f <= 5 ? 5 : 10) * pow;
}
function toolWidthM() {
  const ts = scene && scene.toolSections;
  if (ts && ts.length) {
    let lo = Infinity, hi = -Infinity;
    for (const s of ts) { if (s.left < lo) lo = s.left; if (s.right > hi) hi = s.right; }
    if (hi > lo) return hi - lo;
  }
  return 6;
}
// HUD text (DOM). Built each frame from the latest tick/scene.
// ---- Skia (CanvasKit) render — Phase A: vector layers at parity. Works in CSS
//      px (canvas.scale(dpr)), reusing w2s. Coverage/imagery/tool/lightbar TODO. ----
// CanvasKit 0.41: Path is immutable (no moveTo/reset) — build via MakeFromCmds
// (flat [VERB,x,y,...]) for polylines; drawLine for single segments.
function strokePtsSk(canvas, pts, close, paint) {
  if (!pts || pts.length < 2) return;
  strokePtsSk3D(canvas, pts, close, paint);
}
// Extend a 2-point AB line far past both ends so the reference line spans the field instead of
// being a short stub (issue #37). Curves (>2 points) pass through unchanged. The viewport clip
// in strokePtsSk3D bounds the draw cost of the now-long line. AB_EXTEND covers any field.
const AB_EXTEND = 3000; // metres added beyond each endpoint
function extendAbLine(pts) {
  if (!pts || pts.length !== 2) return pts;
  const a = pts[0], b = pts[1];
  let dx = b.e - a.e, dy = b.n - a.n;
  const len = Math.hypot(dx, dy);
  if (len < 1e-6) return pts;
  dx /= len; dy /= len;
  return [
    { e: a.e - dx * AB_EXTEND, n: a.n - dy * AB_EXTEND },
    { e: b.e + dx * AB_EXTEND, n: b.n + dy * AB_EXTEND },
  ];
}
// Perspective path for strokePtsSk: a vertex behind the tilted camera (w < EPS)
// projects through w2s with a negative w → a mirrored ghost segment (the same bug
// clipNear() fixes for single grid segments). So walk the polyline in WORLD space,
// split it at every near-plane crossing, and stroke each continuous front-facing run
// in screen space — only ever feeding w2s points that are in front of the camera.
// Clip a screen-space polyline to the viewport (+margin) into connected sub-polylines. Under
// tilt a line recedes to the horizon vanishing point, so its PROJECTED length — and a dashed
// stroke's dash count — is effectively unbounded, which collapsed FPS. Clipping bounds the work
// to on-screen length. Cohen–Sutherland per segment, re-joining contiguous pieces. [x,y] arrays.
function clipScreenPolyline(pts, margin) {
  const x0 = -margin, y0 = -margin, x1 = vw + margin, y1 = vh + margin;
  const code = (x, y) => (x < x0 ? 1 : x > x1 ? 2 : 0) | (y < y0 ? 4 : y > y1 ? 8 : 0);
  const out = [];
  let cur = null;
  for (let i = 0; i + 1 < pts.length; i++) {
    let ax = pts[i][0], ay = pts[i][1], bx = pts[i + 1][0], by = pts[i + 1][1];
    let ca = code(ax, ay), cb = code(bx, by), accept = false;
    for (let g = 0; g < 8; g++) {
      if (!(ca | cb)) { accept = true; break; }   // both inside
      if (ca & cb) break;                          // both outside the same edge → reject
      const c = ca || cb; let nx, ny;
      if (c & 8) { nx = ax + (bx - ax) * (y1 - ay) / (by - ay); ny = y1; }
      else if (c & 4) { nx = ax + (bx - ax) * (y0 - ay) / (by - ay); ny = y0; }
      else if (c & 2) { ny = ay + (by - ay) * (x1 - ax) / (bx - ax); nx = x1; }
      else { ny = ay + (by - ay) * (x0 - ax) / (bx - ax); nx = x0; }
      if (c === ca) { ax = nx; ay = ny; ca = code(ax, ay); }
      else { bx = nx; by = ny; cb = code(bx, by); }
    }
    if (!accept) { if (cur) { out.push(cur); cur = null; } continue; }
    if (cur && Math.abs(cur[cur.length - 1][0] - ax) < 0.5 && Math.abs(cur[cur.length - 1][1] - ay) < 0.5)
      cur.push([bx, by]);
    else { if (cur) out.push(cur); cur = [[ax, ay], [bx, by]]; }
  }
  if (cur) out.push(cur);
  return out;
}
function strokePtsSk3D(canvas, pts, close, paint) {
  const EPS = 1.0;
  const n = pts.length;
  const wOf = (p) => pw(p.e, p.n);
  // World point where segment a→b crosses the near plane (a, b straddle it).
  const cross = (a, b, wa, wb) => {
    const t = (EPS - wa) / (wb - wa);
    return [a.e + (b.e - a.e) * t, a.n + (b.n - a.n) * t];
  };
  let run = [];
  const flush = () => {
    // Clip the front-facing screen run to the viewport before stroking — bounds a dashed
    // line's dash flattening (and stroke fill) to on-screen length, the tilt FPS fix.
    if (run.length >= 2) {
      for (const sub of clipScreenPolyline(run, 48)) {
        if (sub.length < 2) continue;
        const cmds = [];
        for (let i = 0; i < sub.length; i++)
          cmds.push(i === 0 ? CK.MOVE_VERB : CK.LINE_VERB, sub[i][0], sub[i][1]);
        const path = CK.Path.MakeFromCmds(cmds);
        if (path) { canvas.drawPath(path, paint); path.delete(); }
      }
    }
    run = [];
  };
  const segCount = close ? n : n - 1;
  for (let i = 0; i < segCount; i++) {
    const a = pts[i], b = pts[(i + 1) % n];
    const wa = wOf(a), wb = wOf(b);
    const aFront = wa >= EPS, bFront = wb >= EPS;
    if (aFront && bFront) {                       // whole segment in front
      if (run.length === 0) run.push(w2s(a.e, a.n));
      run.push(w2s(b.e, b.n));
    } else if (aFront && !bFront) {               // exits behind: clip end, break run
      if (run.length === 0) run.push(w2s(a.e, a.n));
      const c = cross(a, b, wa, wb);
      run.push(w2s(c[0], c[1]));
      flush();
    } else if (!aFront && bFront) {               // enters from behind: start at clip
      flush();
      const c = cross(a, b, wa, wb);
      run.push(w2s(c[0], c[1]));
      run.push(w2s(b.e, b.n));
    } else {                                      // wholly behind: nothing to draw
      flush();
    }
  }
  flush();
}
// Field flags — filled dot (0.8 m radius like native, min 4 px) + dark outline,
// coloured by the flag's hex. Skips flags behind the tilted camera (near-plane).
// Live recorded-path markers (Recorded Path → Record). The host streams the points
// captured so far on the RecordedPath frame; draw each as a dot so the path is visible
// as it's driven, like native's growing "Recording…" track.
function drawRecordingMarkersSk(canvas) {
  const r = recPath;
  if (!r || !r.recordingPoints || r.recordingPoints.length < 2) return;
  const pts = r.recordingPoints;
  const rad = Math.max(3, 0.4 * pxPerM);
  SKP.flagFill.setColor(ckColor('#FFD040')); // recorded-path yellow
  for (let i = 0; i + 1 < pts.length; i += 2) {
    const e = pts[i], n = pts[i + 1];
    if (pw(e, n) < 1.0) continue; // behind camera
    const xy = w2s(e, n);
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagFill);
  }
}
// Live drive-around boundary (Boundary → Drive Around): the points captured so far,
// streamed on the Boundary frame. Draw a connecting line + dots so it's visible as it's
// driven, like native's growing recording.
function drawBoundaryRecordingSk(canvas) {
  const b = boundary;
  if (!b || !b.recordingPoints || b.recordingPoints.length < 2) return;
  const flat = b.recordingPoints;
  const pts = [];
  for (let i = 0; i + 1 < flat.length; i += 2) pts.push({ e: flat[i], n: flat[i + 1] });
  if (pts.length >= 2) strokePtsSk(canvas, pts, false, SKP.boundary);
  const rad = Math.max(3, 0.35 * pxPerM);
  SKP.flagFill.setColor(ckColor('#E05220'));
  for (const p of pts) {
    if ((pw(p.e, p.n)) < 1.0) continue;
    const xy = w2s(p.e, p.n);
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagFill);
  }
}
// Live boundary draw-on-map preview (Phase MT) — the polygon vertices tapped on the
// satellite imagery so far. Closed white outline + outlined dots; client-side only.
// Extra guidelines (Phase MT parity) — the adjacent passes ±tool-width × i from the
// followed line, when Display.ExtraGuidelines is on. Mirrors native DrawExtraGuidelinesSk:
// offset the active/display line by ±toolW·i and stroke faint green (over a shadow), with
// a zoom gate so the lines don't collapse into a band.
function offsetLine(pts, offset) {
  const out = new Array(pts.length);
  for (let i = 0; i < pts.length; i++) {
    const a = pts[Math.max(0, i - 1)], b = pts[Math.min(pts.length - 1, i + 1)];
    const dx = b.e - a.e, dy = b.n - a.n, len = Math.hypot(dx, dy);
    if (len < 1e-6) { out[i] = pts[i]; continue; }
    out[i] = { e: pts[i].e + (-dy / len) * offset, n: pts[i].n + (dx / len) * offset };
  }
  return out;
}
function drawExtraGuidelinesSk(canvas) {
  const d = config && config.display;
  if (!d || !d.extraGuidelines) return;
  const count = d.extraGuidelinesCount || 0;
  if (count < 1) return;
  const line = scene && scene.guidanceLine; // the followed pass (native offsets ActiveTrack)
  if (!line || line.length < 2) return;
  const toolW = toolWidthM();
  if (toolW < 0.1 || toolW * pxPerM < 3) return; // zoom gate: skip when passes < ~3 px apart
  for (let i = 1; i <= count; i++) {
    for (const off of [toolW * i, -toolW * i]) {
      const ol = offsetLine(line, off);
      strokePtsSk(canvas, ol, false, SKP.extraGuideShadow);
      strokePtsSk(canvas, ol, false, SKP.extraGuide);
    }
  }
}
function drawSatBoundarySk(canvas) {
  if (!satBnd || !satPts.length) return;
  if (satPts.length >= 2) strokePtsSk(canvas, satPts, satPts.length >= 3, SKP.boundary);
  const rad = Math.max(4, 0.5 * pxPerM);
  SKP.flagFill.setColor(ckColor('#FFFFFF'));
  for (const p of satPts) {
    if ((pw(p.e, p.n)) < 1.0) continue;
    const xy = w2s(p.e, p.n);
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagFill);
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagOutline);
  }
}
// #40: preview the AB/curve points being drawn as native-style dots — orange A (first),
// blue B (last), white interior — so the placed A point is visible before B is set. The
// host holds the real point list; drawPts is the client's tap echo (preview only).
function drawAbDrawPreviewSk(canvas) {
  if (!drawPts.length) return; // renders for map-tap AND Drive-AB markers (drawMode may be null)
  const rad = Math.max(5, 0.6 * pxPerM);
  for (let i = 0; i < drawPts.length; i++) {
    const p = drawPts[i];
    if (pw(p.e, p.n) < 1.0) continue; // behind the near plane
    const xy = w2s(p.e, p.n);
    SKP.flagFill.setColor(ckColor(abLabelColor(i))); // A orange, B blue, rest white — matches labels
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagFill);
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagOutline);
  }
}
// Tram lines (wheel tracks) generated from the tram systems. Drawn when the tram display is
// on (bottom-nav tram mode > 0) or while the Field Builder Tram tab is open (so you see the
// effect of edits). Orange polylines; the host owns the geometry.
function drawTramLinesSk(canvas) {
  if (!scene || !scene.tramLines || !scene.tramLines.length) return;
  const fbTramOpen = document.getElementById('fieldbuilder').classList.contains('open') && fbTab === 'tram';
  const tramOn = tick && tick.tools && tick.tools.tramMode > 0;
  if (!fbTramOpen && !tramOn) return;
  SKP.tram.setStrokeWidth(Math.max(2, 0.3 * pxPerM));
  for (const line of scene.tramLines) if (line.length >= 2) strokePtsSk(canvas, line, false, SKP.tram);
}
// Field Builder Headland editor — draw each segment's offset line + overshoot extensions
// (yellow = contributes to the closed headland, red = doesn't yet) ONLY while the Headland
// tab is open, so the operator can watch the lines cross and enclose the area, mirroring
// native's "Create and Edit Headland" view. The built headland polygon draws separately.
function drawHeadlandSegEditLinesSk(canvas) {
  if (!scene || !scene.headlandSegs || !scene.headlandSegs.length) return;
  const fb = document.getElementById('fieldbuilder');
  if (!fb.classList.contains('open') || fbTab !== 'headland') return;
  const w = Math.max(2, 0.4 * pxPerM);
  for (const s of scene.headlandSegs) {
    if (!s.editLine || s.editLine.length < 2) continue;
    const paint = s.effective ? SKP.hlEdit : SKP.hlEditOff;
    paint.setStrokeWidth(w);
    strokePtsSk(canvas, s.editLine, false, paint);
  }
}
// Live headland draw preview (Field Builder stage 2) — the boundary points tapped so far,
// snapped to the nearest boundary vertex. Green (headland colour) dots + connecting line;
// the host builds the real inward-offset segment + headland on finish.
function drawHeadlandDrawSk(canvas) {
  if (!hlFlow || !hlPts.length) return;
  if (hlPts.length >= 2) strokePtsSk(canvas, hlPts, false, SKP.headland);
  const rad = Math.max(4, 0.6 * pxPerM);
  SKP.flagFill.setColor(ckColor('#6BFF6B'));
  for (const p of hlPts) {
    if ((pw(p.e, p.n)) < 1.0) continue;
    const xy = w2s(p.e, p.n);
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagFill);
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagOutline);
  }
}
// Live draw-track preview (Phase MT) — the points tapped so far while drawing an AB/curve
// on the map. Client-side only (the host holds the authoritative list); cleared on
// finish/cancel. Cyan line through the points + an outlined dot at each.
function drawDrawingSk(canvas) {
  if (!drawMode || !drawPts.length) return;
  if (drawPts.length >= 2) strokePtsSk(canvas, drawPts, false, SKP.track);
  const rad = Math.max(4, 0.5 * pxPerM);
  SKP.flagFill.setColor(ckColor('#40E0FF'));
  for (const p of drawPts) {
    if ((pw(p.e, p.n)) < 1.0) continue; // behind camera
    const xy = w2s(p.e, p.n);
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagFill);
    canvas.drawCircle(xy[0], xy[1], rad, SKP.flagOutline);
  }
}
function drawFlagsSk(canvas, flags) {
  if (!flags || !flags.length) return;
  const r = Math.max(4, 0.8 * pxPerM);
  for (const fl of flags) {
    if (pw(fl.e, fl.n) < 1.0) continue; // behind camera
    const xy = w2s(fl.e, fl.n);
    SKP.flagFill.setColor(ckColor(fl.color || '#FF0000'));
    canvas.drawCircle(xy[0], xy[1], r, SKP.flagFill);
    canvas.drawCircle(xy[0], xy[1], r, SKP.flagOutline);
  }
}
function drawGridSk(canvas) {
  const disp = config && config.display;
  if (!disp || !disp.gridVisible) return; // honour Display.GridVisible (mirrors native)
  const G = 2000;
  let halfW = (vw / 2) / pxPerM, halfH = (vh / 2) / pxPerM;
  halfW = Math.max(halfW, 180); halfH = Math.max(halfH, 180); // see far enough under tilt
  if (mapRotation !== 0) { const d = Math.hypot(halfW, halfH); halfW = d; halfH = d; } // cover rotated corners
  const minE = Math.max(camE - halfW, -G), maxE = Math.min(camE + halfW, G);
  const minN = Math.max(camN - halfH, -G), maxN = Math.min(camN + halfH, G);
  if (minE >= maxE || minN >= maxN) return;
  const toolW = toolWidthM();
  // Spacing = tool-width × NiceStep125(viewSpan / (toolW·30)) — same as native.
  const spacing = toolW * niceStep125(Math.max(vw, vh) / pxPerM / (toolW * 30));
  const major = k => k % 10 === 0 ? SKP.gridMajor : SKP.gridMinor;
  const gm = 6; // gridMult = BaseStrokeMult 3 × GridExtraStrokeMult 2

  // Draw in WORLD coords under the perspective matrix so Skia GPU-clips at the near
  // plane. (Per-vertex screen projection garbles any line crossing behind a tilted
  // camera.) Stroke widths in world metres (native: 0.3 px floor + 0.05 m floor).
  const wpp = 1 / pxPerM;
  SKP.gridMinor.setStrokeWidth(Math.max(0.3 * wpp, 0.05) * gm);
  SKP.gridMajor.setStrokeWidth(Math.max(0.6 * wpp, 0.1) * gm);
  SKP.axisX.setStrokeWidth(Math.max(0.9 * wpp, 0.15) * gm);
  SKP.axisY.setStrokeWidth(Math.max(0.9 * wpp, 0.15) * gm);
  canvas.save();
  canvas.concat(perspM);
  const line = (e1, n1, e2, n2, paint) => {
    const c = clipNear(e1, n1, e2, n2); // returns WORLD coords
    if (c) canvas.drawLine(c[0] - camE, c[1] - camN, c[2] - camE, c[3] - camN, paint); // camera-relative
  };
  for (let k = Math.ceil(minE / spacing); k * spacing <= maxE; k++)
    line(k * spacing, minN, k * spacing, maxN, major(k));
  for (let k = Math.ceil(minN / spacing); k * spacing <= maxN; k++)
    line(minE, k * spacing, maxE, k * spacing, major(k));
  if (0 >= minE && 0 <= maxE) line(0, minN, 0, maxN, SKP.axisY);
  if (0 >= minN && 0 <= maxN) line(minE, 0, maxE, 0, SKP.axisX);
  canvas.restore();
}
// Implement hitch line: yellow line from the vehicle hitch pivot to the tool, drawn
// under the perspective path so it clips/tilts like the other vectors. Mirrors native
// DrawToolSk's hitch segment (hitch → tool).
function drawHitchSk(canvas) {
  const tool = renderTool(), p = renderPose();
  if (!tool || !p || (!tool.e && !tool.n)) return;
  const veh = config && config.vehicle, tl = config && config.tool;
  if (!veh || !tl) return;
  if (!SKP.hitch) {
    SKP.hitch = new CK.Paint();
    SKP.hitch.setStyle(CK.PaintStyle.Stroke);
    SKP.hitch.setColor(ckColor('#FFFF00'));
    SKP.hitch.setStrokeWidth(3);
    SKP.hitch.setAntiAlias(true);
    SKP.hitch.setStrokeCap(CK.StrokeCap.Round);
  }
  const type = tl.type | 0; // 0 front, 1 rear, 2 TBT, 3 trailing (ToolConfigDto)
  if (type === 3 || type === 2) {
    // Trailing / TBT: a single tongue line from the vehicle hitch pivot to the tool.
    if (tick && tick.hitchE != null && (tick.hitchE || tick.hitchN))
      strokePtsSk(canvas, [{ e: tick.hitchE, n: tick.hitchN }, { e: tool.e, n: tool.n }], false, SKP.hitch);
    return;
  }
  // Mounted (front/rear 3-point): two arms from the tractor mount (rear axle, or
  // wheelbase ahead for front-mount) ± a lateral spread, converging on the tool.
  // Mirrors native: spread = trackWidth*0.3; base = pivot (+wheelbase ahead if front).
  const spread = veh.trackWidth * 0.3;
  const front = type === 0;
  const baseE = p.e + (front ? Math.sin(p.heading) * veh.wheelbase : 0);
  const baseN = p.n + (front ? Math.cos(p.heading) * veh.wheelbase : 0);
  const ps = Math.cos(p.heading), pc = -Math.sin(p.heading); // perpendicular (= native cosV,sinV)
  strokePtsSk(canvas, [{ e: baseE - ps * spread, n: baseN - pc * spread }, { e: tool.e, n: tool.n }], false, SKP.hitch);
  strokePtsSk(canvas, [{ e: baseE + ps * spread, n: baseN + pc * spread }, { e: tool.e, n: tool.n }], false, SKP.hitch);
}
// Vehicle: the TractorAoG sprite drawn world-sized on the ground (scales with zoom,
// foreshortens under tilt), sized from track-width/wheelbase via the same normalized
// sprite proportions as native (BitmapTractorSize). Falls back to the screen-space
// triangle until the image/config is ready. Native sequence: translate→rotate(-heading)
// →flip Y→draw bitmap into the rear-axle-anchored rect.
const SPR_REAR = 0.245, SPR_FRONT = 0.75, SPR_HALFX = 0.245; // bitmap norm anchors (native)
// Vector line weights: native uses world-metre stroke widths × strokeMult (3) drawn in
// world space, so they scale with zoom (no per-pixel factor — that's only the grid). We
// stroke in screen space, so set px = worldMetres × pxPerM each frame (min 1 px so lines
// don't vanish when zoomed far out). Values = native SkiaMapControl widths × 3.
function updateLineWidths() {
  // World-metre widths scaled by zoom, but CAPPED at MAXW px so a line can't balloon when
  // zoomed in and swallow the implement/vehicle (issue #38: a 3 m boundary line covered a
  // 4 m tool). Min 1 px so it stays visible zoomed out.
  const z = pxPerM, MAXW = 3.5, w = (m) => Math.min(Math.max(m * z, 1), MAXW);
  SKP.boundary.setStrokeWidth(w(3.0));   // boundaryOuter 1 × 3
  SKP.boundaryInner.setStrokeWidth(w(3.0)); // boundaryInner 1 × 3
  SKP.headland.setStrokeWidth(w(3.0));   // headland 1 × 3
  SKP.guidance.setStrokeWidth(w(1.5));   // trackActive 0.5 × 3
  SKP.reference.setStrokeWidth(w(0.9));  // trackBaseDash 0.3 × 3
  SKP.next.setStrokeWidth(w(1.2));       // trackNext 0.4 × 3
  SKP.uturn.setStrokeWidth(w(3.0));      // youTurn 1 × 3
  SKP.track.setStrokeWidth(w(1.5));      // saved tracks ~ active weight
  SKP.extraGuide.setStrokeWidth(w(0.9)); // extra guide 0.3 × 3
  SKP.extraGuideShadow.setStrokeWidth(w(1.2));
  SKP.routeSwath.setStrokeWidth(w(0.5));
  SKP.routeTurn.setStrokeWidth(w(0.5));
  SKP.routeHeadland.setStrokeWidth(w(0.7));
  SKP.routeApproach.setStrokeWidth(w(0.4));
  SKP.routeSplit.setStrokeWidth(w(0.8));
  SKP.angleRef.setStrokeWidth(w(0.5));
}
function vehicleSk(canvas, p) {
  const veh = config && config.vehicle;
  if (tractorReady && veh && veh.trackWidth > 0.01 && veh.wheelbase > 0.01) {
    if (!skTractor) skTractor = CK.MakeImageFromCanvasImageSource(tractorImg);
    if (skTractor) {
      const bW = veh.trackWidth / (2 * SPR_HALFX);
      const bH = veh.wheelbase / (SPR_FRONT - SPR_REAR);
      const half = bW / 2, top = (1 - SPR_REAR) * bH, bot = -SPR_REAR * bH;
      canvas.save();
      canvas.concat(perspM);
      canvas.translate(p.e - camE, p.n - camN); // camera-relative (f64) — see buildScreenMatrix
      canvas.rotate(-p.heading * 180 / Math.PI, 0, 0); // vehicle frame: +Y forward, +X right (matches native)
      // Body sprite (scale 1,-1 = bitmap rows top-down → world N up).
      canvas.save();
      canvas.scale(1, -1);
      canvas.drawImageRectOptions(skTractor,
        CK.LTRBRect(0, 0, skTractor.width(), skTractor.height()),
        CK.LTRBRect(-half, -top, half, -bot),
        CK.FilterMode.Linear, CK.MipmapMode.None, null);
      canvas.restore();
      // Steerable front wheels: one sprite drawn at both front-axle ends, rotated by the
      // live wheel angle. Mirrors native DrawVehicleSk (translate ±trackWidth/2, wheelbase
      // −0.05; rotate −steer; scale 1,-1; centred dst). Tire sizing = native constants.
      if (frontWheelReady && (skFrontWheel || (skFrontWheel = CK.MakeImageFromCanvasImageSource(frontWheelImg)))) {
        const steerDeg = -(tick ? tick.vehicleSteerAngle : 0); // tick angle already in degrees, +right
        const woX = veh.trackWidth / 2, woY = veh.wheelbase - 0.05;
        const ww = 0.378 / 0.27, wh = 0.85 / 0.29; // FrontTireWidth/ContentW, Diameter/ContentH
        const wsrc = CK.LTRBRect(0, 0, skFrontWheel.width(), skFrontWheel.height());
        const wdst = CK.LTRBRect(-ww / 2, -wh / 2, ww / 2, wh / 2);
        for (const sx of [1, -1]) {
          canvas.save();
          canvas.translate(sx * woX, woY);
          canvas.rotate(steerDeg, 0, 0);
          canvas.scale(1, -1);
          canvas.drawImageRectOptions(skFrontWheel, wsrc, wdst, CK.FilterMode.Linear, CK.MipmapMode.None, null);
          canvas.restore();
        }
      }
      canvas.restore();
      return;
    }
  }
  const xy = w2s(p.e, p.n);
  canvas.save();
  canvas.translate(xy[0], xy[1]);
  canvas.rotate(screenHeading(p.e, p.n, p.heading) * 180 / Math.PI, 0, 0); // degrees; ground-aligned under tilt
  if (skTri) canvas.drawPath(skTri, SKP.vehicle);
  canvas.restore();
}
// Background imagery as an SkImage — decoded once from the loaded <img> and
// cached, re-decoded only when the imagery version changes (same trigger as the
// 2D path's <img> swap). drawImageRectOptions with Linear filtering = the 2D
// path's imageSmoothingEnabled=true.
// Tiled ground texture: a repeating shader over a camera-centred world rect, drawn under
// the perspective matrix so it foreshortens with tilt. One tile = 50 m, matching native
// SkiaMapControl.DrawGroundTextureSk. CanvasKit's makeShaderOptions localMatrix maps
// texel→local(world) (the inverse of native SkShader.CreateBitmap), so W texels span 50 m
// when the scale is 50/W. Shader + paint are built once (world-anchored — no per-frame
// rebuild; the tiles stay fixed in the world and scroll under the vehicle).
function drawGroundTextureSk(canvas) {
  const disp = config && config.display;
  if (!disp || !disp.fieldTextureVisible) return;
  if (mapIsDay ? !groundReadyDay : !groundReadyNight) return;
  let skGround = mapIsDay ? skGroundDay : skGroundNight;
  if (!skGround) {
    skGround = CK.MakeImageFromCanvasImageSource(mapIsDay ? groundImgDay : groundImgNight);
    if (!skGround) return;
    if (mapIsDay) skGroundDay = skGround; else skGroundNight = skGround;
    if (!SKP.ground) { SKP.ground = new CK.Paint(); SKP.ground.setAntiAlias(false); }
  }
  const W = skGround.width(), H = skGround.height();
  // We render camera-relative, but the tiles must stay anchored to WORLD. The pattern
  // repeats every 50 m, so only camE,camN MOD 50 affects alignment — using the remainder
  // keeps the shader's local-matrix translation small (f32-safe → no shimmer). texel =
  // (W/50)(P + off) ⇒ texel→local matrix is scale(50/W) then translate(-off).
  const offE = camE - Math.floor(camE / 50) * 50;
  const offN = camN - Math.floor(camN / 50) * 50;
  const lm = [50 / W, 0, -offE, 0, 50 / H, -offN, 0, 0, 1];
  if (SKP.groundShader) SKP.groundShader.delete(); // free last frame's shader (already flushed)
  SKP.groundShader = skGround.makeShaderOptions(
    CK.TileMode.Repeat, CK.TileMode.Repeat, CK.FilterMode.Linear, CK.MipmapMode.Linear, lm);
  SKP.ground.setShader(SKP.groundShader);
  const half = Math.max(Math.max(vw, vh) / pxPerM, 300); // cover the view; see far under tilt
  canvas.save();
  canvas.concat(perspM);
  canvas.drawRect(CK.LTRBRect(-half, -half, half, half), SKP.ground); // camera-relative
  canvas.restore();
}
let skImagery = null, skImageryVer = null, skImageryMult = -1;
function drawImagerySk(canvas) {
  if (!imageryImg || !imageryRect) return;
  // Quality button: scale the imagery LOD by DisplayResolutionMultiplier so the background
  // degrades with quality like native's Apple composite path (which bakes imagery into the
  // multiplier-sized coverage bitmap). Ultra = full res; lower quality = downsampled (also a
  // real GPU/memory win on the tablet). Downsample via a 2D canvas (reliable browser scaling).
  const mult = (config && config.display && config.display.resolutionMultiplier) || 1;
  if (skImageryVer !== imageryVer || skImageryMult !== mult || !skImagery) {
    if (skImagery) skImagery.delete();
    let src = imageryImg;
    if (mult > 1.01 && imageryImg.naturalWidth > 0) {
      const w = Math.max(1, Math.round(imageryImg.naturalWidth / mult));
      const h = Math.max(1, Math.round(imageryImg.naturalHeight / mult));
      const cv = document.createElement('canvas');
      cv.width = w; cv.height = h;
      const ctx = cv.getContext('2d');
      ctx.imageSmoothingEnabled = true;
      ctx.drawImage(imageryImg, 0, 0, w, h);
      src = cv;
    }
    skImagery = CK.MakeImageFromCanvasImageSource(src);
    skImageryVer = imageryVer;
    skImageryMult = mult;
  }
  if (!skImagery) return;
  const r = imageryRect;
  drawImageWorldSk(canvas, skImagery, r.minE, r.minN, r.maxE, r.maxN, CK.FilterMode.Linear);
}
// Draw a north-up image over a world rect under the perspective matrix. The image
// (top row = high northing) is placed via a px→world affine, then perspM warps it
// — Skia does perspective-correct sampling AND near-plane clipping on the GPU, so
// it stays correct even when part of the rect falls behind the tilted camera.
function drawImageWorldSk(canvas, img, minE, minN, maxE, maxN, filter, mipMode) {
  const w = img.width(), h = img.height();
  // Map image px → CAMERA-RELATIVE world (minE-camE …): the world−camera offset is done
  // here in f64 so only small coords reach the f32 matrix — no floating-origin jitter on
  // imagery/coverage. See buildScreenMatrix.
  const imgToWorld = [(maxE - minE) / w, 0, minE - camE, 0, -(maxN - minN) / h, maxN - camN, 0, 0, 1];
  // Photo layers (imagery / satellite tiles, Linear filter) recede to the horizon
  // under perspective → heavy minification → shimmer/crawl under motion without a
  // mip chain. Trilinear mipmaps fix it (matches native's mipmapped imagery). Callers
  // may override the mip mode: coverage uses Linear sampling but mip-free, so its edges
  // smooth (bilinear) without paying the per-update mip-regen on a multi-MP texture.
  const mip = (mipMode !== undefined) ? mipMode
    : ((filter === CK.FilterMode.Linear) ? CK.MipmapMode.Linear : CK.MipmapMode.None);
  canvas.save();
  canvas.concat(perspM);
  canvas.concat(imgToWorld);
  canvas.drawImageOptions(img, 0, 0, filter, mip, null);
  canvas.restore();
}
// ---- satellite tile underlay (Phase MT — Draw boundary on map) ----
// A slippy-map of keyless Bing aerial tiles drawn world-positioned under the field, so
// the existing pan/zoom frames it and the s2w tap primitive draws the boundary on it.
// Tiles are proxied through the host (/sattile/<quadkey>) to avoid CORS taint. Flat-earth
// E/N↔lat/lon around the field origin is sub-metre accurate at field scale (the saved
// imagery uses the host's precise LocalPlane); good enough to draw against.
const MPD_LAT = 111320; // metres per degree latitude (flat-earth)
function mpdLon() { return MPD_LAT * Math.cos(originLat * Math.PI / 180); }
function enToLon(e) { return originLon + e / mpdLon(); }
function enToLat(n) { return originLat + n / MPD_LAT; }
function lonToE(lon) { return (lon - originLon) * mpdLon(); }
function latToN(lat) { return (lat - originLat) * MPD_LAT; }
function lonToTileX(lon, z) { return Math.floor((lon + 180) / 360 * (1 << z)); }
function latToTileY(lat, z) {
  const r = lat * Math.PI / 180;
  return Math.floor((1 - Math.log(Math.tan(r) + 1 / Math.cos(r)) / Math.PI) / 2 * (1 << z));
}
function tileXToLon(x, z) { return x / (1 << z) * 360 - 180; }
function tileYToLat(y, z) {
  const n = Math.PI - 2 * Math.PI * y / (1 << z);
  return 180 / Math.PI * Math.atan(0.5 * (Math.exp(n) - Math.exp(-n)));
}
function tileQuadkey(x, y, z) {
  let qk = '';
  for (let i = z; i > 0; i--) {
    let d = 0; const m = 1 << (i - 1);
    if (x & m) d += 1;
    if (y & m) d += 2;
    qk += d;
  }
  return qk;
}
// Pick the Bing zoom whose ground resolution ≈ the screen resolution (1 tile px ≈ 1 css px).
function satZoom() {
  const z = Math.log2(156543.03392 * Math.cos(originLat * Math.PI / 180) * pxPerM);
  return Math.max(2, Math.min(20, Math.round(z)));
}
const satTiles = new Map(); // "z/x/y" → { img, skImg }
function getSatTile(x, y, z) {
  const key = z + '/' + x + '/' + y;
  let t = satTiles.get(key);
  if (!t) {
    t = { img: null, skImg: null };
    satTiles.set(key, t);
    const im = new Image();
    im.onload = () => { t.img = im; };
    im.src = '/sattile/' + tileQuadkey(x, y, z);
    if (satTiles.size > 500) { const k = satTiles.keys().next().value; const ev = satTiles.get(k); if (ev && ev.skImg) ev.skImg.delete(); satTiles.delete(k); }
  }
  if (t.img && !t.skImg) t.skImg = CK.MakeImageFromCanvasImageSource(t.img);
  return t;
}
function drawSatelliteSk(canvas) {
  if (!satEnabled || (!originLat && !originLon)) return;
  const z = satZoom();
  let halfW = (vw / 2) / pxPerM, halfH = (vh / 2) / pxPerM;
  halfW = Math.max(halfW, 50); halfH = Math.max(halfH, 50);
  if (mapRotation !== 0) { const d = Math.hypot(halfW, halfH); halfW = d; halfH = d; }
  // Visible E/N → lat/lon corners → tile range.
  const lonW = enToLon(camE - halfW), lonE = enToLon(camE + halfW);
  const latN = enToLat(camN + halfH), latS = enToLat(camN - halfH);
  const xMin = lonToTileX(lonW, z), xMax = lonToTileX(lonE, z);
  const yMin = latToTileY(latN, z), yMax = latToTileY(latS, z); // y grows southward
  if ((xMax - xMin) > 40 || (yMax - yMin) > 40 || xMax < xMin || yMax < yMin) return; // sanity
  for (let tx = xMin; tx <= xMax; tx++) for (let ty = yMin; ty <= yMax; ty++) {
    const t = getSatTile(tx, ty, z);
    if (!t.skImg) continue;
    const eW = lonToE(tileXToLon(tx, z)), eE = lonToE(tileXToLon(tx + 1, z));
    const nN = latToN(tileYToLat(ty, z)), nS = latToN(tileYToLat(ty + 1, z));
    drawImageWorldSk(canvas, t.skImg, eW, nS, eE, nN, CK.FilterMode.Linear);
  }
}
// Coverage offscreen → SkImage, re-snapshotted only when the cell grid changed
// (cov.dirty). The world blit uses Linear (bilinear) sampling so the cell-grid edge
// reads smooth instead of stair-stepped; see the blit call below for the mip-free
// rationale. The snapshot lives on the cov object so a new coverage-init naturally
// starts with a fresh (null) image.
// Fallback-path throttle (2D canvas → full re-upload): cap the expensive whole-texture
// rebuild at ~4 Hz. The GPU render-target path below has no such cost.
const COV_REBUILD_MS = 250;
// Build a fresh coverage grid for a CoverageInit. Primary: a persistent GPU render target (new
// cells drawn straight onto it, snapshotted via texture copy-on-write — no whole-texture
// re-upload). Fallback: an offscreen 2D canvas + throttled re-upload if the target can't be made.
function makeCovGrid(init) {
  const w = init.width, h = init.height;
  let surface = null;
  if (CK && grCtx) { try { surface = CK.MakeRenderTarget(grCtx, w, h); } catch (e) { surface = null; } }
  let canvas = null, cctx = null, covPaint = null;
  if (surface) {
    surface.getCanvas().clear(CK.TRANSPARENT);
    covPaint = new CK.Paint(); covPaint.setStyle(CK.PaintStyle.Fill); covPaint.setAntiAlias(false);
    // Src (replace), not SrcOver: cells carry a coverage-fraction alpha (edge cells < 1), and a
    // cell is re-emitted with rising alpha as it fills — replace sets the exact value each time;
    // SrcOver would blend re-draws and creep edges toward opaque.
    covPaint.setBlendMode(CK.BlendMode.Src);
  } else {
    canvas = document.createElement('canvas'); canvas.width = w; canvas.height = h; cctx = canvas.getContext('2d');
  }
  return {
    cellSize: init.cellSize, originE: init.originE, originN: init.originN,
    width: w, height: h, surface, covPaint, pending: [],
    canvas, cctx, dirty: true, skImg: null, lastBuild: 0,
  };
}

// Re-anchor existing coverage into a grown grid (bounds expansion, SAME cell size). The old
// painted surface is copied into the new, larger surface at the integer cell offset between the
// grids — exact, because the host shifts the grid by a whole number of cells — accounting for the
// render y-flip (surface row 0 = highest northing). Not-yet-rasterized delta batches are shifted
// into the new coords too. Nothing is re-sent by the host; the newly-added ground is empty.
function reanchorCoverage(init) {
  const cs = init.cellSize;
  const oldCov = cov;
  const offX = Math.round((oldCov.originE - init.originE) / cs);
  const offY = Math.round((oldCov.originN - init.originN) / cs);
  const nc = makeCovGrid(init);
  const destX = offX;
  const destY = (nc.height - oldCov.height) - offY; // y-flip: old image sits (maxN growth) below the top

  if (oldCov.surface && nc.surface) {
    oldCov.surface.flush();
    const img = oldCov.surface.makeImageSnapshot();
    // Nearest + integer offset = exact pixel copy (no resample).
    nc.surface.getCanvas().drawImageOptions(img, destX, destY, CK.FilterMode.Nearest, CK.MipmapMode.None, null);
    img.delete();
    nc.surface.flush();
  } else if (oldCov.canvas && nc.canvas) {
    nc.cctx.drawImage(oldCov.canvas, destX, destY);
  }
  // else: render-target mode changed across the grow (rare) — old coverage isn't carried; deltas
  // repaint going forward and the next full reload/reconnect reseeds. No crash, no misplacement.
  nc.dirty = true;

  // Carry queued (not-yet-drained) delta batches, shifted into the new grid coords.
  for (const b of oldCov.pending) {
    const c = b.cells;
    for (let i = 0; i + 2 < c.length; i += 3) { c[i] += offX; c[i + 1] += offY; }
    nc.pending.push(b);
  }

  if (oldCov.skImg) oldCov.skImg.delete();
  if (oldCov.surface) oldCov.surface.delete();
  if (oldCov.covPaint) oldCov.covPaint.delete();
  cov = nc;
}

function drawCoverageSk(canvas) {
  if (!cov) return;
  // Only rasterize batches that have aged past RENDER_DELAY, so coverage lands on the same
  // playback timeline as the render-delayed tool (see onCoverageCells). pending is FIFO in
  // arrival time, so the first not-yet-aged batch ends the drain.
  const cutoff = performance.now() - RENDER_DELAY;
  if (cov.surface) {
    // GPU render target: draw only the aged pending NEW cells onto it, then snapshot (a GPU
    // texture COW — cheap). No whole-texture upload, so no regular per-rebuild stutter.
    if (cov.pending.length && cov.pending[0].t <= cutoff) {
      const sk = cov.surface.getCanvas(), paint = cov.covPaint, H = cov.height;
      let lastRgb = -1, drained = 0, budget = COV_DRAIN_BUDGET;
      for (const batch of cov.pending) {
        if (batch.t > cutoff) break;
        const c = batch.cells;
        let i = batch.off || 0; // resume a batch we ran out of budget on last frame
        for (; i + 2 < c.length && budget > 0; i += 3) {
          const x = c[i], y = c[i + 1], v = c[i + 2] >>> 0; // (a<<24)|(r<<16)|(g<<8)|b
          if (v !== lastRgb) { paint.setColor(CK.Color((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF, ((v >>> 24) & 0xFF) / 255)); lastRgb = v; covEdgeRgb = v & 0xFFFFFF; }
          sk.drawRect(CK.XYWHRect(x, H - 1 - y, 1, 1), paint); // flip: high northing at top
          covCells++; budget--;
        }
        if (i + 2 < c.length) { batch.off = i; break; } // budget hit mid-batch; finish next frame
        drained++;
      }
      cov.pending.splice(0, drained);
      cov.surface.flush();
      cov.dirty = true;
    }
    if (cov.dirty || !cov.skImg) {
      if (cov.skImg) cov.skImg.delete();
      cov.skImg = cov.surface.makeImageSnapshot();
      cov.dirty = false;
    }
  } else {
    const nowMs = performance.now();
    // 2D fallback: drain aged batches into the offscreen canvas (same RENDER_DELAY gate).
    if (cov.pending.length && cov.pending[0].t <= cutoff) {
      const H = cov.height, cctx = cov.cctx;
      let lastRgb = -1, drained = 0, budget = COV_DRAIN_BUDGET;
      for (const batch of cov.pending) {
        if (batch.t > cutoff) break;
        const c = batch.cells;
        let i = batch.off || 0; // resume a batch we ran out of budget on last frame
        for (; i + 2 < c.length && budget > 0; i += 3) {
          const x = c[i], y = c[i + 1], v = c[i + 2] >>> 0; // (a<<24)|(r<<16)|(g<<8)|b
          if (v !== lastRgb) {
            cctx.fillStyle = 'rgba(' + ((v >> 16) & 0xFF) + ',' + ((v >> 8) & 0xFF) + ',' + (v & 0xFF) + ',' + (((v >>> 24) & 0xFF) / 255) + ')';
            lastRgb = v;
          }
          // clear+fill = replace (the 2D analogue of BlendMode.Src) so re-emitted edge
          // cells set their exact alpha instead of blending toward opaque.
          cctx.clearRect(x, H - 1 - y, 1, 1);
          cctx.fillRect(x, H - 1 - y, 1, 1); // flip: high northing at offscreen top
          covCells++; budget--;
        }
        if (i + 2 < c.length) { batch.off = i; break; } // budget hit mid-batch; finish next frame
        drained++;
      }
      cov.pending.splice(0, drained);
      cov.dirty = true;
    }
    if (!cov.skImg || (cov.dirty && nowMs - (cov.lastBuild || 0) >= COV_REBUILD_MS)) {
      if (cov.skImg) cov.skImg.delete();
      cov.skImg = CK.MakeImageFromCanvasImageSource(cov.canvas);
      cov.dirty = false;
      cov.lastBuild = nowMs;
    }
  }
  if (!cov.skImg) return;
  const cs = cov.cellSize;
  const minE = cov.originE, minN = cov.originN;
  const maxE = cov.originE + cov.width * cs, maxN = cov.originN + cov.height * cs;
  // Bilinear (Linear), mip-free: smooths the cell-grid stair-step at the worked-area edge
  // without the per-update mip-regen cost. The surface is cleared to premultiplied
  // TRANSPARENT, so the boundary fades cleanly to transparent (no dark fringe).
  drawImageWorldSk(canvas, cov.skImg, minE, minN, maxE, maxN, CK.FilterMode.Linear, CK.MipmapMode.None);
}
// Crisp worked-area edge — the server-fed vector perimeter (only segments that don't adjoin
// another pass), stroked in the coverage colour over the raster so the soft alpha-AA feather
// is replaced by a sharp boundary. Bounded by perimeter length, so cost is ~flat regardless
// of field size. Stroke width ≈ 0.6 m (covers the feather). Polylines are broken at the near
// plane per vertex (see below). Reloaded fields have no perimeter (graceful fall back to feather).
function drawCoverageEdgeSk(canvas) {
  if (EDGE_OFF || !coverageEdges || !coverageEdges.length) return;
  const p = SKP.covEdge || (SKP.covEdge = (() => {
    const q = new CK.Paint(); q.setStyle(CK.PaintStyle.Stroke); q.setAntiAlias(true);
    q.setStrokeCap(CK.StrokeCap.Round); q.setStrokeJoin(CK.StrokeJoin.Round); return q;
  })());
  const c = covEdgeRgb;
  p.setColor(CK.Color((c >> 16) & 0xFF, (c >> 8) & 0xFF, c & 0xFF, 1));
  // Thin outline: the stroke is CENTRED on the paint's true perimeter, so half of
  // it lands OUTSIDE the painted area. The old 0.6 m width bled 0.3 m of colour
  // past the real coverage on every side — a 2.9 m pass read as ~3.5 m ("paint
  // spills out wider than the tool"). The painted data itself is exact; keep the
  // outline near-hairline so what you see is what was covered.
  p.setStrokeWidth(Math.max(1.25, 0.12 * pxPerM));
  // Per-vertex near-plane gate: a vertex at/behind the near plane (homogeneous w ≤ NEAR)
  // projects through w2s to a huge/flipped coord, so a polyline crossing behind the camera
  // would shoot a stroke to infinity (seen on zoom/tilt). Break the polyline at those
  // vertices and draw each in-front run separately — no clipping math, no infinity lines.
  const NEAR = 0.02;
  const flush = (cmds) => {
    if (cmds && cmds.length >= 6) { const path = CK.Path.MakeFromCmds(cmds); if (path) { canvas.drawPath(path, p); path.delete(); } }
  };
  for (const pl of coverageEdges) {
    if (pl.length < 2) continue;
    let cmds = null;
    for (let i = 0; i < pl.length; i++) {
      if (pw(pl[i].e, pl[i].n) < NEAR) { flush(cmds); cmds = null; continue; } // behind near plane → break
      const xy = w2s(pl[i].e, pl[i].n);
      if (!cmds) cmds = [CK.MOVE_VERB, xy[0], xy[1]];
      else cmds.push(CK.LINE_VERB, xy[0], xy[1]);
    }
    flush(cmds);
  }
}
// Tool/section footprint — section bars perpendicular to the (dead-reckoned) tool
// heading, coloured by ColorCode. Same geometry as the 2D toolFootprint().
// Tool footprint: each section a filled 2 m-deep rect (coloured by ColorCode) with a
// thin black outline + a 0.05 m inter-section gap, matching native DrawToolSk. The four
// world corners come from the perpendicular (left/right) and forward (depth) directions;
// all four are projected via w2s (the tool is always near the camera, so no near-plane
// clipping is needed) and filled/stroked as a quad.
function toolFootprintSk(canvas) {
  const t = renderTool();
  if (!t || !scene || !scene.toolSections || !scene.toolSections.length) return;
  if (!t.e && !t.n) return;
  const perp = t.heading + Math.PI / 2;
  const ps = Math.sin(perp), pc = Math.cos(perp);        // perpendicular (right) dir
  const hs = Math.sin(t.heading), hc = Math.cos(t.heading); // forward (depth) dir
  const depth = 1.0, halfGap = 0.025;                    // toolDepth 2 m; 0.05 m gap
  const secs = (tick && tick.sections) || [];
  const quad = (L, R) => {
    const cmds = [];
    const corners = [[L, -depth], [R, -depth], [R, depth], [L, depth]];
    for (let k = 0; k < 4; k++) {
      const off = corners[k][0], d = corners[k][1];
      const xy = w2s(t.e + ps * off + hs * d, t.n + pc * off + hc * d);
      cmds.push(k === 0 ? CK.MOVE_VERB : CK.LINE_VERB, xy[0], xy[1]);
    }
    cmds.push(CK.CLOSE_VERB);
    return CK.Path.MakeFromCmds(cmds);
  };
  for (let i = 0; i < scene.toolSections.length; i++) {
    const span = scene.toolSections[i];
    const L = span.left + halfGap, R = span.right - halfGap;
    if (R - L < 0.01) continue;
    const path = quad(L, R);
    if (!path) continue;
    canvas.drawPath(path, SKP.sectionFill[secs[i]] || SKP.sectionFill[5]);
    canvas.drawPath(path, SKP.sectionOutline);
    path.delete();
  }
}
// Lightbar — screen-space LED strip, identical logic/geometry to the 2D
// lightbar(). The cm/label text lives in the HUD div (no CanvasKit font bundled),
// so here we draw the LEDs plus a directional arrow triangle. Drawn in CSS px
// (under renderSkia's scale(dpr)), so it must run before canvas.restore().
function lightbarSk(canvas) {
  // GuidanceBarOn is the master; SteerBarEnabled picks the mode (steer-angle error vs
  // cross-track). Mirrors the native LightBarPanel.
  const cfg = config && config.autosteer;
  if (!tick || !tick.guidanceActive || !cfg || !cfg.guidanceBarOn) return;
  const SEG = 15, W = 18, H = 16, GAP = 4;
  const mid = (SEG - 1) / 2;
  const steerMode = !!cfg.steerBarEnabled;
  let val, PER, onThresh;
  if (steerMode) {
    val = tick.steerAngleError || 0;                          // actual − commanded (deg)
    const dz = (tick.op && tick.op.autoSteer) ? 0.5 : 0.2;    // AgOpen dead-zone
    if (Math.abs(val) < dz) val = 0;
    PER = 12 / mid; onThresh = dz;                            // ±12° full deflection
  } else {
    val = tick.crossTrackError || 0; PER = 0.05; onThresh = 0.05; // + = right of line
  }
  const totalW = SEG * (W + GAP) - GAP;
  const x0 = (vw - totalW) / 2, top = 54; // below the top status bar
  const lit = Math.min(Math.round(Math.abs(val) / PER), mid);
  const onLine = Math.abs(val) < onThresh;
  const steerLeft = val > 0; // lights point the way to STEER (native convention)
  const litCol = steerLeft ? '#ff7a3d' : '#39FF6A';
  const p = SKP.lbFill;
  for (let i = 0; i < SEG; i++) {
    const idx = i - mid;
    const x = x0 + i * (W + GAP);
    let on = false, col = '#1c2230';
    if (idx === 0) { on = onLine; col = on ? '#39FF6A' : '#2a3550'; }
    else if (steerLeft ? (idx < 0 && -idx <= lit) : (idx > 0 && idx <= lit)) { on = true; col = litCol; }
    p.setColor(ckColor(on ? col : '#1c2230'));
    canvas.drawRect(CK.XYWHRect(x, top, W, H), p);
  }
  // Directional arrow under the strip: ◀ steer-left / ▶ steer-right / ● on-line.
  const cx = vw / 2, ay = top + H + 11;
  p.setColor(ckColor(onLine ? '#39FF6A' : litCol));
  if (onLine) {
    canvas.drawCircle(cx, ay, 5, p);
  } else {
    const d = steerLeft ? -1 : 1; // tip points the steer direction
    const tri = CK.Path.MakeFromCmds([
      CK.MOVE_VERB, cx + d * 8, ay,
      CK.LINE_VERB, cx - d * 6, ay - 6,
      CK.LINE_VERB, cx - d * 6, ay + 6,
      CK.CLOSE_VERB,
    ]);
    if (tri) { canvas.drawPath(tri, p); tri.delete(); }
  }
}
function renderSkia(canvas, rp) {
  canvas.clear(ckColor(mapIsDay ? MAP_CLEAR.day : MAP_CLEAR.night));
  canvas.save();
  canvas.scale(dpr, dpr); // work in CSS px so w2s + stroke widths match
  updateLineWidths(); // world-metre line weights × current zoom (matches native)
  drawGroundTextureSk(canvas); // ground backdrop (under everything)
  drawSatelliteSk(canvas); // Bing aerial underlay while drawing a boundary on map
  drawImagerySk(canvas); // imagery overlays the ground where present
  drawTerrainSk(canvas); // Terrain: recorded elevation shading (under coverage/routes)
  drawPickOutlinesSk(canvas); // pick-from-map: all mapped field outlines
  drawRoutePlanSk(canvas); // route planner: generated coverage-route preview
  drawRefillSk(canvas);    // tank-mix: where each fill runs dry along the route
  drawRouteSplitsSk(canvas); // field-split divider lines (dashed magenta)
  drawAngleRefSk(canvas); // live pass-direction preview while the planner is open
  drawMissedSk(canvas); // missed-spots tint: red under coverage, so unworked ground shows
  drawCoverageSk(canvas);
  drawCoverageEdgeSk(canvas); // crisp vector perimeter over the raster (server-fed)
  drawGridSk(canvas);
  if (scene) {
    for (let bi = 0; bi < scene.boundaries.length; bi++)
      strokePtsSk(canvas, scene.boundaries[bi], true,
        (scene.boundaryInner && scene.boundaryInner[bi]) ? SKP.boundaryInner : SKP.boundary);
    // Headland line shows only when the headland is ON — mirrors the native
    // SetHeadlandVisible gate (IsHeadlandOn). The bottom-nav headland button drives it.
    if (scene.headland && tick && tick.tools && tick.tools.headlandOn)
      strokePtsSk(canvas, scene.headland, true, SKP.headland);
    drawExtraGuidelinesSk(canvas); // faint adjacent passes (under the bold lines)
    if (scene.nextTrack) strokePtsSk(canvas, scene.nextTrack, false, SKP.next);
    if (scene.uTurnPath) strokePtsSk(canvas, scene.uTurnPath, false, SKP.uturn);
    // Purple reference (extended across the field) — drawn ONLY when the tractor is offset from
    // it (pass ≠ 0). On the reference pass it coincides with the magenta guidance, so we show
    // just the one line. Matches the host's baseTrack = nearestPass!=0?track:null intent.
    const _onRef = !tick || !tick.op || (tick.op.passNumber | 0) === 0;
    if (!_onRef) for (const tr of scene.tracks)
      strokePtsSk(canvas, extendAbLine(tr.points), false, SKP.reference);
    // Magenta current pass, extended across the field. The host's DisplayLine is only ~track
    // length (a 2-point offset AB); extendAbLine spans it full-field, so an offset pass isn't a
    // short stub. Curves are >2 points and already host-extended, so they pass through unchanged.
    if (scene.guidanceLine) strokePtsSk(canvas, extendAbLine(scene.guidanceLine), false, SKP.guidance);
    drawFlagsSk(canvas, scene.flags);
  }
  drawRecordingMarkersSk(canvas); // live recorded-path dots (independent of the Scene)
  drawBoundaryRecordingSk(canvas); // live drive-around boundary line + dots
  drawSatBoundarySk(canvas); // live boundary-on-satellite polygon being drawn
  drawAbDrawPreviewSk(canvas); // #40 — A/B (orange/blue) dots for the AB/curve being drawn
  drawCurveMarksSk(canvas); // record-curve path dots (Set Point A → B)
  drawDrawingSk(canvas); // live draw-on-map AB/curve preview (Phase MT)
  drawHeadlandDrawSk(canvas); // live headland draw preview (Field Builder stage 2)
  drawHeadlandSegEditLinesSk(canvas); // headland-editor offset lines (yellow/red) while editing
  drawTramLinesSk(canvas); // generated tram lines (orange) when tram display on / editing
  drawEditHandlesSk(canvas); // stage-4 on-map edit handles (drag points to reshape)
  drawHitchSk(canvas); // implement hitch line (under the tool footprint)
  toolFootprintSk(canvas);
  if (rp) vehicleSk(canvas, rp);
  lightbarSk(canvas); // screen-space overlay, still inside the dpr scale
  canvas.restore();
}
// The single render loop. Window rAF (not surface.requestAnimationFrame) so it
// survives surface recreation on resize and runs even before CanvasKit finishes
// loading (DOM overlays update regardless; the GL map draws once skSurface exists).
function skFrame() {
  // Client render rate over a ~500 ms window (shown on the GPS-detail card).
  const _now = performance.now();
  if (_fpsT0 === 0) _fpsT0 = _now;
  else { _fpsFrames++; const dt = _now - _fpsT0; if (dt >= 500) { fps = _fpsFrames * 1000 / dt; _fpsFrames = 0; _fpsT0 = _now; } }
  const rp = updateCamera(); // follow mode + rotation + perspM, once per frame
  maybeSaveView();           // persist tilt/zoom changes (debounced) — issue #35
  sampleCurveMark();         // drop record-curve path dots as the vehicle drives
  if (skSurface) {
    try { renderSkia(skSurface.getCanvas(), rp); skSurface.flush(); }
    catch (e) { ckStatus = 'render err: ' + (e && e.message || e); }
  }
  // DOM overlays (independent of the GL surface).
  updateLightbarText();
  updateHeadlandHud();
  renderStatusBar();
  renderSimBar();
  renderSectionBar();
  renderBottomNav();
  renderSettings();
  renderRightNav();
  renderUTurnIndicator();
  renderAbDotLabels(); // #40 — letters beside the dropped AB/curve points
  renderBlockLabels(); // split-block letters (A, B, …) at block centroids
  renderRoll();
  renderCampad();
  renderCharts();
  renderRollCorr();
  renderOffsetFix();
  requestAnimationFrame(skFrame);
}
skFrame();
