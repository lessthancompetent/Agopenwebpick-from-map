# AgOpenWeb — Unwired Controls Report (2026-08-23)

Repo: `C:\Users\OEM\.claude\sessions\Agvalonia Rework\AgOpenWeb`. All paths below are relative to that root; `app.js`/`index.html`/`transport.js` live in `Shared/AgOpenWeb.RemoteServer/wwwroot/`, `RemoteServerWiring*.cs` in `Shared/AgOpenWeb.RemoteWiring/`, `MainViewModel*.cs` in `Shared/AgOpenWeb.ViewModels/`.

---

## 1. Headline numbers

Counted from the tree today (grep, not estimates):

| Layer | Count |
|---|---|
| `<button>` elements in index.html (3,641 lines) | **613** (326 carry an `id`) |
| Distinct `data-cmd` ids in index.html | 51 |
| Distinct `data-key` config inputs (all funnel to `config.set`) | 138 |
| `transport.send(` call sites in app.js (7,394 lines) | **200** (+8 `rnSend`, +8 `swbSend`, +34 `cfgSend`) |
| Distinct command ids that leave the browser (literal sends + data-cmd + rnSend + swbSend) | **218** |
| Distinct ids handled in RemoteServerWiring.cs (153 arg-switch `case` ids + 86 `"id" => vm.XCommand` map entries) | **239** |
| `config.set` keys handled in RemoteServerWiring.Helpers.cs | 179 |
| `*Command` properties on MainViewModel | **305** — 98 referenced by the wiring, **207 never referenced** by any wiring file |
| `*Command` properties across all ViewModels (incl. ConfigurationViewModel 144, AutoSteerConfigViewModel 63) | 544 |
| Browser ids with **no** wiring case | **0** (only `diag.ping`, which WebSocketHub.cs:113 handles itself) |

The last line is the important one: **none of today's breakage is an id typo.** Every string the browser sends lands in a `case`. The dead controls come from three other places: (a) the single-seat gate dropping Tier-2 commands with no reply, (b) VM refusals and host-side dialogs that have no wire back to the browser, and (c) VM commands/wiring cases that were never created for a web surface.

**Confirmed dead or broken after adversarial verification: 13 controls + 1 root cause**, out of 46 candidates (33 refuted).

| Failure class | Findings | Severity |
|---|---|---|
| Seat gate drops the command silently **and** the client prints a success message | Rate Control / Module Setup cluster (31 send sites, ~20 buttons); Field Builder track rename | high, low |
| Host-side confirmation dialog that the web can never answer | Tracks → Delete All (+ the GPS-far-from-field safety prompt, noted in §5) | high |
| VM precondition refusal whose only output is `StatusMessage`, which is not on the wire | root cause; YouTurn on closed track; RecPath Play <5 pts; Smooth on AB line | high (root), medium, medium, low |
| Wiring case **and** HTML control both missing (VM body exists) | GPS MinFixQuality/MaxHDOP/MaxDiffAge; Draw inner boundary on map; Headland Undo; Flag by Lat/Lon | high, medium, low, low |
| VM command orphaned — no UI, no wiring, never executed | Contour record Start/Stop; Tram start-pass ± | medium, low |
| Host-side web substitute is wrong | File → Reset All Settings | low by blast radius, but false-success |

---

## 2. Safety / High — and anything that lies to the operator

Format: **control** → what you expect → what actually happens → missing link → fix.

