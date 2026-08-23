# AB / Curve / Headland generator — AgOpenGPS 6.8.6 vs AgOpenWeb: the remap

Source roots used throughout:
- **AOG** = `C:\Users\OEM\Downloads\AgOpenGPS-src-6.8.6\AgOpenGPS-21be26aa58d09b9b5abacc715d0abcf4a021f1db\SourceCode\GPS` (file:line cites are relative to this)
- **Web** = `C:\Users\OEM\.claude\sessions\Agvalonia Rework\AgOpenWeb\Shared\…` (ViewModels = `AgOpenWeb.ViewModels`, wiring = `AgOpenWeb.RemoteWiring\RemoteServerWiring.cs`, client = `AgOpenWeb.RemoteServer\wwwroot\app.js` / `index.html`)

Conventions that matter for every section: AOG headings are compass radians (0 = north, clockwise; unit vector = (sin h, cos h)). The outer fence is wound so the field interior is on the LEFT of each vertex heading (`Classes/CFenceLine.cs:157-182`); inner fences are wound the opposite way. "Move inward by d" everywhere in AOG is `p' = p + (−cos h, sin h)·d` = d metres to the left of the point heading (`FormHeadAche.cs:487-490`, `FormHeadLine.cs:656-659, 734-740`, same as `Classes/CTurn.cs:141-143`). The fence (`bndList[j].fenceLine`) is resampled by FixFenceLine to 1.1 m (<20 ha) / 2.2 m (<40 ha) / 3.3 m (larger), inner boundaries half that (`CFenceLine.cs:48-114`), and is an OPEN ring (no duplicated closing vertex).

---

## 1. HOW AGOPENGPS 6.8.6 DOES IT

There are THREE different places in 6.8.6 with "touch the boundary" tools and A++/B++ buttons, and they do not behave the same. The operator sees them as one thing; they are not:

| Menu / button | Form | What it builds | Working copy |
|---|---|---|---|
| Main screen `btnABDraw` (`Forms/Controls.Designer.cs:464-484`) | **FormABDraw** "Draw AB – Click 2 points on the Boundary to Begin" | Guidance TRACKS (AB line, Curve, Boundary Curve) | `gTemp` (copy of all tracks), committed on OK |
| Field menu "Headland" (`Forms/FormGPS.Designer.cs:1880`, `Controls.Designer.cs:876-888`) | **FormHeadLine** | The HEADLAND POLYGON itself (`bndList[0].hdLine`) by clipping slices into it | `hdLine` in place + one-level `backupList` |
| Field menu "Headland (Build)" (`FormGPS.Designer.cs:1889`, `Controls.Designer.cs:898-914`) | **FormHeadAche** | A list of independent offset SEGMENTS (`mf.hdl.tracksArr`, Headlines.txt) stitched into `hdLine` by Build | `tracksArr` (saved on every change) |

### 1a. Straight AB line from 2 points on the boundary (FormABDraw → btnMakeABLine)

Operator view: open Draw AB (refused with "Create a boundary first" if there is no boundary, and Contour is switched off first — `Controls.Designer.cs:464-484`). Tap near the fence twice. The taps do not land where you touch: each tap snaps to the nearest existing fence VERTEX. Then press the "AB" button. You get a straight line exactly through those two fence vertices, named by its bearing. Nothing is saved until you press OK.

Exact algorithm:
1. Screen→field: square GL view, `scale = width·0.903`, easting = `(x − w/2)·maxFieldDistance/scale·zoom + fieldCenterX − maxFieldDistance·sX` (northing likewise) — `FormABDraw.cs:584-619`. `maxFieldDistance` = max(E extent, N extent) of `bndList[0].fenceLine` clamped to [100, 5000] m (`OpenGL.Designer.cs:2973-2991`).
2. Tap A (`isA==true`): brute-force squared-distance search over EVERY vertex of EVERY boundary's `fenceLine` (outer and inner) → `bndSelect = j`, `start = i` (`FormABDraw.cs:625-646`). No segment projection, no distance threshold: a tap in the middle of the field still snaps to something.
3. Tap B: search ONLY `bndList[bndSelect].fenceLine` → `end` (`647-661`). B is therefore on the same ring as A. Both Make buttons become enabled (`665-666`). Sentinel for "unset" is 99999. Optional `cboxIsZoom`: the next tap zooms 10× around the tap instead of picking; the tap after that picks and zoom resets (`590-597`).
4. Paint: black 24 px dot with orange 16 px (A = `start`) and blue 16 px (B = `end`) on the chosen vertices (`797-817`). Built tracks are hidden while a touch pair is pending (`697-700`).
5. `btnMakeABLine` (`FormABDraw.cs:531-580`):
   - Ordering rule (`534-547`): `n = fenceLine.Count`. If `|start−end| ≤ n/2` (short way round does NOT cross the index seam) ensure `start > end` (swap if needed); else ensure `start < end`. So which vertex becomes A is NOT "first tap" — it is decided by index order.
   - `abHead = atan2(fence[end].E − fence[start].E, fence[end].N − fence[start].N)` wrapped to [0, 2π) (`550-553`).
   - New `CTrk`: `mode = AB`, `ptA = fence[start]`, `ptB = fence[end]` — the raw fence vertices, NOT extended, NOT offset inward (`555-567`). `name = "AB " + round(deg,1) + "°"` (`570-571`). Becomes the selected index; Make buttons disabled; `start=end=99999` (`574-579`).
   - Extension happens only at draw/use time: the form draws ±2000 m (`727-728`), and `CABLine` builds `endPtA/endPtB = ptA − 2000·dir / ptB + 2000·dir` (`Classes/CABLine.cs:82, 100-104`).
6. Where the passes fall (this is the part people miss): AOG's stored reference is a SWATH EDGE, not a swath centre. `CABLine.cs:118`: `distanceFromRefLine −= 0.5·(w−o)`; `:140-142`: driven line = `ref + (w−o)·k + 0.5·(w−o) ∓ offset + nudge`. So with the reference ON the fence, pass 0's centre is half a swath inside the fence → the implement's outer edge runs along the fence. That is why Draw AB does no inward offset of its own.

Edge cases: `start == end` gives `atan2(0,0) = 0` (a north line through the vertex). Because `ptA/ptB` sit on the fence, an operator who wants the implement edge on the fence needs nothing more; one who wants the centre on the fence nudges.

### 1b. AB curve from 2 points on the boundary — btnMakeCurve vs btnMakeBoundaryCurve

Same two taps as 1a, then either button. They are different products:

**btnMakeCurve** (`FormABDraw.cs:424-529`) — "curve along the fence between A and B":
1. Choose direction (`426-446`): if `|start−end| > n·0.5` the short arc crosses the seam → `isLoop = true`, ensure `start > end`, `limit = end`, `end = n`. Else ensure `start < end`. The curve ALWAYS follows the SHORTER arc (by vertex count, not metres) and is ALWAYS walked in INCREASING fence index = the fence's normalised winding direction. Tap order does not change travel direction.
2. `desList` = copies of `fenceLine[i]` for `i = start … end−1` (HALF-OPEN: the higher-index touched vertex is excluded; wraps through 0 when `isLoop`) (`448-464`). Raw fence vertices with their fence headings, still ON the fence.
3. New `CTrk` added immediately; `ptA = desList[0]`, `ptB = desList[last]` (`466-474`).
4. If `desList.Count > 3` (`477`):
   - `CABCurve.MakePointMinimumSpacing(ref desList, 1.6)` — midpoint insertion wherever spacing > 1.6 m, rescanning from 0 after each insert (`Classes/CABCurve.cs:1453-1475`). Shape-preserving, no smoothing.
   - `CalculateHeadings`: first = atan2 to pt[1], middle = central difference atan2(pt[i+1] − pt[i−1]), last = atan2 from pt[n−2] (`CABCurve.cs:1383-1414`).
   - Track heading = circular mean of all point headings (`FormABDraw.cs:484-494`).
   - `AddFirstLastPoints`: because a boundary exists, append 99 points at 1, 2 … 99 m beyond the last point along the LAST point's heading, and insert 99 points at 1 … 99 m before the first point backwards along the FIRST point's heading (`CABCurve.cs:1604-1652`). These straight 99 m tails are what A++/B++ later lengthen.
   - `SmoothAB` is commented out (`498`): no smoothing. `CalculateHeadings` again (`499`).
   - `name = "Cu " + round(deg,1) + "°"`, `mode = Curve`, `curvePts = desList` (`505-515`).
