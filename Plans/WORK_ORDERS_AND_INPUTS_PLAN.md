# Work Orders & Unified Inputs — design spec

**Status:** approved design (2026-08-22), pending implementation plan
**Owner:** lessthancompetent fork
**Supersedes/extends:** `Plans/Completed/FIELDS_AND_JOBS_PLAN.md` (#349 per-field Jobs)

## Goal

Let an operator run **one task across several paddocks** (e.g. "pre-spray for crop"), where a
task carries a **tank mix / list of inputs** (herbicides, pesticides, fertilisers, carrier) at a
job-wide rate or **per-paddock overrides**, allocate that task to paddocks **in any order**
(field→job→product, or fill-tank→drive→pick-paddock-on-map), and have the **tank-fill calculator
allocate a fill to whichever paddock(s) are selected**. Unify "tank mix" and "job product" — they
are the same concept — and surface a **product selector on the floating rate HUD**.

## Guiding decisions (agreed)

1. **Work Order layer, NOT a multi-field Job rewrite.** Per-field `Job` stays the *execution
   record* (coverage is inherently per-paddock geometry — correct as-is). A new top-level
   **Work Order** owns the task + shared mix + the set of allocated paddocks. Additive and
   migration-safe. (Rejected: moving jobs to a top-level store keyed by JobId with cross-field
   coverage — large, risky, and buys nothing because coverage is genuinely per-field.)
2. **Inputs are a planning catalogue; only *metered* inputs link to hardware.** Chemicals in the
   tank are *recorded*; the carrier/metered products map to a rate-control channel. Not every
   input drives hardware.
3. **Per-paddock rate = whole-paddock override.** True intra-paddock variable-rate (prescription
   maps) is a separate future feature, out of scope here.

## Current state (grounded)

- **`Job`** (`Shared/AgOpenWeb.Models/Job/Job.cs`) already carries `Product, Rate, RateUnit,
  AppliedAmount, AppliedUnit` **and** `TankMixJson` (opaque client blob), plus stable `Id` (Guid),
  scalar `FieldName`, `TaskName` (= on-disk folder), `Status` (InProgress/Done/Abandoned).
  Stored at `<FieldsRoot>/<FieldName>/jobs/<TaskName>/job.json` (via `JobJsonService`); coverage
  bins + `coverage.geojson` live in the same folder. `JobService` holds a single `ActiveJob`.
  **`CloseCurrentJob(Done)` exists but no UI action calls it** (field-close uses `SuspendCurrentJob`).
- **Rate control** (`RateControlService`) holds **5 fixed channels** (`RateProduct`, `ProductCount=5`)
  per tool in `channels-{tool}.json`; a global **catalogue** `RateCatalog` (`products-catalog.json`:
  Name/Units/DefaultRate). Products couple to the **tool** (meter cal is a physical property of the
  implement), never a field. No add/remove — 5 slots you fill/enable. `AssignProduct` copies a
  catalogue item's identity into a channel; `SetProductValue(i,key,val)` is the single edit entry.
- **Tank mix** is client-only: `tmState = {areaHa, carrier, tank, buffer, chems:[{name, rate,
  basis}]}`, `basis ∈ {L/ha, mL/ha, kg/ha, g/ha, mL/100L, L/100L}` (dual per-ha **and** per-100L
  bases). Serialized **opaque** into `Job.TankMixJson`. App-wide chem catalogue in
  `PersistentAppState.TankMixCatalogJson`. Tank-fill calc (`tmRenderOut`) + refill overlay
  (`drawRefillSk`). Only link to rate control = a one-time tank-size prefill.
- **Application record**: `coverage.geojson` is single-product (one MultiPolygon, one rate);
  `MeasuredAppliedProvider` = a single flowmeter total.
- **Field selection**: strictly single-field everywhere. Reusable primitives exist:
  `startMapTap` (already does multi-point accumulate for headland/boundary draws),
  `/api/nearbyfields` (`GetNearbyFieldOutlinesJson` — every paddock outline in the map plane),
  `TryOpenFieldAtTap` (boundary-containment "which paddock did I tap"), and the route-planner
  split-blocks as a per-region precedent.

## Target model

### Input catalogue (merge the two catalogues)
`InputCatalogItem { Id, Name, Category (Carrier|Herbicide|Pesticide|Fungicide|Fertiliser|Seed|Other),
DefaultRate, Basis, HardwareChannelRef? }`. `HardwareChannelRef` (optional) links a *metered* input
to a rate-control product/channel; non-metered chems just record. Persisted globally at
`inputs-catalog.json`, migrated from `products-catalog.json` + `TankMixCatalogJson` on first run
(old files left in place for one-way migration, per the repo's file-format philosophy).

### Work Order (new top-level entity)
`WorkOrder { Id (Guid), Name, WorkType, Status (Planned|Active|Done), CreatedAt,
Mix: List<MixLine>, Tank {Carrier, TankSize, Buffer}, Paddocks: List<PaddockAllocation>, Notes }`
- `MixLine { InputId?, Name, Rate, Basis, PerPaddockOverride: Map<PaddockRef,double> }` — job-wide
  rate + optional per-paddock overrides.
- `PaddockAllocation { FieldName, PaddockRef (outer boundary; inner boundaries optional later),
  Status (Pending|InProgress|Done), JobId? }` — links to the per-field job created when driven.
- Stored at `<Root>/workorders/<Id>/workorder.json` via a new `WorkOrderService : IWorkOrderService`.
- Status rolls up from its `PaddockAllocation`s' per-field job statuses.

### Per-field Job (small extension)
Add nullable `WorkOrderId`. When a paddock is driven, its per-field `Job` is created/opened and
**seeded** from the work order's mix (resolving per-paddock overrides) into the existing
`Product/Rate/RateUnit/TankMixJson` fields. Existing standalone jobs keep working (`WorkOrderId`
null). Bump `JobDto.SchemaVersion`.

### Tank-fill allocation
The tank-fill calc reads the work order's mix + the **summed area of the selected paddock(s)**,
computes fills, and marks those paddocks allocated/filled — usable in either order.

### Floating HUD product selector
The rate HUD/switchbox gains a compact selector to pick the **active input/channel** (from the work
order's mix or the 5 channels) without opening the config panel.

## Phased delivery (build in order)

**Phase 0 — quick wins (low risk, addresses today's complaints):**
- "Mark job done" action → wire `JobService.CloseCurrentJob(Done)` to a Field-Tools/job button +
  web command (`job.done`), with a resume-if-reopened path intact.
- Floating-HUD **product selector** (switch active rate-control channel from the HUD).
- Relocate the Tank Mix panel under the **Job / Products** heading (UI grouping only; no model
  change) so mix and product read as one thing.

**Phase 1 — unify catalogues + type the mix:**
- `InputCatalogItem` + `inputs-catalog.json` + `IInputCatalogService`; migrate both existing
  catalogues; UI reads the merged list. Keep rate-control channel identity separate (meter cal
  stays per-tool).
- Promote `Job.TankMixJson` (opaque) → a typed backend `TankMix { AreaHa, Carrier, Tank, Buffer,
  List<TankMixLine{Name,Rate,Basis}> }` that serializes 1:1 to the current JS shape (wire-compatible).

**Phase 2 — Work Orders + multi-paddock allocation:**
- `WorkOrder` model + store + `WorkOrderService` (create, add/remove paddocks, allocate mix,
  status roll-up).
- **Multi-paddock map select**: add an *accumulate* mode to `startMapTap` (copy the headland/sat
  multi-point + Finish convention), a `field.tapSelect` command (toggle add-to-set), a selected-set
  projector, and per-ring "selected" styling in `drawPickOutlinesSk`. Reuse `/api/nearbyfields`
  and factor the field-resolution half out of `TryOpenFieldAtTap`.
- Allocate a work order's mix to the selected paddocks; **tank-fill calc allocates to the selection**.
- Driving a paddock opens/creates its per-field Job seeded from the work order (`WorkOrderId` link).

**Phase 3 — multi-input record + per-paddock rates:**
- Replace/extend the Job's scalar product trio with `List<ProductLine>` (carrier + chemical lines,
  each with rate/unit/applied/measured). Extend `job.setProduct`/`job.setApplied` wire commands and
  `SetActiveJobProduct/Applied`.
- `CoverageExportService.BuildGeoJson` emits a `products[]` array instead of the flat scalar fields
  (keep a back-compat single-product mirror for existing consumers).
- Apply **per-paddock rate overrides** when seeding each paddock's job.

## Migration & back-compat
- Everything additive. Standalone per-field jobs unchanged. `inputs-catalog.json` seeded from the
  two old catalogues (one-way). `TankMix` typed model stays JSON-wire-compatible with the JS blob.
  `coverage.geojson` keeps single-product fields alongside the new `products[]` for existing readers
  (the farm-server viewer / `coverage-records-system`).

## Testing
- Follow existing patterns (`RateCatalogTests.cs`). New unit tests: input-catalogue merge/migration;
  `TankMix` typed round-trip against the current JS blob; `WorkOrderService` create/allocate/rollup;
  per-paddock override resolution; multi-input `coverage.geojson` emission (+ single-product mirror).
- Sim-drive check per phase where observable (floating selector, mark-done, allocate-on-map).

## Out of scope
Intra-paddock variable-rate / prescription maps; per-input measured totals from multiple
flow/scale channels (single flowmeter fallback stays); cross-field shared coverage.