**2.1 Rate Control + Module Setup: Send config / Send pins / Send valve tuning / Send all / Assign ID / Set subnet / Defaults / rate ±5% / Prime test / calibration start-stop-apply / Machine→Switches master mode & type / HUD tank top-up** (31 send sites: app.js:2748, 3166, 3195, 3203, 3215, 3226, 3239, 3299-3309, 3325, 3348-3376, 3504, 3523-3566, 4296)
→ You expect the module to be configured / the rate to change.
→ If this browser is an **Observer** (which a tablet or phone at `:5174` is by default, because the launcher's own WebView takes the seat first — ControlAuthority.cs:83-88, app.js:5223), the hub discards the command and the screen still says *"Module config sent."*, *"Pins sent — the module restarts if any changed."*, *"Valve tuning sent."*, *"Full setup sent to module N."*, *"ID N assigned."* (app.js:3299, 3303, 3306, 3310, 3325, plus 3226/3239).
→ Missing link: every one of these is a bare `transport.send('rate.…')` with no `iHoldControl` check (zero hits between app.js:2864 and 3608); `transport.send` is raw `ws.send` (transport.js:388). RemoteServerWiring.cs:1001-1009 marks every `rate.*` id Tier-2; WebSocketHub.cs:125-126 `if (restricted(id) && !_authority.HoldsFresh(conn)) return;` — no nack frame exists (WireCodec.cs has nothing to encode one). The backend (RemoteServerWiring.cs:157-330) is fully wired for the seat holder, so this works from the launcher and lies from any second device. The on-screen switchbox already does it right via `swbSend` (app.js:2863, self-takeover).
→ Fix: route all 31 sends through `swbSend` (self-takeover) or an `rnSend`-style guard that `flashHint('Observer — tap the role badge…')` and returns **before** `msStatus`; make `msStatus` success text conditional on `iHoldControl`; add a `.disabled` toggle on `#modulesetup/#ratecontrol/#switches` in `updateControlUi` (pattern: `.rn-gated.disabled`, index.html:507). Belt-and-braces: WebSocketHub.Dispatch sends a `dropped|id` frame back when it discards a restricted command.

**2.2 Field Builder → Tracks → Delete All** (`#fb-trk-deleteall`, index.html:3055 → app.js:3640-3643 → RemoteServerWiring.cs:947)
→ You expect all tracks deleted after the browser confirm.
→ Nothing is deleted, saved, logged or reported. The seat holder's command reaches `DeleteAllTracksCommand` (MainViewModel.Commands.Track.cs:321-342), which calls `ShowConfirmationDialog` (MainViewModel.cs:2995-3007) — a **second**, host-side confirm. The actual `SavedTracks.Clear()/SaveTracksToFile()/DeleteRecFile` lives inside that callback (:331-341), fired only by `ConfirmConfirmationDialogCommand` (Commands.Boundary.cs:577-592), which no wiring id, projector or JS can reach (zero hits for `ConfirmConfirmationDialog`/`DialogType.Confirmation` in RemoteWiring + RemoteServer). Side effect: `State.UI.ActiveDialog` stays parked on Confirmation, which makes `HandleHotkey` bail (Hotkeys.cs:105).
→ Missing link: the wiring maps to the dialog-bound command instead of a `*Remote()` body, unlike its siblings `headland.deleteAll → RemoteDeleteAllHeadland` (Wiring :539, Headland.Remote.cs:162) and `flag.deleteAll → DeleteAllFlagsRemote` (Wiring :682, MainViewModel.cs:2535).
→ Fix: add `vm.DeleteAllTracksRemote()` (= the lambda at Track.cs:331-341 without the dialog), route `case "track.deleteAll": vm.DeleteAllTracksRemote(); return;` in the arg switch next to `track.deleteAt` (:485), remove the :947 mapping. This is the only wiring-reachable `ShowConfirmationDialog` caller (the field/job deletes already pass through `EnsureRemoteStartWorkSession`'s `confirm: (_, action) => action()` at Commands.Fields.cs:975-991).

**2.3 GPS → Minimum Fix Quality / Max HDOP / Max Differential Age** (no HTML control; VM `EditMinFixQualityCommand/EditMaxHdopCommand/EditMaxDiffAgeCommand` ConfigurationViewModel.cs:985-989, 1453-1481)
→ You expect to be able to loosen/tighten the fix-acceptance gate before a job.
→ You cannot. The gate is live — GpsPipelineService.cs:480-484 → GpsFixQualityValidator.cs:34-51 rejects fixes below MinFixQuality 4 / above HDOP 2.0 / diff-age 5.0 s — but there is no input (index.html:1796-1814 is the whole GPS panel; app.js:5655/5818 only *display* `gc-hdop/gc-fix`), no `gps.*` case in ApplyConfigSetCore (Helpers.cs:114-124), and ConfigurationService.cs:546-560 / 618-632 omit the three fields so they are not even persisted from a hand-edited file. Effectively hard-coded defaults; a RTK-float day with HDOP 2.1 drops every fix with no knob to turn.
→ Fix: three cases in Helpers.cs ~:124 (`gps.minFixQuality` clamp 1-5, `gps.maxHdop`, `gps.maxDiffAge`); project into the GPS ConfigDto (Contracts.cs ~396, SceneProjector.cs ~675 + hash ~769-773, WireCodec.cs ~225, transport.js reader); three `cfg-num` inputs under the RTK block (index.html ~1813); add to the AppSettings sync in ConfigurationService.cs.

**2.4 Root cause: `MainViewModel.StatusMessage` never reaches the browser** (MainViewModel.cs:1034-1038)
→ Every VM refusal of the form `StatusMessage = "…"; return;` is invisible on the web. The setter is a plain `SetProperty`; no `PropertyChanged` subscriber in RemoteWiring/RemoteServer; no field in StatusDto/TickDto/SceneDto/AppInfoDto (Contracts.cs:35, 80, 170-265, 466); `WireCodec.EncodeStatus` (:532) has nothing to encode; not logged so the App-Info log viewer (SceneProjector.cs:595-600) never sees it; app.js:2316-2319 documents the gap and hand-rolls a client precheck for A+ only. Aggravating: `StartHelloTimerAsync` (MainViewModel.cs:972-1026, 1325) rewrites StatusMessage every 100 ms while UDP is connected, so even a polled projection would miss it.
→ Fix: push channel, not polled field — hook `vm.PropertyChanged` for `nameof(StatusMessage)` in RemoteServerWiring → new `WireCodec.EncodeHint(text)` frame → transport.js decode → existing `flashHint()` in app.js; and stop the hello timer from clobbering the text (write only on change, or move module-count to its own property). This one change gives feedback to 2.2, 3.1, 3.2, 3.4 and the "works but silent in one state" caveats in §4.

**2.5 File → Reset All Settings** (`#fm-reset` index.html:2512 → app.js:4781 showConfirm → `app.resetSettings` → RemoteServerWiring.cs:881-883 → Helpers.cs:542-545)
→ You expect settings back to defaults.
→ Nothing changes. `ISettingsService.ResetToDefaults()` (SettingsService.cs:194-197) only does `Settings = new AppSettings()` in memory; the very next line `configService.LoadAppSettings()` (ConfigurationService.cs:454-458) re-reads `settings.json` from disk (SettingsService.cs:67-102) and pushes the **old** values back into the store. No status, no frame change; you confirmed a dialog and got nothing. The VM's own path (Commands.Settings.cs:69-88) calls `_settingsService.Save()` between the two calls; the web substitute skipped it.
→ Fix (3 lines): `ss.ResetToDefaults(); ss.Save(); configService.LoadAppSettings();` in Helpers.cs:542-545, plus a hint via 2.4's channel.

---

## 3. Medium / Low — by panel

| # | Panel / control | What happens | Missing link | Fix | Sev |
|---|---|---|---|---|---|
| 3.1 | **Right-nav YouTurn** on a closed (polygon / Boundary Curve) track — `#rn-youturn` index.html:3369 → app.js:5278 `rnSend('youturn.toggle')` → Wiring :901 | Tap does nothing; icon stays `YouTurnNo.png`. VM bails at Commands.Track.cs:1068-1073 with StatusMessage only; `IsYouTurnEnabled` was already forced false at MainViewModel.cs:2366-2367. | The data is **already on the wire** — Contracts.cs:111 `IsActiveTrackClosed`, WireCodec.cs:503, SceneProjector.cs:281, transport.js:94 decodes `tick.op.trackClosed` — and app.js never reads it (transport.js:94 is the only hit). | In `renderRightNav` (app.js ~5948) toggle `.disabled` on `!!op.trackClosed`; in the click path `flashHint("U-turns aren't available on a closed track")` instead of sending. Same flag should hide the on-screen U-turn glyphs (app.js:1825-1834), which have the identical silent case at YouTurn.cs:178/184. | med |
| 3.2 | **Recorded Path → Play** with a short/missing path — `#rp-playbtn` index.html:3191 → app.js:2651 → Wiring :964 | `LoadRecPathForPlayback` accepts ≥2 points and shows "Loaded: N points" (RecordedPath.cs:582), but `StartDrivingRecordedPath` refuses <5 (:303) with StatusMessage only (:210). Button is enabled for any seat holder (app.js:6300). | No `PointCount`/`CanPlay` in RecordedPathDto (Contracts.cs:485-493, provider Wiring :1069-1077). | Add `PointCount` to the DTO; disable `rp-playbtn` in `renderRecPath` when <5 with a hint; `_logger.LogWarning` at :210; align the ≥2 load threshold with the ≥5 play minimum. | med |
| 3.3 | **Boundary → Draw inner boundary on map** — VM `DrawMapInnerBoundaryCommand` MainViewModel.cs:3909 / Commands.Boundary.cs:724-733 | No button, no wiring id. The web draw path `#bm-drawmap` → `boundary.fromMapPoints` (app.js:2171 → Wiring :595 → Commands.Boundary.cs:1022-1034) writes `boundary.OuterBoundary = outer` unconditionally. Inner polygons can only come from GPS drive-around, KML with multiple rings (MainViewModel.cs:4791-4796) or parametric obstacles. | Wiring :969-987 has no inner-from-points case and nothing on the web sets `PendingBoundaryType = Inner`. | Add `boundary.innerFromMapPoints` → new `vm.RemoteCreateInnerBoundaryFromMapPoints(points)` (`InnerBoundaries.Add`, `IsHard=true`, skip aerial capture); `#bm-drawinner` button; `startSatBoundary(inner)` flag in app.js:2160. | med |
| 3.4 | **Tracks → Record contour** — VM `StartContourRecordingCommand/StopContourRecordingCommand` MainViewModel.cs:4103-4104, bodies TrackManagement.cs:258-313 | No web id, no HTML, no JS. `IsRecordingContour = true` occurs only at TrackManagement.cs:268, so the GPS capture hook (GpsHandling.cs:209-212) and `Track.FromContour` save path are dead. `#rn-contour` only flips guidance mode (Commands.Track.cs:1121-1125). | Wiring has only `contour.toggle` (:898), `track.deleteContours` (:932), `track.delete` (:956). `IsRecordingContour` is not exported. | Add `contour.recordStart/recordStop` ids (already Tier-2 by the `contour.` prefix at :1003); export the flag in the status frame; start/stop control next to `#rn-contour` or in the tracks panel beside `track.recordCurve` (app.js:2091). Or delete the dead feature. | med |
| 3.5 | **AB flyout → Smooth** on a 2-pt AB line or 3-4 pt curve — index.html:3454 → app.js:1784-1793 → Wiring :931 | Button is shown for any active track (`.bn-abdep` gates only on *having* a track, app.js:5912); VM refuses at Commands.Track.cs:801-809 with StatusMessage only. Works for curves ≥5 pts. | No `IsABLine`/point-count in ToolsDto. | Hide/disable when the active track is AB or <5 pts (project a flag beside `ActiveTrackName`), or surface the refusal via 2.4. | low |
| 3.6 | **Field Builder → Tracks → Rename** from an Observer browser — `#fb-trk-rename` index.html:3053 → app.js:3645-3657 | Inline input opens, you type a name, it silently reverts. `commit` (3654) sends `track.rename|…` bare; id is Tier-2 (not in UngatedTrackIds, Helpers.cs:13-27; Wiring :478 even says "Tier-2"); hub drops it. Adjacent `fb-trk-delete` (3637) at least checks `iHoldControl`. | No guard, no hint, no `.disabled` (updateControlUi 5239-5244 only re-gates `.rn-gated`). | Guard at the top of the handler like rnSend, or add `track.rename` to `UngatedTrackIds` (it only edits a name and saves). | low |
| 3.7 | **Headland → Undo** — VM `UndoHeadlandCommand` MainViewModel.cs:3942 / Commands.Boundary.cs:379 | No button, no id, no wiring case (Wiring :909-915 maps only `headland.toggle/sectionToggle`; :511-575 handle the other 7 ids). Also `RemoteDeleteAllHeadland` (Headland.Remote.cs:162-168) never snapshots `_previousHeadlandLine`, so undo would say "Nothing to undo" after a web delete anyway. | Wiring + HTML + snapshot. | Add `headland.undo` mapping + `UngatedHeadlandIds` entry (Helpers.cs:34-38) + button; make the remote deletes snapshot before `ClearHeadland()`. Or delete Undo/Reset/TurnOff as dead code. | low |
| 3.8 | **Tram → Start pass ±** — VM `Increase/DecreaseTramStartPassCommand` MainViewModel.cs:4058-4059 / Commands.Track.cs:1262-1276 | No UI, no id; and `TramConfig.StartPass` is read only by `GenerateParallelTramLines` (TramLineService.cs:282), which nothing but tests calls — the live builder `GenerateConcentricTramLanes` never reads it. Round-tripped by TramConfigFileService.cs:49/92 for no effect. | Everything. | Delete the commands + `StartPass` + `GenerateParallelTramLines`, or re-home it as a `TramSystem` field via `tram.set|…,startPass,…` (Tram.Remote.cs:49-80) and consume it in `UpdateTramLines`. | low |
| 3.9 | **Flags → Place by Lat/Lon** — VM `PlaceFlagByLatLonCommand` Commands.Settings.cs:128-144, 561-601 | No inputs or button (index.html:3437-3439, 3627-3630 are the only flag controls); app.js emits only placeAt/delete/setColor/rename/placeHere/deleteAll; Wiring :669-699, :919 has no lat/lon case; `DialogType.FlagByLatLon` has no web renderer. | Wiring + HTML. | `flag.placeLatLon|lat,lon` case beside `flag.placeAt` (:669) setting `FlagLatitudeInput/FlagLongitudeInput` then executing the command (or a `PlaceFlagAtLatLon(lat, lon)` overload); small input card in the Flags dialog. | low |

---

## 4. Works — don't touch

Each was claimed dead and traced end-to-end as working for the seat holder. Caveats in parentheses are cosmetic and mostly fall out of fix 2.4.

1. `trk-activate` (`track.activate`) — works; it is a **deactivate** button mislabelled "Activate" because selection = activation in this VM (MainViewModel.cs:2278-2295). Rename the tooltip.
2. `bm-accept` (`boundary.accept`) — closes the menu client-side (app.js:2389-2400); every boundary edit is already persisted on change, so there is nothing to commit. `ToggleBoundaryPanelCommand` is an orphan flag, harmless.
3. All Tier-2 ids in general — the seat gate is by-design defence in depth; the operator always holds the seat on a single-browser head (WebSocketHub.cs:62, app.js:5249-5252), `RefreshIfHolder` at Dispatch:123 makes the "fresh" half unreachable for the holder, and rnSend/section bar/bottom-nav/data-t2 fallback all flash the Observer hint. Only the §2.1/§3.6 sites bypass that.
4. `youturn.manualLeft/Right`, `track.snapLeft/Right` on-screen glyphs — wired to PipelineIntents (YouTurn.cs:176-186, Commands.Track.cs:249-258). (Observer drop has no hint; closed-track case is §3.1's twin.)
5. `youturn.manualLeft/Right` refusal paths — 4 of 5 are unreachable or already logged (YouTurnCreationService.Orchestration.cs:957/997 reach the App-Info log).
6. `autosteer.toggle` — works; grey icon (app.js:5951) is the designed "no track" affordance.
7. `headland.toggle` — works; icon round-trips via tick.tools.headlandOn (app.js:5920). (No-headland refusal is silent → 2.4.)
8. `rate.primed|1` Prime test — works when parked with a momentary switch; maintained-switch refusal is disclosed in the help text (index.html:2775, 2817). (>0.5 km/h refusal not surfaced.)
9. `rate.modPush/modSubnet/modAssignId/relay*` when the module plane is down — socket uses ReuseAddress (RateControlService.cs:233-235), plane-offline is shown in `#rt-live` (app.js:3444); relay reset/renumber are pure config writes.
10. `track.snap*/nudge*/halfNudge*/resetNudge` — work; `.bn-abdep` hides them without a track (app.js:5911-5912). (Edge: `#osb-lateral` stays visible if autosteer is engaged with no track.)
11. `track.cycle` — works with ≥1 track. (`.bn-abdep` on the parent `.bn-flyrow` never matches index.html:295's `.bn-btn.hide` rule, so the row is visible with zero tracks; harmless.)
12. `track.delete / swapAB / activate` toolbar — work; dimmed for Observers (index.html:1030).
13. `autosteer.freedrive.left/right` — work in Free Drive; not greyed when Free Drive is off (native FormSteer.cs:1009-1021 greys them).
14. `rate.rateReset` — works for catalogue products; no-op when the channel name is not in the catalogue, and the picker already shows "— pick —" then.
15. `wizard.action|Name` — every rendered `data-act` resolves to a real command on its own step. (Motor StartTest with no hardware is silent; `PhysicalSwitchPromptText` not projected.)
16. `youturn.direction` — works; the mid-arc no-op is by design and the tap zone is hidden while executing.
17. `control.acquire/takeover` — fully functional; the only gap is observability (Debug.WriteLine at Wiring :1019/:1044 is stripped in Release; no ILogger in RemoteServer).
18. bottom-nav data-t2, `fb-trk-delete`, `trk-*` — the "silent gate" evidence was misattributed to app.js:4236 (nio-subnet); the real dispatcher at app.js:1791 flashes the hint.
19. `ToggleYouSkipCommand` — orphan alias; `youturn.skipToggle` (Wiring :913) does the same thing.
20. 15 headland builder commands (Clip/Extend/Shrink/Build/Clear/Reset/TurnOff…) — superseded by the segment API (`headland.wholeBoundary/setOffset/fromMapPoints/deleteAll`).
21. Tram `SwapSide/ToggleLeftManual/ToggleRightManual/Clear/ShowSettings/SetMode*` — no web control; superseded by `tram.set/add/delete/cycle`. (Note: the two **manual tram-dot** toggles have a live backend consumer — TramLineService.cs:609-610 → PGN 239 — so that is a missing feature, not dead code.)
22. `PlaceRed/Green/YellowFlag`, `PlaceFlagOnClick`, `DeleteFlag`, `DeleteAllFlags` VM commands — every web flag action uses a parallel wired id.
23. Log viewer `Clear/SetFilter/Show/Close` — web log viewer is client-side and live (app.js:4846-4860). (No Clear button exists — minor.)
24. `ShowViewSettingsDialogCommand` — web "View All Settings" renders from the config frame (app.js:4824-4841).
25. `CreateDebugDumpCommand` — superseded by `app.bugReport` (Helpers.cs:559-581); `ScreenshotProvider` is never assigned anyway.
26. `SimulatorForward/ReverseCommand` — orphan; web uses step speed (`sim.speedUp/Down/stop`). The accel flags *are* consumed (GpsSimulationService.cs:161, 260-288) if ever wired.
27. ConfigurationViewModel `EditUTurnSkipWidth/SetHeadingSource/SetGpsUpdateRate/ToggleUseRtk/TogglePolygons/ToggleSpeedometer/ToggleDirectionMarkers/ToggleSectionLines/UploadPinConfig/SendAndSaveMachineConfig` — dead VM code; `machine.sendSave` (Wiring :726-731) and `machine.pin` are the live paths.
28. AutoSteerConfigViewModel `SteerOffset5/EditSteerOffset` and other `Edit*` — replaced by `config.set autosteer.*` (Helpers.cs:225-283); `TestSteerOffset` has no reader.
29. `ResumeFieldCommand` — test-harness hook (DiagFlags `.auto_resume_field`); "Resume Last Job" uses `field.resumeLast` (Wiring :855).
30. Boundary `StartRecording/Pause/RecordInner/DrawMap/ShowOffset/ShowMapDialog/ConfirmMapDialog` — all covered by `boundary.driveAround/toggleRecording/driveAroundInner/fromMapPoints/setOffset`.
31. NTRIP `Show/Add/Edit/Delete/SetDefault/Save/Cancel/Test` VM commands — web goes via `ntrip.save/delete/setDefault/test` → `ApplyNtripCommand` (Helpers.cs:425-479), deliberately bypassing the chain-dialog VM.
32. `ScanModulesCommand / SendSubnetCommand` — `net.scan` / `net.subnet` (Wiring :809-821) call the UDP service directly.
33. `StopGuidance/UTurn/DeleteSelectedTrack/CreateALineFromPosition/ImportTracks/StartNewABLine/StartNewABCurve` — orphans with wired twins (`youturn.manualLeft`, `track.delete`, `track.aPlus`, `field.importTracks`, `track.driveAB`, `track.recordCurve`).
34. ~95 native dialog/nav/keypad/copy/sort commands — browser owns that state; results ship as data-carrying ids (`field.new`, `field.fromExisting`, `coverage.*`).
35. Camera/grid/chart commands (`Toggle2D3D`, `ZoomIn`, `ToggleGrid`, chart panels…) — client-owned; `IMapService` is `NullMapService` on every head (NullMapService.cs:29-66).

---

## 5. Prioritised fix plan (batched by root cause)

**Batch 1 — Seat-gate consistency (1 commit, highly mechanical, ~40 lines).** Fixes §2.1, §3.6 and the hint-less Observer drops noted in §4 items 4/10/18.
- app.js: add `t2Send(id)` = `rnSend` semantics (app.js:5268-5271) or reuse `swbSend` (2863); sed the 31 `transport.send('rate.` sites and `track.rename` commit (3654) to it; move the five `msStatus('… sent.')` strings into the helper's success branch.
- app.js `updateControlUi` (5239-5244): toggle `.disabled` on `#modulesetup #ratecontrol #switches .cfg-act` like `.rn-gated`.
- WebSocketHub.cs:126: replace bare `return;` with `Send(conn, WireCodec.EncodeDropped(id)); return;` + decode in transport.js → `flashHint('Not in control — command dropped')`. Also the one place to add an ILogger line for seat-drop observability (§4 item 17).

**Batch 2 — Hint channel / StatusMessage on the wire (1 commit, moderate, ~100 lines).** Fixes §2.4 and gives feedback to §2.2, §3.1, §3.2, §3.5 and every "silent in one state" caveat in §4.
- RemoteServerWiring.cs: subscribe `vm.PropertyChanged` for `nameof(MainViewModel.StatusMessage)` → `server.Broadcast(WireCodec.EncodeHint(text))`.
- MainViewModel.cs:972-1026 / 1325: only assign StatusMessage in the hello timer when the module-summary text changes (or give the module summary its own property) so pushed hints are not overwritten within 100 ms.
- WireCodec.cs: new frame tag; transport.js: decode → `onHint`; app.js: `onHint = flashHint`.
- Client-side gates that already have data: `tick.op.trackClosed` → disable `#rn-youturn` and hide `#osb-uturn` (app.js:5948, 1825-1834); `PointCount` in RecordedPathDto → disable `#rp-playbtn`.

**Batch 3 — Host-side dialogs that the web cannot answer (1 commit, mechanical, ~30 lines).** Fixes §2.2, §2.5, plus one item found while writing this report:
- `track.deleteAll` → `DeleteAllTracksRemote()` per the `EnsureRemoteStartWorkSession` precedent (Commands.Fields.cs:975-991 `confirm: (_, action) => action()`).
- `app.resetSettings`: insert `ss.Save()` (Helpers.cs:542-545).
- **Also found:** `HandleFarFromFieldWarning` (MainViewModel.OriginGuard.cs:19-35, triggered from ApplyResults.cs:256) disengages autosteer (good) and then opens a host-only `ShowConfirmationDialog("GPS far from field", … Yes = close field)`. Grep of RemoteWiring/RemoteServer/app.js for `FarFromField` returns nothing, so on the web the operator never sees the prompt and `State.UI.ActiveDialog` parks on Confirmation. Project it the way `UnsavedCoveragePrompt` is (Contracts.cs:252-255; `coverage.saveJob/discardClose/cancelClose` Wiring :862-864). Not adversarially verified to the same depth as the list above, but the trace is two greps.

**Batch 4 — Missing wiring cases + missing HTML (1-2 commits, moderate; each is a new `case` + a new button, one needs a VM method).** Fixes §2.3, §3.3, §3.4, §3.7, §3.9.
- `gps.minFixQuality / maxHdop / maxDiffAge` (Helpers.cs ~124 + DTO + 3 inputs + ConfigurationService sync) — do this first, it is the only one that changes field-day behaviour.
- `contour.recordStart/recordStop`, `headland.undo`, `flag.placeLatLon`, `boundary.innerFromMapPoints` (+ `RemoteCreateInnerBoundaryFromMapPoints`).

**Batch 5 — Dead-code decision (optional, 1 commit).** Tram start-pass (§3.8) and the 207 unreferenced MainViewModel commands in §4 items 19-35. Either delete or record them in the allowlist from §6 so they stop showing up in audits.

---

## 6. Systemic recommendation — a wiring-contract test

Today's 0-typo result shows id spelling is already safe; the bugs are in gates, feedback and missing surfaces. One NUnit fixture in `Tests/AgOpenWeb.ViewModels.Tests` (NUnit 4.5.1 is already there; `MainViewModelBuilder` already constructs a headless VM for CommandSmokeTests.cs:11) that treats app.js / index.html / RemoteServerWiring*.cs / WebSocketHub.cs as text would have caught every class above. Concretely, `WiringContractTests.cs` with a `RepoRoot` found by walking up to `AgOpenWeb.sln`:

1. **`EveryBrowserSendHasAWiringCase`** — regexes over app.js: `transport\.send\('([a-z]+\.[A-Za-z0-9_.]+)`, `rnSend\('…`, `swbSend\('…`, `wireRn\('[^']+',\s*'([^']+)'`, and over index.html: `data-cmd="([^"]+)"`. Handled set: `case "([a-z]+\.[A-Za-z0-9_.]+)"` and `^\s*"([a-z]+\.[A-Za-z0-9_.]+)" =>` in RemoteServerWiring.cs plus `case "…"` in WebSocketHub.cs (control.*, diag.ping). Computed ids (`'track.draw' + kind`, `'profile.' + op`, `'coverage.' + …`, `'wizard.' + …` — 24 wiring ids have no literal sender today) go in `WiringContract.Computed.txt` as `prefix -> id,id,id`; the test fails on any `'x.' +` concatenation not listed. Passes today; prevents the typo class forever.

2. **`EveryWiringMappingResolvesToANonNullCommand`** — for each `"id" => vm.(\w+Command)` capture, `typeof(MainViewModel).GetProperty(name)` must exist and its value on `new MainViewModelBuilder().Build()` must be non-null; for each `vm\.(\w+)\(` method call, `GetMethod` must exist. Passes today (CommandSmokeTests covers part of this), but pins it.

3. **`EveryHtmlButtonHasAHandler`** — parse `<button[^>]*>` in index.html; a button passes if it has `data-cmd/data-key/data-act/data-lvl/data-chart/data-t2`, or its `id` appears in app.js as `getElementById('id')`, `'#id'`, `wireRn('id'`, `wireCampad`… , or one of its classes is in a delegated-class allowlist (`cfg-act`, `fm-back`, `ln-closex`, `trk-tool`, `fb-act`, `sa-tgl`, `log-filt`, `chart-x`, `tl-chartbtn`, `rn-gated`, `bn-row`, `ms-tgl/.ms-num/.ms-sel`). Reports the unhandled ids.

4. **`NoBareTier2SendOutsideTheGuardHelpers`** — the regression guard for §2.1/§3.6. Reconstruct the Tier-2 predicate textually from RemoteServerWiring.cs:1001-1009 (prefixes `section. autosteer. youturn. contour. smartwas. wizard.action rate.`, `net.subnet`, `recpath.play`, `track.*` minus `UngatedTrackIds` parsed from Helpers.cs:13-27, `headland.*` minus `UngatedHeadlandIds` :34-38). Then every `transport.send('<tier2 id>` in app.js must be lexically inside the bodies of `rnSend`, `swbSend` or `t2Send` (find the enclosing `function name(` by scanning backwards and brace-matching). **Fails today with 32 hits** — which is the point; it goes green with Batch 1.

5. **`NoWiringReachableCommandOpensAHostDialog`** — for each `vm.XCommand` captured in test 2, locate `XCommand = new …Command(` in `Shared/AgOpenWeb.ViewModels/*.cs`, brace-match the lambda, and fail if the body contains `ShowConfirmationDialog(`, `ShowNumericInput(`, `ShowTextInput(` or `OpenChainDialog(`. Exempt list for dialogs that *are* projected (`DialogType.UnsavedCoverage`). **Fails today on `DeleteAllTracksCommand`**; catches the next §2.2.

6. **`EveryMainViewModelCommandIsWiredOrAllowlisted`** — every `public ICommand \w+Command` in MainViewModel*.cs must appear in a wiring file, in `Commands.Hotkeys.cs`, or in `KnownUnwiredCommands.txt` with a one-line reason (`superseded by track.delete`, `native-only, delete`, `harness hook`). Seeding the file with today's 207 is a half-hour job and turns the next orphan (§3.4, §3.8) into a red test with a name instead of an audit finding.

Two runtime complements, both one-liners: log the Tier-2 drop at WebSocketHub.cs:126 through an `ILogger` (there is none in RemoteServer today), and have `ShowConfirmationDialog` (MainViewModel.cs:2995) `LogWarning` when no platform head is attached, so a parked dialog is at least visible in the App-Info log viewer.