5. If `desList.Count ≤ 3` the `CTrk` was already added (line 466) and stays `mode = None`, name "New Track", heading 3, empty `curvePts` — an orphan that gets saved on OK (`524-526`).

**btnMakeBoundaryCurve** (`FormABDraw.cs:377-422`) — "whole fence ring as a track", needs no taps. Enabled by `timer1` only while NO track with `mode == bndCurve` exists (`823-831`): it is one-shot; delete the boundary curve to re-enable it.
- For EVERY boundary q (outer AND every inner): `bndPoints = fenceLine` verbatim (fence spacing, fence headings; no densify, no heading recompute, no tails, no smoothing). Skip if < 4 points. `ptA = bndPoints[0]`, `ptB = bndPoints[Count−2]` (leftover from an old closed-ring format). `name = "Boundary Curve"` (q=0) or `"Inner Boundary Curve q"`. `heading = 0`, `mode = bndCurve` (= 32, `Classes/CTrack.cs:12`). Last one created becomes selected.
- Guidance treats `bndCurve` specially: no 200 m end extensions (`CABCurve.cs:88-100`), no boundary-clipping extension (`:455`), drawn as a closed LineLoop (`:1159-1162`), U-turn loop flag (`CYouTurn.cs:1993`). Drawn in the form in brown stipple (`FormABDraw.cs:756, 767`).

Summary of the difference: Make Curve = short arc, densified to 1.6 m, with 99 m straight tails, mode Curve, "Cu 123.4°", one track. Boundary Curve = entire ring(s) verbatim, closed, mode bndCurve, one track per boundary, one-shot.

Both lie ON the fence; the swath-edge convention (1a step 6) puts pass 0's centre half a swath inside.

### 1c. Headland curve path (FormHeadLine / FormHeadAche)

Common to both forms:
- **Touch A then B**: same screen→field maths as FormABDraw; A = nearest fence VERTEX across all boundaries (sets `bndSelect`), B = nearest vertex on that same boundary (`FormHeadLine.cs:202-223` / `FormHeadAche.cs:231-252`). Snapping is to existing 1.1–3.3 m fence vertices, never interpolated. Zoom checkbox works as in 1a. FormHeadLine in Curve mode refuses a tap with "Distance Set to 0, Nothing to Move" when `nudSetDistance == 0` (`FormHeadLine.cs:157-162`); FormHeadAche refuses `start == end` with "Start Point = End Point" (`FormHeadAche.cs:271-276`). FormHeadAche also CLEARS `bndList[0].hdLine` and sets `hdl.idx = −1` on every tap (`:228-229`), so the built polygon vanishes until Build is pressed again.
- **Offset distance** `nudSetDistance` (user units ft/m, 1 dp, 0..200, default 0; `GUI.Designer.cs:477-493`): typed on the keypad or picked from `cboxToolWidths` "0".."10" = N × (tool.width − tool.overlap) (`FormHeadAche.cs:856-859`, `FormHeadLine.cs:943-946`). It is NOT implicitly the tool width.
- **rbtnCurve (default)** — after the second tap (`FormHeadLine.cs:243-348` / `FormHeadAche.cs:279-385`): same shorter-arc/wrap bookkeeping as 1b, copy fence vertices in INCREASING index order (here the end vertex IS included), `CalculateHeadings` if > 3 points, then extend BOTH ends straight by 29 points at 1 m steps along the end point's heading (`FormHeadLine.cs:318-334` / `FormHeadAche.cs:351-369`). `mode = Curve`. FormHeadAche additionally creates a `CHeadPath` (`Classes/CHeadLine.cs:1-34`) with `a_point = start` (fence index of A after swap), `name = idx + " Cu " + mm:ss`, `moveDistance = 0`, and saves Headlines.txt.
- **rbtnLine** — same swap normalisation; `ptA = fence[start]`, `ptB = fence[end]`; `abHead = atan2(B − A)`; points every 1 m from A to B with `heading = abHead`; same 29 m extensions each end; `mode = AB` (`FormHeadLine.cs:349-414` / `FormHeadAche.cs:386-471`). A Line is the CHORD between the two fence vertices, not the fence. FormHeadAche inserts the new `CHeadPath` at `idx+1` if `hdl.idx < Count−1` else appends; `name = idx + " AB " + hh:mm:ss`.
- **Inward move** — immediately after the second tap (FormHeadLine only when `nud != 0`, `SetLineDistance` `:416-418, 642-694`; FormHeadAche always, inline `:474-524`): `distAway = nud·ftOrMtoM`; every point (including the 29 m extensions) moves `distAway` to the LEFT of its heading (= into the field for the outer fence, away from the hole for an inner fence). Then two culls: (1) drop a moved point if its squared distance to ANY ORIGINAL point is `< distAway² − 0.01` (O(n²); removes the crossing region at concave corners so the two arms meet roughly at their true intersection), (2) drop it if within 1 m of the last kept point. Headings are copied unchanged. FormHeadAche accumulates `tracksArr[idx].moveDistance += distAway` (informational). With `distAway = 0` in HeadAche `distSqAway = −0.01`, so only the 1 m filter runs.
- **A++ / A−− / B++ / B−−** — see 1d.
- **Headland off** (`btnHeadlandOff`, SwitchOff icon, both forms): `hdLine.Clear()`, `FileSaveHeadland()` (header-only Headland.txt), `bnd.isHeadlandOn = false`, `vehicle.isHydLiftOn = false`, close (`FormHeadLine.cs:1004-1011`, `FormHeadAche.cs:861-868`). Headlines.txt segments are NOT deleted.
- **Section-in-headland checkbox** `cboxIsSectionControlled` → `bnd.isSectionControlledByHeadland` + `ToolSettings.setHeadland_isSectionControlled` (tool profile, not field).

**FormHeadLine-specific** ("Headland" menu — the edit-in-place builder):
- Load (`FormHeadLine.cs:47-108`): if `hdLine` is empty it is seeded as a verbatim copy of `fenceLine` (headland == boundary, effectively zero-width); otherwise the existing polygon is resampled to ≤1.2 m (`MakePointMinimumSpacing`) and headings recomputed. Only `bndList[0]` is ever written.
- **Bnd Loop / "Build"** (`btnBndLoop_Click`, `:705-797`; disabled while `nud == 0`, `:590`): whole-boundary headland from the OUTER fence only: every fence vertex moved `moveDist` left; cull if squared distance to ANY fence vertex `< moveDist²·0.999`; cull if ≤1 m from last kept; append a copy of the first kept point (and a second copy when count > 3); `MakePointMinimumSpacing(1.2)`; `CalculateHeadings`; `hdLine := result`; `FileSaveHeadland()`. If the offset is bigger than half the field everything is culled and it silently keeps the old `hdLine` (`:768-771`).
- **Clip Line** (`btnClipLine` → `btnSlice_Click`, `:799-914`; enabled while `sliceArr` non-empty): `backupList := hdLine`. Intersect every slice segment (i = 0..Count−3) against every headland segment (k = 0..Count−3, wrapping k+1) with `GeoLineSegment.IntersectionPoint` (`AgOpenGPS.Core/Models/Base/GeoLineSegment.cs:24-47`). First hit → `startBnd = k+1, startLine = i+1`; every later hit overwrites `endBnd = k+1, endLine = i`. Need ≥ 2 hits else "Crossings not Found". If `|startBnd − endBnd| > fenceLine.Count·0.5` (quirk: fence count, not hdLine count) → new `hdLine = hdLine[endBnd..startBnd) + slice[startLine..endLine)`; else `hdLine[0..startBnd) + slice[startLine..endLine) + hdLine[endBnd..)`. Net: the SHORTER index-arc of the current headland between the two crossings is replaced by the middle of the slice; the 29 m overhangs are discarded. `sliceArr` cleared (A/B buttons grey out).
- **Undo** (`:933-941`): `hdLine := backupList` (one level, taken at each Clip).
- **Reset** (`btnDeletePoints`, `:916-931`): `hdLine := fence copy`, touch/slice/backup cleared, nothing saved.
- **OK / btnExit** (`:593-640`): recompute each point heading as atan2(p[i−1] − p[i]) (backward-pointing quirk; nobody reads these headings), decimate keeping a point only when accumulated heading change since the last kept point > 0.005 rad, append last point, store the section-control flag, `FileSaveHeadland()` → Headland.txt (`IO/HeadlandFiles.cs:61-85`: "$Headland", then per boundary: count, then `e,n,h` 3/3/5 dp). On return `bnd.isHeadlandOn = hdLine.Count > 0` (`Controls.Designer.cs:883`).

