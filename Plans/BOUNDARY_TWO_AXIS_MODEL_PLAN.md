# Two-axis boundary model (coverage vs physical) — implementation plan

> **STATUS: ✅ COMPLETE (2026-08-23).** All 6 tasks implemented + verified. Models 156/156,
> Services 1168/1170 (2 pre-existing skips), solution builds clean, UI verified live.
> See tester-feedback #13. Out of scope (bug E) unchanged.

> **For agentic workers:** execute task-by-task, test-first. Steps use `- [ ]` checkboxes.

**Goal:** Let each boundary independently control **(a) whether product coverage stops at its
edge** and **(b) whether the implement may physically cross it** — so a fence stops both, a
nominal "apply-here" line stops coverage but not driving, and a broadcast-over-a-hillside/pylon
line stops the machine but lets the *spread* go beyond.

**Architecture:** Two orthogonal per-boundary flags on `BoundaryPolygon`:
- **`StopCoverageAtEdge`** (NEW, coverage axis) — section control turns sections off where the
  **swath** crosses this boundary, using the **working/spread width** (the D-fixed swath check).
- **`IsHard`** (EXISTS, physical axis) — the implement's swept path must stay clear, using the
  **physical frame width** (`Tool.PhysicalWidth`). Live-drive enforcement is bug E (separate).

The four corners fall out of `{StopCoverageAtEdge, IsHard}` per boundary: fence `{T,T}`,
nominal product `{T,F}`, broadcast-beyond-a-barrier `{F,T}`, reference line `{F,F}`.

**Tech stack:** C# / .NET 10, NUnit. Files under `Shared/AgOpenWeb.Models`,
`Shared/AgOpenWeb.Services/Section`, `Shared/AgOpenWeb.Services/GeoJson`,
`Shared/AgOpenWeb.RemoteWiring`, `Shared/AgOpenWeb.RemoteServer/wwwroot`.

## Global Constraints
- **Back-compat / migration:** default `StopCoverageAtEdge = true` on every boundary so a field
  with the flag absent behaves exactly as today (all boundaries gate coverage when the global
  `Tool.IsSectionOffWhenOut` master is on). On load, an outer boundary with no flag → `true`;
  an inner boundary with no flag → `!IsDriveThrough` (a non-drive-through hole already stops
  coverage today).
- `Tool.IsSectionOffWhenOut` stays as the **global master** (off ⇒ no boundary coverage gating
  at all, as today). `StopCoverageAtEdge` only refines gating *when the master is on*.
- Coverage axis uses the **working** half-width; physical axis uses **`Tool.PhysicalWidth`**.
  Never mix them.
- Do **not** change `IsHard` behaviour or the U-turn/planner swept-path in this plan (bug E).
- The physical spread on a `{F,T}` boundary reaches beyond the fence because spread width ≫
  frame width — nothing new is needed for that; it's simply *not* gating coverage there.

## Task 1: `StopCoverageAtEdge` on the model + migration defaults
**Files:** `Shared/AgOpenWeb.Models/BoundaryPolygon.cs`.
- [ ] Add `public bool StopCoverageAtEdge { get; set; } = true;` next to `IsHard`, documented as
  the coverage axis (vs `IsHard` the physical axis).
- [ ] **Test** (Models.Tests): default is `true`; independent of `IsHard`.

## Task 2: per-boundary coverage gating in `Boundary.GetSegmentBoundaryStatus`
**Files:** `Shared/AgOpenWeb.Models/Boundary.cs:117-150`.
- [ ] Gate the OUTER boundary only when `OuterBoundary.StopCoverageAtEdge`; when false, treat the
  outer as fully-inside for coverage (spread may extend beyond) but still process inners.
- [ ] Subtract an inner boundary's inside portion only when `inner.StopCoverageAtEdge` (replaces
  the current `!IsDriveThrough` coverage test; `IsDriveThrough` stays a *planner-routing* concept).
- [ ] **Test** (Models.Tests): a straddling swath reads partly-outside when outer
  `StopCoverageAtEdge=true`, fully-inside when false; an inner hole subtracts only when its flag
  is true.

## Task 3: section control reads the per-boundary flag
**Files:** `Shared/AgOpenWeb.Services/Section/SectionControlService.cs` (GetSegmentBoundaryStatus
wrapper ~1048-1076, and the `!IsSectionOffWhenOut` short-circuit).
- [ ] Keep the `IsSectionOffWhenOut` global master short-circuit. When the master is on, delegate
  to `Boundary.GetSegmentBoundaryStatus` (now per-boundary-flag aware) — the D-fixed swath maths
  is unchanged; only *which* boundaries gate changes.
- [ ] **Test** (Services.Tests, LookAheadSlit harness): drive toward the outer fence — with
  `StopCoverageAtEdge=true` the section forces off at the edge (as today, post-D); with it `false`
  the section keeps painting past the line (broadcast case), still forced off only by a hole or
  the field being genuinely absent.

## Task 4: persistence (geojson) round-trip + migration
**Files:** `Shared/AgOpenWeb.Models/GeoJson/GeoJsonModels.cs` (add
`public const string StopCoverageAtEdge = "stopCoverageAtEdge";`),
`Shared/AgOpenWeb.Services/GeoJson/GeoJsonFieldService.cs` (write + read the property for outer and
each inner). Also `BoundaryFileService.cs` if the legacy Boundary.txt path carries flags.
- [ ] Write `stopCoverageAtEdge` alongside `isHard`/`isDriveThrough`.
- [ ] On read: present ⇒ use it; absent ⇒ outer `true`, inner `!isDriveThrough` (migration).
- [ ] **Test** (Services.Tests): serialize a boundary with `{StopCoverageAtEdge=false, IsHard=true}`,
  reload, assert both survive; a legacy field with no property migrates per the rule above.

## Task 5: UI — per-boundary flag + wiring
**Files:** `Shared/AgOpenWeb.RemoteWiring/RemoteServerWiring*.cs` (mirror the `isHard` command with
`boundary.stopCoverage`), `Shared/AgOpenWeb.RemoteServer/wwwroot/index.html` + `app.js` (add the
toggle next to the existing hard-boundary toggle, per selected boundary; outer + each inner).
- [ ] Command sets the flag on the selected boundary (outer index 0 / inner N) and persists.
- [ ] Label it as the **coverage** axis ("Spray to edge" / "Stop coverage here") distinct from the
  physical **"Hard boundary"** toggle.
- [ ] Sim-drive check: toggling it live changes whether the sprayer paints past the edge.

## Task 6: full-suite regression + the four-corner matrix
- [ ] `dotnet test Tests/AgOpenWeb.Models.Tests` and `…Services.Tests` green (defaults keep every
  existing test identical — `StopCoverageAtEdge` defaults true).
- [ ] One matrix test asserting the four corners on the outer edge: `{T,·}` stops coverage at the
  line; `{F,·}` paints beyond. (IsHard's live effect is bug E, asserted only at the planner level
  where it already is.)

## Out of scope
Bug **E** (live-turn swept-path enforcement of `IsHard`) — the physical axis's live enforcement is
its own piece. Planner `IsDriveThrough` routing (avoid-vs-through) stays as-is. Intra-field
variable coverage. Per-inner physical width beyond the existing `Tool.PhysicalWidth`.