**FormHeadAche-specific** ("Headland (Build)" menu — segment builder):
- Load (`FormHeadAche.cs:44-95`): `hdl.idx = −1`; `FileLoadHeadLines()` reads Headlines.txt ("$HeadLines"; per path: name, moveDistance, mode, a_point, count, `e , n , h` lines; paths ≤ 3 points dropped) into `tracksArr`; `bndList[0].hdLine` is CLEARED (`:56`). Close saves Headlines.txt only; OK does NOT save Headland.txt — only Build and Headland-Off do. Leaving without Build leaves the in-memory headland empty for the session (Headland.txt on disk still has the old polygon and returns on next field open).
- **Cycle forward/back** (`:131-163`): `hdl.idx ±1` wrapped; clears `hdLine`; selected segment drawn thick magenta with black/orange A and black/blue B end dots (`:616-644`). Trash (`:165-185`) removes the selected segment. Reset (`btnDeleteHeadland`, `:707-720`) clears `hdLine` only, keeps segments. Cancel Touch (`:921-933`) aborts a half-made pair.
- **Build** (`btnBndLoop_Click`, `:722-854`): sort `tracksArr` by `a_point` (fence index of each A) so segments run around the fence in winding order; save Headlines.txt; `hdLine` cleared. For each segment L: scan its segments i = 0..Count−3 against every segment of the PREVIOUS segment; on first hit record `i+1` and switch target to the NEXT segment (the k loop continues from its current k, not from 0); on the second hit record `i` and stop. Require `crossings.Count == 2·segments` else "Make sure all ends cross and only once" (or "Is there maybe only 1 line?") and `hdLine` stays empty. `hdLine` = concatenation of `trackPts[low..high)` per segment (the part between its two crossings; the 29 m overhangs drop out). Then the same backward-heading + 0.005 rad decimation as FormHeadLine's OK, but without appending the last point and without a closing duplicate. `FileSaveHeadland()`. Segments stay for later editing.

**What the headland is used for** (and what it is NOT):
- Storage: Headland.txt = the product (per boundary block); Headlines.txt = HeadAche segments only, read by nobody except FormHeadAche (`IO/HeadlandFiles.cs`, `IO/HeadlinesFiles.cs`, `SaveOpen.Designer.cs:151, 362-372`). `bnd.isHeadlandOn := hdLine.Count > 0` on field open; main-screen `btnHeadlandOnOff` toggles it and zeroes the PGN 239 hydLift byte when switched off (`Controls.Designer.cs:1935-1957`).
- Section control (only when `isSectionControlledByHeadland`): `IsPointInsideHeadArea` = inside `bndList[0].hdLine` AND not inside any inner `hdLine` (`Classes/CHead.cs:99-114`); per-section look-ahead-ON points (`CHead.cs:67-97`) + back-buffer scan between the OFF and ON look-ahead rows for the green-250 headland line (`OpenGL.Designer.cs:903-909, 1143-1191`): force OFF when required-on AND look-on points in headland AND no line in the band; force ON when look-on points in the field AND the line is in the band.
- Hydraulic lift (`OpenGL.Designer.cs:1033-1058`, `CHead.cs:15-65`): raise (PGN 239 hydLift = 2) only when BOTH outer tool corners are in the headland AND no headland pixel is within `hydLiftLookAheadDistance` ahead; lower (=1) otherwise; only when `isHydLiftOn`, speed > 0.2, not reversing; with sounds. `isHydLiftOn` requires `isHeadlandOn`.
- Headland alarm: raycast from tool pivot to `hdLine` (<20 m inside / <5 m outside, heading within 60°) (`CHead.cs:115-165`).
- **The U-turn never reads hdLine.** It uses `bndList[j].turnLine` = fence offset by `yt.uturnDistanceFromBoundary` (Config → Module → "Turn distance from boundary", `ConfigModule.Designer.cs:414,450`), built by `CTurn.BuildTurnLines` (`Classes/CTurn.cs:120-200`) on field open and boundary edits. A headland drawn in these builders changes section control, hyd lift, the alarm and the drawing — not where U-turns arm.

### 1d. The coloured A++ / A−− / B++ / B−− buttons — exactly what each does

Colour meaning everywhere: the A end is ORANGE, the B end is BLUE (touch dots `FormABDraw.cs:797-817`; selected-curve endpoints `:777-792`; HeadAche segment end dots `FormHeadAche.cs:616-644`). Icons: `APlusPlusA`, `APlusPlusB` (extend), `APlusMinusA`, `APlusMinusB` (shrink).

| Form | Button | Acts on | What it does per press | Units | Direction | Recomputes? |
|---|---|---|---|---|---|---|
| **FormABDraw** (track editor) | `btnALength` "A++" (`FormABDraw.cs:845-860`) | SELECTED track, only if `mode == Curve` (enabled by timer `:833-842`; greyed for AB and bndCurve) | Copies `curvePts[0]` and inserts 49 new points at 1, 2 … 49 m BEHIND it along `curvePts[0].heading` (`pt −= (sin h, cos h)·i`, `Insert(0)`) | 49 m, 1 m spacing, unbounded, repeatable | Straight along the tail heading — does NOT follow the fence | Nothing (ptA, heading, name untouched; inserted points inherit the heading) |
| FormABDraw | `btnBLength` "B++" (`:862-876`) | same | `ptCnt = Count−1` captured once; appends 49 points at 1 … 49 m AHEAD of that original last point along its heading | 49 m | Straight | Nothing |
| FormABDraw | A−− / B−− | **do not exist** — there is no shorten in the track editor; only Cancel (discard everything) or Delete | | | | |
| **FormHeadLine** (headland clip builder) | `btnALength` / `btnBLength` (`FormHeadLine.cs:948-979`) | `sliceArr` (the touched path AFTER inward offset, before Clip); enabled while `sliceArr` non-empty | A: insert 9 points at 1 … 9 m before the first point backwards along its heading. B: append 9 points at 1 … 9 m beyond the last point along its heading | 9 m per press, 1 m spacing, metres regardless of ft/m display | Straight, tangent to the path end (heading copied from the fence/chord before offset) | Nothing |
| FormHeadLine | `btnAShrink` / `btnBShrink` (`:981-991`) | `sliceArr` | If `Count > 8`: A removes the FIRST 5 points (`RemoveRange(0,5)`); B removes the LAST 5 points | 5 POINTS, not metres: ≈5 m on the 1 m-spaced chord/extension points, 5.5–16.5 m on fence-spaced curve points (minus any culled by the offset) | Along whatever the path is there | Nothing; silently refuses at ≤ 8 points |
| **FormHeadAche** (segment builder) | `btnALength` / `btnBLength` (`FormHeadAche.cs:870-884, 895-910`) | `tracksArr[hdl.idx].trackPts` (the SELECTED segment; needs `idx > −1`) | Same as FormHeadLine: 9 points, 1 m, straight along the end heading | 9 m | Straight | Nothing |
| FormHeadAche | `btnAShrink` / `btnBShrink` (`:886-893, 912-919`) | selected segment | Same 5-point removal when `Count > 8` | 5 points | | |

Why they exist: in the headland builders the slice/segment is deliberately overshot 29 m past each tap so it crosses the neighbouring line or the existing headland; A++/B++ lengthen that overshoot when 29 m was not enough to cross, A−−/B−− shorten it when it crosses twice (non-convex fields). In FormABDraw, A++/B++ lengthen the 99 m straight run-out tails of a curve (99 → 148 → 197 m …) so guidance/U-turn can reach the boundary on a curve that stops short.

Two things the buttons never do in AOG: they never walk along the boundary, and they never act in metres-along-fence. They are always straight, in metres (extend) or points (shrink), from the current end of the path.

### 1e. Everything else on FormBuildTracks / FormQuickAB (compact)

Data model first (`Classes/CTrack.cs:319-360`): `CTrk { curvePts List<vec3>, heading (rad, default 3), name ("New Track"), isVisible, ptA, ptB, mode (None=0, AB=2, Curve=4, bndTrackOuter=8, bndTrackInner=16, bndCurve=32, waterPivot=64), nudgeDistance }`. `endPtA/B` runtime only. Swath-edge guidance: driven line = `ref + (k+0.5)(w−o) ∓ offset + nudge` (`CABLine.cs:118-142`, `CABCurve.cs:225-241`). Persisted in `<field>/TrackLines.txt` (`IO/TrackFiles.cs:128-159`: "$TrackLines"; per track: name, heading, "ptA.e,ptA.n", "ptB.e,ptB.n", nudgeDistance, mode int, True/False, count, then `e,n,h` lines). Load resets `trk.idx = −1`.

| Button (form) | What it does | Cite |
|---|---|---|
| Main `btnBuildTracks` | Opens FormBuildTracks on panelMain; snapshots all tracks into `gTemp` for Cancel; `selectedItem = trk.idx` | `Controls.Designer.cs:417-438`, `FormBuildTracks.cs:76-150` |
| Row tap | Highlights only (hidden rows cannot be selected); nothing live changes until OK | `FBT:376-419` |
| Row green/red button | Toggles that track's `isVisible`, clears selection | `FBT:357-374` |
| `btnHideShow` | Sets EVERY track visible/hidden, alternating from "show all" | `FBT:581-591` |
| `btnMoveUp` / `btnMoveDn` | Swaps the selected track with its neighbour (order = cycle order and file order) | `FBT:420-462` |
| `btnSwapAB` | AB: swap ptA/ptB, heading += π. Curve: reverse `curvePts`, heading += π on the track AND every point, drops the first and last reversed point (off-by-one), swap ptA/ptB | `FBT:526-579` |
| `btnDuplicate` | Deep copy appended, name panel prefilled "name Copy" | `FBT:621-639` |
| `btnEditName` / `btnSaveEditName` / `btnAddTimeEdit` | Rename; blank → "No Name hh:mm:ss"; append " hh:mm:ss" | `FBT:641-654, 1577-1599` |
| `btnNewTrack` → panelChoose | 8 creators: Pivot-by-driving, Load KML, Lat/Lon, Lat/Lon+Heading, Pivot by Lat/Lon, A+, AB Line, Curve | `FBT:593-605`, `FBTD:720-995` |
| AB Line: `btnALine`, `btnBLine`, `btnEnter_AB` | A = pivot position; 500 ms timer previews A→current position (`CABLine.DrawABLineNew`); B = pivot position; Enter: `CTrk{AB, ptA, ptB, heading = atan2(B−A)}`, name "AB ddd.ddddd° ", then REF-SIDE SHIFT `NudgeRefABLine(±0.5(w−o) + tool.offset)` (right by default) → name panel | `FBT:936-1019, 1123-1136`, `CT:198-207` |
| A+: `btnAPlus`, `nudHeading`, `btnEnter_APlus` | A = pivot; the SAME timer keeps overwriting heading = bearing A→current position, so the effective heading is where you drove to by Enter (stationary >500 ms → atan2(0,0) = north); or stop the timer by typing a heading on the keypad (B = A + 200 m). Enter: `CTrk{AB}`, name "A+ddd.ddddd° ", ref-side shift | `FBT:1033-1119` |
| Curve: `btnACurve`, `btnPausePlay`, `btnBCurve` | A starts recording; the position loop appends `pivotAxlePos` every `sectionTriggerStepDistance/2` (≈0.35–2.5 m, speed/width adaptive); Pause stops recording (A button then adds a manual point per press); B: needs > 2 points, `MakePointMinimumSpacing(1.6)`, `CalculateHeadings`, heading = circular mean, `AddFirstLastPoints` (99 m with boundary / 299 m without), `SmoothAB(4)` (mean of i−2..i+1), `CalculateHeadings`, name "Cu ddd.d° ", ref-side `NudgeRefCurve` (shift + inside-corner prune + 1 m thin + Catmull-Rom 1.2 m resample) | `FBT:748-923`, `POS:1170-1202, 1324-1344, 1550-1553`, `CC:1383-1414, 1453-1475, 1604-1652`, `CT:209-316` |
| `btnRefSideAB/APlus/Curve` | Flips `isRefRightSide` (shared flag, default right). Moves the STORED reference to the right or left swath edge of what you drove; the driven path stays a pass (k = −1 right / k = 0 left); only pass numbering and tram anchoring change | `FBT:28, 759-765, 928-934, 1025-1031` |
| Lat/Lon (`btnEnter_LatLonLatLon`) | Two typed WGS84 points (7 dp keypads, fill-from-GPS buttons) → `CTrk{AB}`; NO ref-side shift (so the typed line is treated as a swath edge) | `FBT:1350-1401` |
| Lat/Lon + Heading (`btnEnter_LatLonPlus`) | Typed point + heading (deg, unwrapped) → B = A + 200 m | `FBT:1405-1464` |
| Pivot by Lat/Lon (`btnEnter_Pivot`) | `CTrk{waterPivot, ptA = centre}`, name "Piv"; guidance builds concentric circles of radius `(k+0.5)(w−o) ∓ offset + nudge` | `FBT:1470-1499`, `CC:298-331` |
| Pivot by driving (`btnLatLonPivotCircle`) | Collect 3 pivot positions with the A button, B = circumcentre (`FindCircleCenter`; (0,0) if collinear) | `FBT:733-758, 808-821, 1501-1513` |
| Load KML (`btnLoadABFromKML`) | Every `<coordinates>`: 2 points → AB, > 2 → Curve (1.6 m spacing, headings, tails, no smoothing); names from `<name>`; no ref-side shift | `FBT:1140-1324` |
| Name panel `btnAdd` / `btnAddTime` / `btnCancel_Name` | Names the LAST track (bug: empty text writes "" — `FBT:1554`); Cancel leaves the track in the list as "New Track" | `FBT:1546-1575, 1523-1544` |
| `btnListUse` (OK) | Invalidate AB/curve caches, YouTurn off, `FileSaveTracks()` ALWAYS; `trk.idx = selectedItem` if visible, else first visible, else stop autosteer with "Guidance stopped / No guidance lines". Note: after creating a track `selectedItem` is still the ORIGINAL row, so OK re-activates the original unless you tap the new one | `FBT:198-263` |
| `btnListDelete` | `RemoveAt(selected)`; `trk.idx = Count−1` (last track becomes active); not saved until OK | `FBT:607-619` |
| `btnCancelMain` | Stops autosteer ("Return From Editing") and YouTurn, restores the `gTemp` snapshot and `originalLine`; nothing written | `FBT:168-196` |
| **FormQuickAB** (main `btnPlusAB`, also auto-opened by Easy Drive) | Only A+ / AB Line / Curve; curve needs > 3 points or the form closes; `btnAdd` names, saves (unless Easy Drive), stops autosteer/YouTurn if on, `trk.idx = last` (new track becomes active), closes | `CTRL:440-462, 570-586`, `FQ:128-371, 383-468, 497-524` |
| Main `btnCycleLines` / `btnCycleLinesBk` | Live forward/back cycle that SKIPS hidden tracks, shows "i/N", and disengages autosteer + YouTurn with "Guidance Stopped – Track Changed" | `Controls.Designer.cs:263-339` |

---

## 2. WHAT AGOPENWEB DOES TODAY (a–d)

### 2a. Straight AB from 2 boundary points — verdict: **MISSING**

No path snaps two taps to fence vertices and makes a straight line. Nearest things:
- Quick AB → "Straight" (`index.html` `[data-qab=drawStraight]`, `app.js:2019-2024, 2076-2083`) → `track.drawStraight` + `track.drawPoint|e,n` ×2 (`RemoteServerWiring.cs:645-657`) → `StartDrawABModeCommand` / `SetABPointCommand` DrawAB (`MainViewModel.Commands.Track.cs:631-741`): RAW tap coordinates (no snap), heading = tap order, `ExtendABLinePastBoundary` raycasts each end to the fence + 20 m and STORES the extended points (`:1567-1640`), name `AB_{deg:F1} HH:mm:ss`, saved immediately.
- Route Planner "Plot A–B" (`#rp-plotab`, `app.js:2532-2548`) → `track.abFromPoints` → `CreateAbTrackFromPoints` (`MainViewModel.RoutePlanning.cs:839-862`): raw taps, ≥ 2 m apart, name "AB n", saved.
- "Longest Edge" (`[data-qab=boundaryEdge]` / `[data-fbadd=bedge]`) → `CreateTrackFromBoundaryCommand` (`Commands.Track.cs:1316-1367`): auto-picks the longest consecutive-vertex fence edge (no operator choice), raw fence vertices ±50 m (no inset — verified lines 1348-1356), name "Boundary Edge N", NOT saved until field close.
- "All Edges" → `CreateTracksFromAllEdgesCommand` (`:1477-1546`): corner detection at 0.35 rad per vertex (can fail on driven boundaries with rounded corners), one AB per edge ≥ 5 m, ±50 m, "Edge N (dist m)", not saved.

The snap machinery already exists for the curve sibling (`nearestBoundaryPt` `app.js:2045-2057`; `NearestIndex` in `RemoteCreateBoundaryCurveSegment`; `FindNearestBoundaryVertex` `MainViewModel.Headland.Remote.cs:213`). The gap is the missing AB variant, on outer AND inner rings.

### 2b. Curve from 2 boundary points, and whole Boundary Curve — verdict: **DIFFERS** (both)

**Segment curve** — Quick AB → "Bnd. Curve" (`index.html:3597`) → `startBoundaryCurve` → two taps → `track.boundaryCurveSeg|aE,aN,bE,bN` (`app.js:2058-2066`, `RemoteServerWiring.cs:669-679`, ungated Tier-1) → `RemoteCreateBoundaryCurveSegment` (`Commands.Track.cs:83-145`):
- Client preview snaps each tap to the nearest raw vertex of ANY ring (outer + inner, `SceneProjector.cs:78-86`) and draws orange A / blue B dots. The host ignores that and re-snaps to its own ring.
- Host ring = Clipper2 INWARD offset of the OUTER boundary only by `ActualToolWidth/2 + UTurnDistanceFromBoundary/2` (`:96-103`, raw ring fallback). `NearestIndex` squared-distance for A and B; `ai == bi` → "Pick two different points".
- Direction: shorter arc by vertex count, walked from the FIRST tap toward the SECOND (`:115-124`) — tap order decides travel direction (AOG: fence winding decides).
- `BuildBoundarySegmentCurve` (`:230-244`): ring[ai..bi] INCLUSIVE of both ends; needs ≥ 3 vertices else "Segment too short for a curve" and nothing is added; `ChaikinsSmooth` ×3 with preserved endpoints (`CurveProcessing.cs:682-725`, corner-cutting, ~8× points); forward-difference `CalculateHeadings` (`:207-226`); `ExtendCurvePastBoundary` (`:1646-1775`): raycast each end tangent to the outer fence crossing + 20 m, 2 m spacing.
- Track `{Name = "Boundary Curve", Type = Curve, IsClosed = false, NoPassOffset = true}`; `SavedTracks.Add`, `SelectedTrack = track` (live guidance immediately, autosteer left engaged), `SaveTracksToFile()`. Remembers `_bndSegTrack/_bndSegRing/_bndSegAi/_bndSegBi/_bndSegStep` for the trim buttons (`:140-144`).
- Ring density problem (verified with Clipper2 1.5.4 during the audit): every boundary is `BoundaryResolution.Normalize`d on load (Douglas-Peucker 0.1 m + 50 m max gap, `MainViewModel.cs:4411`), `FenceLineService.FixSpacing` (`Geometry/FenceLineService.cs:82-95`) has NO callers, and Clipper strips collinear vertices, so on a straight-edged/map-drawn paddock the inset ring can be 4 vertices with 100–400 m gaps. Consequences: two taps 40 m apart on one side both snap to the same corner ("Pick two different points"); a straight side between two corners is a 2-vertex arc and is refused; the "shorter arc by count" can pick the longer arc by distance; Chaikin cuts a 90° corner by ≈0.125·(leg sum) ≈ 9 m on 50 m legs; and the 5 m trim buttons are inert because the first ring segment already exceeds the budget.
- Persistence: `NoPassOffset` is written only by `GeoJsonFieldService.BuildTrackFeature` (`:268`), but every production Save passes `tracks:null` and `FieldService.LoadField` discards geojson tracks; `LoadTracksFromField` (`MainViewModel.cs:5923-5960`) reloads TrackLines.txt whose reader (`TrackFilesService.cs:153-161`) never sets it. After a field reopen the curve silently becomes a normal pass-snapping curve on the inset ring.

**Whole Boundary Curve** — Field Builder → Tracks → Add → "Bnd. Curve" (`index.html:3140`, `app.js:3642`) → `track.boundaryCurve` → `CreateCurveFromBoundaryCommand` (`Commands.Track.cs:1419-1475`): OUTER boundary only; Clipper inward offset by `ActualToolWidth/2` (lines 1435-1441, raw fallback); closes the loop by appending ring[0]; `CalculateHeadings`; `{Name = "Boundary Curve", Type = Curve (saved as mode 4, although `TrackType.BoundaryCurve`/`TrackMode.BndCurve` = 32 exist unused in `TrackFilesService.cs:46/62`), IsClosed = true}`; selected; NOT saved until field close; no one-shot guard (every press stacks another identical track); no inner-boundary tracks. `IsClosed` survives our own reload via `MigrateCurveTrack` (first == last, `MainViewModel.cs:5906-5921`) but an AOG desktop reading mode 4 would treat it as an open curve with tails.

Same label "Bnd. Curve" means the two-tap segment in Quick AB and the whole ring in Field Builder, and both tracks are named "Boundary Curve".

Exact differences vs AOG, in order of operator impact:
1. Placement: ours is inset `w/2 (+ U/2 for the segment)`; AOG's is ON the fence (with the swath-edge convention giving the same "tool edge on the fence" result for pass 0 — see open question 1).
2. Segment direction follows tap order, not fence winding.
3. Ring density / Chaikin change the shape at corners and break the trim buttons on sparse rings.
4. Inner boundaries: AOG can curve around a hole; ours always resolves to the outer ring while the preview dot sits on the hole.
5. Whole ring: outer only, repeatable duplicates, unsaved until close, mode 4 not 32.
6. Naming ("Cu 123.4°" / "Boundary Curve" / "Inner Boundary Curve N" vs "Boundary Curve" for everything) and `NoPassOffset` lost on reopen.

### 2c. Headland curve path — verdict: touch/offset/build **MATCH in effect**, side-selection + files + consumers **DIFFER**

What matches (the audit refuted these as gaps): A/B vertex snapping (`app.js:2183-2227` hlSnap/hlTap; `Headland.Remote.cs:38`, `FieldBuilderEdit.Remote.cs:67`), Line = chord vs Curve = shorter fence arc (`MainViewModel.Headland.Remote.cs:29-90`), whole-boundary build, the clip/stitch outcome (ours auto-rebuilds via `BuildHeadlandFromSegments`, `MainViewModel.Headland.cs:236-728`, after every create/offset/edit/delete — no Build button, no "all ends must cross" dialog), Reset/Undo equivalents, per-row delete. Ours adds per-segment offset edit (`headland.setOffset`), rename, endpoint drag-edit (`headland.editSave`), and the red/yellow `IsEffective` colouring.

What differs and matters:
1. **Offset side** — `ComputeSegmentOffset` (`MainViewModel.Headland.cs:23-166`): closed input → Clipper2 inward offset (Miter for straight-edge boundaries else Round, keeps the LARGEST ring — a paddock with a neck narrower than 2d loses the small lobe); open Line/Curve → true parallel offset with corner intersections, side chosen by testing whether the midpoint normal moves toward the VERTEX CENTROID (`:65-97`). AOG uses fence winding (left of travel = interior) and is correct by construction. Simulated L-shaped 400×400 m paddock with 60 m arms: centroid lies in the notch OUTSIDE the polygon, so a segment on the notch edge offsets INTO the notch. No in-UI way to flip (`headland.setOffset` rejects ≤ 0).
2. **Tool-widths preset** = N × `toolWidthM()` (section span, `app.js:3713, 6434-6440`); AOG = N × (width − overlap). Error = N × overlap.
3. **Overshoot control** — ours fixes `StartExtension = EndExtension = boundary bbox diagonal` (`Headland.Remote.cs:68-84`) and exposes no shrink; on non-convex paddocks an overshoot can cross the fence twice per end and AOG's remedy (A−−/B−−) does not exist here. Endpoint drag moves the boundary anchor vertices, not the overshoot. `ShrinkHeadlandA/BCommand` (`Commands.Boundary.cs:339-347`) is an unrelated, unwired whole-headland inset adjuster.
4. **Files** — ours writes the finished polygon into **Headlines.txt** (one "$HeadLines" path named "Headland", `SaveHeadlandToFile` `MainViewModel.cs:5795-5827` → `HeadlandLineSerializer.Save`) plus `HeadlandSegments.json`, and never writes Headland.txt (only the AgShare stub `AgShareDownloaderService.cs:264`). On open, `BoundaryFileService.LoadHeadland` (`:93-119`) DOES read "Headland.Txt" (case-sensitive probe) into `HeadlandPolygon` and `SetCurrentBoundary` (`MainViewModel.cs:4486-4501`) applies it — then `LoadHeadlandFromField` (`:2042-2103`) unconditionally overrides from Headlines.txt: no tracks → headland nulled and `IsHeadlandOn = false`; tracks present → `Tracks[0].TrackPoints` (which, for an AOG HeadAche field, is a 30 m-overshot construction line) becomes the "headland". Net: AOG-built headlands vanish here; ours are invisible to AOG (and clobber its HeadAche segments). `Boundary.HeadlandPolygon` is never set from the live headland, so the geojson headland role is never written either.
5. **Hydraulic lift** (`GpsPipelineService.ComputeHydLiftState`, `:1745-1765`): single tool-pivot point-in-polygon; no look-ahead (`Machine.LookAhead`, `Guidance.HydLiftLookAheadDistanceLeft/Right` exist in config but are never read); no both-corners test; no reverse hold (`_isReversing` unused, speed is unsigned); no sounds (`SoundEffect.HydraulicLiftUp/Down` defined, never called); does NOT check `FieldTools.IsHeadlandOn` (the toggle only changes map visibility, `MainViewModel.cs:3409-3420`); the pipeline's `_headlandLine` is only refreshed by `SyncGuidanceStateToPipeline` (`GpsHandling.cs:424`), which `ClearHeadland`/the `CurrentHeadlandLine` setter never call, so after Delete All it keeps actuating on the deleted line; and once null reaches it, `ProcessCycle` (`:545-553`) substitutes the synthetic U-turn line into the same local, so lift actuates on fields with NO headland at all. AOG cannot do any of that (`isHydLiftOn` requires `isHeadlandOn` requires a real `hdLine`).
6. **Section control** — matches functionally: per-section look-ahead points at speed × LookAheadOn/Off sampled across the swath (`SectionControlService.cs:428-470, 510-511`), gated on `Tool.IsHeadlandSectionControl && IsHeadlandOn` (`:1113-1143`). Only inner-boundary rings are absent (AOG 6.8.6's UI never populates them either).
7. **U-turn** — ours hands the USER headland polygon to `YouTurnStateMachine` (`GpsPipelineService.cs:518, 740-775`) as the arming zone (`DetermineZone` InCultivatedArea), the >10 m raycast gate, and the next-pass "End of field reached" gate (`YouTurnPathingService.cs:161-253, 384-391`); the fence-inset synthetic line (`:1680-1706`) is only the fallback when no headland exists. The arc itself is still fitted to the fence inset by `UTurnDistanceFromBoundary` (`YouTurnCreationService.Orchestration.cs:338-366`), so the turn shape matches AOG; only arming/eligibility moves with the headland, and toggling the headland OFF does not restore AOG behaviour (only deleting it does).
8. **Selected segment** is only tinted in the `#fb-hllist` row; the map colours by `IsEffective` and never reads `fbHlSel` (`app.js:6664-6675`), so with generic "Line 1 / Curve 2" names the operator cannot see which line Delete/Rename/Offset will hit.

### 2d. A++ / A−− / B++ / B−− — verdict: **DIFFERS** (same labels, different thing)

Ours: `#draw-exta-plus / #draw-exta-minus / #draw-extb-minus / #draw-extb-plus` (`index.html:1341-1344`, titles say "along the boundary, 5 m per tap"), shown ONLY in the `bndSegExtend` phase right after a Quick-AB Bnd. Curve (`showAbBar(false,false,true,true)`, `app.js:2010-2019, 2067-2072`; Cancel is hidden in this phase). Each sends `track.boundarySegExtend|A|B,±1` (`app.js:2307-2309` → `RemoteServerWiring.cs:680-687`) → `RemoteBoundarySegExtend` (`Commands.Track.cs:153-222`):
- Acts ONLY on `_bndSegTrack` (the last Bnd. Curve created this session; "Create a boundary curve first" otherwise; forgotten after delete/field switch) — not on the selected track.
- `stepMeters = 5`, `minCurveMeters = 2`. Walks the A or B ring index along the INSET ring (A++: against the arc's step; B++: with it; −−: into the arc) by whole vertices, accumulating segment lengths while the next would not exceed `budget = min(5, perimeter − 2 − arc)` (extend) or `min(5, arc − 2)` (shorten). Can move 0 → "Boundary curve at maximum/minimum length".
- Rebuilds the WHOLE track via `BuildBoundarySegmentCurve` (re-Chaikin, re-headings, tails regenerated to fence + 20 m), `SaveTracksToFile`, re-selects, `OnTrackVisibilityChanged` (saves again).

So: AOG A++/B++ = +49 m STRAIGHT run-out along the tail heading, unbounded, any selected Curve track, no shrink. Ours = ≤5 m of MORE FENCE-FOLLOWING (vertex-quantised, clamped), last Bnd. Curve only, plus shrink (our extra), straight tails fixed at fence + 20 m and not adjustable. Neither the headland 9 m/5-point buttons nor FormABDraw's 49 m straight extension exist anywhere in our UI. Mitigation: both apps auto-extend curve ends ~200 m straight at guidance time (`CurveProcessing.ExtendCurveEnds`, `GpsPipelineService.cs:904/1337`; AOG `GetExtendedReferenceCurve`), and ours adds ≥20 m past the fence at creation, so the "reach the boundary" use case is already covered; the missing thing is the manual control.

Secondary verdicts relevant to a–d (from the audit, kept short):
- Editor transaction: **MISSING**. No `gTemp`/OK/Cancel. Every action mutates `SavedTracks` live; delete is persisted at once with no undo (`TrackManagement.cs:235-257, 489-502`); A+ / Longest Edge / whole Boundary Curve / All Edges are NOT saved until field close (`MainViewModel.cs:1958`; `Autosave.cs:4-9`).
- Active-track change while engaged: **DIFFERS (safety)**. `SelectedTrack` setter (`MainViewModel.cs:2278-2375`) never disengages; a new Bnd. Curve, a Tracks-manager row tap (`track.select` toggles — tapping the active row deactivates guidance), or `track.cycle` swaps the line under an engaged autosteer; deleting the active track leaves `IsAutoSteerEngaged = true` with PGN 254 status 1 and a frozen steer angle (`PgnBuilder.cs:155-159`). AOG forces autosteer/YouTurn off with "Guidance Stopped" on every editor exit that changed the line and on main-screen cycle.
- Hidden tracks: selectable and cycleable (`RemoteServerWiring.cs:508-517`, `Commands.Track.cs:770-781`); AOG blocks both. No back-cycle, no i/N, no Hide/Show All, no Move Up/Down, no Duplicate.
- Swap A/B: `Points.Reverse()` only (`Commands.Track.cs:334-341`); per-point headings not flipped, no pipeline reset — in-session guidance direction is inverted for AB AND curves until field reopen (`GpsPipelineService.FindNearestSegmentHeading` `:1590-1631` reads stored point headings).
- Record curve: raw GPS event (antenna position + raw NMEA heading, pre-fusion, pre-AntennaToPivot; `GpsHandling.cs:201-205, 335-362`) every ≥2 m; no pause/manual point; no 1.6 m densify / heading recompute / `SmoothAB`; no ref-side + tool-offset shift.
- Drive A→B / A+: no `+tool.offset` at creation (with offset ≠ 0 the implement band lands where the tractor ran, not where the implement ran); A+ is one press from the instantaneous fused heading, no typed heading, not saved until close.
- TrackLines.txt: byte-compatible but AOG stores swath-EDGE points and ours swath-CENTRE; a file moved either way guides exactly (w−o)/2 off every pass. Ours also writes RecordedPath (128) / Contour (256) modes AOG does not define.
- Missing creators: Lat/Lon A–B, Lat/Lon + heading, Pivot (both ways) and pivot guidance, KML track import, Duplicate, name-on-create.

---

## 3. THE REMAP PLAN (prioritised, grouped to ship together)

"Operator-visible" = behaviour change on the tablet; "internal" = file/model/pipeline change with no new button.

### P0 — Safety (ship first, small)

**P0.1 Disengage on guidance-line change** (operator-visible).
`MainViewModel.cs` `SelectedTrack` setter: when `IsAutoSteerEngaged` and the new value is a different track OR null, call the same path as the manual toggle (`_autoSteerService.Disengage()` / `IsAutoSteerEngaged = false`), turn `IsYouTurnEnabled` off, `ClearYouTurnState()`, and post `StatusMessage = "Guidance stopped – track changed"` (the Hint frame already reaches the tablet, `RemoteServerWiring.cs:46-61`). Apply to `CycleABLinesCommand`, `track.select`, `DeleteContourTrackCommand` / `DeleteTrackAt`, `RemoteCreateBoundaryCurveSegment`, `RemoteBoundarySegExtend`, and every `SelectedTrack = track` in the creators. Also zero the steer angle in `AutoSteerService` when the track goes null so PGN 254 does not carry a stale angle.

**P0.2 Hydraulic lift gating** (operator-visible on lift-equipped machines).
`GpsPipelineService.ComputeHydLiftState` (`:1745-1765`): require `FieldTools.IsHeadlandOn` AND a real user headland (pass a flag from `ProcessCycle` so the synthetic line at `:545-553` is never used for lift); hold the previous state while `_isReversing`; emit hydLift = 0 when gated off. `ClearHeadland` and the `CurrentHeadlandLine` setter must call `SyncGuidanceStateToPipeline()` (or `SetHeadlandLine(...)`) so the pipeline copy is never stale. `IsHeadlandOn` setter: push the toggle to the pipeline. Then the AOG look-ahead: compute both outer tool-corner points (ToolPositionService already has them) and a forward probe of `lookAhead(s) × speed` (wire `MachineConfig.LookAhead`, currently dead) — raise only when both corners are in the headland and the probe does not hit the headland polygon edge; lower as soon as it does. Call `SoundEffect.HydraulicLiftUp/Down` on transitions.

### P1 — Boundary-pick parity (FormABDraw behaviour)

**P1.1 Dense, normalised pick ring** (internal, fixes most of 2b).
Add a `BoundaryPickRing` helper: take the ring (raw fence or inset), run `FenceLineService.FixSpacing` at AOG's 1.1/2.2/3.3 m (half for inner rings) and compute headings, BEFORE `NearestIndex` and before walking. Use it in `RemoteCreateBoundaryCurveSegment`, `RemoteBoundarySegExtend`, and the new P1.2 command. This alone fixes "Pick two different points" on straight sides, the inert 5 m trim buttons, the 2-vertex refusal, and the longer-arc-by-distance pick. Send the SAME ring's vertices to the client for the preview snap (or project the host's chosen vertex back as the dot), so the orange/blue dots show where A/B really land.

**P1.2 New command `track.boundaryAB|aE,aN,bE,bN`** (operator-visible: new "Bnd. AB" button in Quick AB and Field Builder).
Host `RemoteCreateBoundaryAB`: snap A to the nearest vertex of ANY ring (outer or inner, record the ring), snap B to the nearest vertex of THAT ring; apply AOG's ordering rule (`|start−end| ≤ n/2` → `start > end`, else `start < end`) so heading matches AOG for the same two taps; heading = atan2(B − A); build the line through the two fence vertices with `ExtendABLinePastBoundary`; placement per open question 1 (recommended: shift the line by `(w−o)/2` to the interior side so pass 0 puts the tool edge on the fence, exactly AOG's net result); name `"AB {deg:F1}°"`; `SavedTracks.Add`, select (with P0.1), `SaveTracksToFile`. Client: reuse `startBoundaryCurve`/`boundaryCurveTap` with a mode flag; after the second tap offer "AB" / "Curve" buttons instead of auto-building (this restores AOG's review-before-commit window and Cancel Touch; see open question 7).

**P1.3 Segment curve geometry** (operator-visible).
In `RemoteCreateBoundaryCurveSegment` / `BuildBoundarySegmentCurve`: walk the shorter arc in FENCE WINDING order regardless of tap order (open question 3); support the ring A was picked on (inner rings offset AWAY from the hole); replace Chaikin with midpoint densification to ≤1.6 m (`MakePointMinimumSpacing` equivalent — exists conceptually in `CurveProcessing`) and central-difference headings; keep `ExtendCurvePastBoundary` but cap the tail at 99 m past the fence crossing (AOG: 99 m) to avoid the run-along-the-fence edge case; name `"Cu {deg:F1}°"`. Persist `NoPassOffset` (and `IsClosed`) in a sidecar `Tracks.meta.json` keyed by track name/index written by `SaveTracksToFile` and merged in `LoadTracksFromField` — TrackLines.txt stays AOG-compatible.

**P1.4 Whole Boundary Curve** (operator-visible).
`CreateCurveFromBoundaryCommand`: one track per ring (outer "Boundary Curve", inner "Inner Boundary Curve N", inner offset away from the hole); `Type = BoundaryCurve` so `TrackFilesService` writes mode 32 (mapping already exists); guard: refuse (status hint) if a BoundaryCurve track already exists, mirroring AOG's greyed button; `SaveTracksToFile()` at the end. Relabel Field Builder button "Bnd. Ring" so it stops colliding with Quick AB's "Bnd. Curve" segment tool.

**P1.5 Persist every creator** (internal, crash safety).
Add `SaveTracksToFile()` to `StartAPlusLineCommand` (`:414-440`), `CreateTrackFromBoundaryCommand` (`:1316-1369`), `CreateCurveFromBoundaryCommand`, `CreateTracksFromAllEdgesCommand` (`:1477-1538`).

### P2 — A++ / A−− / B++ / B−− remap

**P2.1 Decide the semantics** (open question 2). Recommended: keep the along-fence trim as OUR feature but rename the buttons so they stop impersonating AOG's ("A +fence / A −fence / B −fence / B +fence" or "Longer A / Shorter A"), and add AOG's straight extension as a separate control.

**P2.2 Straight extension for any curve** (operator-visible).
New command `track.extendEnd|A|B,metres` → `ExtendTrackEnd(track, isA, m)`: copy the end point, insert/append `m` points at 1 m spacing along that point's heading (AOG `FormABDraw.cs:845-876`; default 49 m; 9 m variant for headland segments if P3.3 is done). Expose as "A+49 m / B+49 m" in the Tracks manager for the SELECTED Curve track (not just the last Bnd. Curve). Save after.

**P2.3 Make the trim work on dense rings** — comes free with P1.1; keep `stepMeters = 5` (AOG's headland shrink is ≈5 m) and recompute the straight tails as today.

**P2.4 Cancel in the trim phase** (operator-visible).
Show `#draw-cancel` during `bndSegExtend`; Cancel = `track.deleteAt` of the just-created track + restore the previously selected track (host remembers `_bndSegPrevSelected`). "Done" stays client-only.

### P3 — Headland: side selection, interop, conveniences

**P3.1 Winding-based offset side** (operator-visible on L/notched paddocks).
In `ComputeSegmentOffset` (`MainViewModel.Headland.cs:65-97`) replace the centroid test: normalise the outer ring to AOG's orientation (interior on the left of travel; shoelace sign as `CFenceLine.cs:157-182`, inner rings opposite), make `ExtractBoundaryRing` return the arc in ring order, and offset to the left of travel. Remove the need for any centroid heuristic. Keep Clipper for closed rings but when it returns multiple rings, keep all rings that are inside the original (necked fields) rather than the largest only.

**P3.2 Tool-widths preset** (internal): `N × (ToolConfig.Width − ToolConfig.Overlap)` in `app.js:3713` (send the host's `ActualToolWidth − Overlap`).

**P3.3 Overshoot control** (operator-visible, optional): per-segment `StartExtension/EndExtension` editable via two buttons ("A −5 m / A +9 m", "B −5 m / B +9 m") → `headland.setExtension|index,A|B,metres` → rebuild. This is the AOG A−−/A++ for headlands.

**P3.4 Selected segment on the map** (cosmetic but cheap): add `Selected` to `HeadlandSegInfoDto` (or use `fbHlSel` client-side) and stroke that segment thicker/magenta with orange A / blue B end dots in `drawHeadlandSegEditLinesSk`.

**P3.5 File roles = AOG's** (internal, interop).
Write Headland.txt ("$Headland", per-boundary block, `e,n,h` 3/3/5 dp) from `SaveHeadlandToFile`; set `Boundary.HeadlandPolygon` from the live headland so geojson carries it; stop writing the polygon into Headlines.txt (leave AOG's HeadAche segments alone; write header-only only when we own the file). On open: probe `Headland.txt` case-insensitively; take the polygon from Headland.txt; treat Headlines.txt as a source ONLY when it contains a single path named exactly "Headland" (our own legacy format) — never use an AOG HeadAche track as the polygon; then rebuild from `HeadlandSegments.json` if present. Apply AOG's 0.005 rad decimation before writing (file size only).

**P3.6 U-turn arming source** (open question 6). If the answer is "like AOG": pass the synthetic fence-inset line to `YouTurnStateMachine` always, and keep the user headland for sections/lift/alarm only. If "keep ours": at least honour `IsHeadlandOn = false` by falling back to the synthetic line.

### P4 — Editor semantics and list tools

**P4.1 Undo for delete** (operator-visible): keep the last deleted `Track` (deep copy + index) in the VM; toast "Track deleted – Undo" for 8 s sends `track.undoDelete`. Cheaper than a full gTemp transaction and covers the accident case.

**P4.2 Hidden tracks**: `track.select`, `SelectTrackAsActiveCommand`, `CycleABLinesCommand` skip/refuse `IsVisible == false`; add `track.cycleBack`; show "i/N" (visible tracks) in the AB flyout; add Hide All / Show All and Move Up / Move Down (`SavedTracks.Move`) and Duplicate (`new Track(copy)` + " Copy") to the Tracks manager.

**P4.3 Swap A/B**: after `Points.Reverse()` add π to every point's `Heading` (wrap), reset `_trackGuidanceState`/YouTurn via `SyncGuidanceStateToPipeline`, `SaveTracksToFile`.

**P4.4 Name on create**: after every creator, pop the existing inline rename input prefilled with the auto-name (+ an "add time" affordance) — the host already has `track.rename`. Give boundary-derived names a timestamp so duplicates do not collide.

### P5 — Driven creators and the track model

**P5.1 Tool offset at creation** (operator-visible when offset ≠ 0; open question 8): in DriveAB (`SetABPointCommand` DriveAB branch) and `FinishCurveRecording`, shift the recorded points by `+Tool.Offset` to the right of the recorded heading so the implement band tiles from where the implement ran (AOG `FormBuildTracks.cs:1000-1013, 872-883` net effect). Do NOT shift tapped/drawn geometry.

**P5.2 Record curve like AOG**: feed `AddCurvePoint` from the pipeline's pivot position + fused heading (after `AntennaToPivotTransform`) instead of the raw `GpsDataUpdated` event; adaptive spacing `min(0.75·w, 5)·twist/2` floored at 0.35 m (or keep 2 m — open question); Pause/Resume and "add point" buttons in the record bar; on finish run densify-to-1.6 m → headings → circular-mean track heading → `SmoothAB(4)` → headings.

**P5.3 A+ two-step**: "A" captures the point; then either drive (heading = bearing A→current, live preview line) or type a heading on a keypad; "Enter" builds B = A + 200 m. Save immediately.

**P5.4 Missing creators** (each a small command + dialog): Lat/Lon A–B and Lat/Lon + heading (reuse `FlagByLatLon`'s WGS84 → local conversion, `Commands.Settings.cs:560-590`); Pivot by lat/lon and by 3 driven points (`FindCircleCenter` circumcentre) — requires pivot GUIDANCE (concentric circles of radius `k·(w−o) + nudge ∓ offset` around `ptA`, 50..500 points, `CABCurve.cs:298-331`) in `TrackGuidanceService` for `TrackType.WaterPivot`, otherwise AOG pivot files load as a 2-point line; KML track import (extend `ParseKmlFile` to accept 2-point and LineString coordinates and route them to `Track.FromABLine`/`FromCurve`).

**P5.5 TrackLines.txt edge/centre convention** (internal, interop; open question 4). If fields are shared with an AOG 6.8.6 install: in `TrackFilesService.Save` shift AB `ptA/ptB` and curve points by `+0.5·(w−o)` to the right of heading (AOG's stored edge) and in `Load` shift back by `−0.5·(w−o)`; write only AB/Curve/BoundaryCurve/WaterPivot modes to TrackLines.txt and keep RecordedPath/Contour in their own files. If never shared, leave as is and document it.

---

## 4. OPEN QUESTIONS FOR THE OPERATOR

1. **Where should a boundary-picked line sit?** AOG stores it ON the fence and its swath-edge convention puts pass 0's centre half a swath inside (tool edge on the fence). Our segment curve sits `w/2 + UTurnDistance/2` inside and is driven directly (`NoPassOffset`); our whole ring sits `w/2` inside; Longest Edge sits ON the fence with centre-convention guidance (so the tool hangs half a swath OVER the fence — almost certainly not what you want). Pick one rule for all four: (a) tool edge on the fence = inset `(w−o)/2` [AOG-equivalent, recommended], (b) AOG's extra U-turn clearance on top for the segment curve only, or (c) on the fence, centre-driven.
2. **What should A++/A−−/B++/B−− mean here?** AOG: straight +49 m run-out per press (track editor) / +9 m and −5 points (headland), never along the fence, no shorten in the track editor. Ours: ±5 m more/less fence-following, last Bnd. Curve only. Options: keep ours and rename the buttons; replace with AOG's; or offer both (P2.2 adds straight extension for any curve). Recommendation: both, with honest labels.
3. **Curve travel direction**: AOG always walks the fence in winding order (A = lower index), so the same two taps give the same direction every time; ours runs from first tap to second. AOG's rule is predictable; ours lets you choose direction by tap order. Which?
4. **Do you exchange field folders with an AgOpenGPS 6.8.6 tablet?** If yes, P5.5 (edge-convention TrackLines.txt) and P3.5 (Headland.txt) are must-haves and we should also stop writing mode 128/256 tracks into TrackLines.txt. If no, P3.5 is still worth it (cheap, future-proof) but P5.5 can wait.
5. **Headland file**: any reason to keep the polygon in Headlines.txt rather than Headland.txt? (Only our own older fields depend on it; P3.5 keeps a fallback.)
6. **U-turn arming**: AOG arms from the fence-inset turn line only; the drawn headland never moves it. Ours arms from the headland polygon when one exists (so a deeper headland delays/blocks turns and refuses side passes inside the headland band). Keep ours (turns respect the headland you drew) or go AOG (turn distance is the config setting, headland is sections/lift only)?
7. **Commit model**: AOG's second tap is a selection — you then choose AB or Curve, or Cancel Touch, and the whole editor can be cancelled. Ours builds, selects and saves on the second tap. Do you want the "tap, tap, then choose AB / Curve / Cancel" step back (P1.2), or is immediate-build + Undo/Delete-track enough?
8. **Tool offset on driven lines**: with a side-offset implement, should A→B / recorded curve tile from where the implement ran (AOG) or from where the tractor ran (ours today)? AOG's is almost certainly right for drilling along a fence; confirm before P5.1 since it changes every offset-tool line created afterwards.
9. **Hyd-lift look-ahead**: AOG uses `hydLiftLookAhead` seconds × speed. We have two unused settings (`MachineConfig.LookAhead`, `Guidance.HydLiftLookAheadDistanceLeft/Right`). Which one should drive P0.2, and in seconds or metres?
10. **Curve smoothing**: AOG does no smoothing on boundary curves (midpoint densify only) and `SmoothAB(4)` on recorded curves. Keep Chaikin (rounder corners, cuts corners by metres on sparse rings) or switch to AOG's shape-preserving densify (P1.3)?
11. **Delete safety**: Undo toast (P4.1), confirm dialog, or a full OK/Cancel editor session like FormABDraw?
12. **Inner boundaries**: worth supporting boundary-picked AB/curves and inner boundary rings (AOG does) on your paddocks, or do you have no holes that matter?