# Work Orders Phase 0 — Quick Wins Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Ship the three low-risk quick wins from `Plans/WORK_ORDERS_AND_INPUTS_PLAN.md` Phase 0 — an explicit "Mark job done" action, a product selector on the floating rate HUD, and grouping the Tank Mix panel under Job/Products — without any data-model change.

**Architecture:** Web client (`Shared/AgOpenWeb.RemoteServer/wwwroot/{app.js,index.html}`) sends pipe/comma commands over `transport.send(...)`; `RemoteServerWiring.cs` switch dispatches them to `MainViewModel`. Job lifecycle lives in `JobService` (`CloseCurrentJob(JobStatus.Done)` already exists). Rate HUD state comes from `/api/ratecontrol` (`rtProducts`, `rtSel`).

**Tech Stack:** .NET 10, C#, vanilla JS web client, NUnit (`Tests/AgOpenWeb.ViewModels.Tests`), browser-preview verification for wwwroot.

## Global Constraints

- Cross-platform parity: all logic in `Shared/`; no platform-head code (CLAUDE.md).
- wwwroot is EMBEDDED — rebuild the Desktop head to see JS/HTML changes.
- No data-model change in Phase 0 (Job/RateProduct/TankMix schemas untouched).
- Web commands are `verb.noun|arg` strings via `transport.send`; backend cases live in the big switch in `RemoteServerWiring.cs`.
- Commit after each task; end messages with the `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>` trailer.

---

### Task 1: "Mark job done" action

**Files:**
- Modify: `Shared/AgOpenWeb.ViewModels/MainViewModel.CoverageExport.cs` (add `MarkActiveJobDone()`)
- Modify: `Shared/AgOpenWeb.RemoteWiring/RemoteServerWiring.cs:437` (add `case "job.done"` next to `job.exportCoverage`)
- Modify: `Shared/AgOpenWeb.RemoteServer/wwwroot/index.html` (add `#ft-jobdone` button in Field Operations, near `#ft-exportcov`)
- Modify: `Shared/AgOpenWeb.RemoteServer/wwwroot/app.js:1269` (add pointerdown handler)
- Test: `Tests/AgOpenWeb.ViewModels.Tests/JobDoneTests.cs` (new)

**Interfaces:**
- Consumes: `IJobService.CloseCurrentJob(JobStatus)` (exists), `IJobService.ActiveJob`, existing `ExportCoverageForActiveJob()`.
- Produces: `MainViewModel.MarkActiveJobDone()` (void) — exports coverage then closes the active job as Done; web command `job.done` (no arg).

- [ ] **Step 1: Write the failing test** — mirror the existing ViewModels.Tests setup (fake `IJobService` with an InProgress `ActiveJob`); assert that after `MarkActiveJobDone()`, `CloseCurrentJob(JobStatus.Done)` was invoked and coverage export ran.

```csharp
// JobDoneTests.cs — follow the harness other MainViewModel tests use (fakes for IJobService/ICoverageMapService).
[Test]
public void MarkActiveJobDone_exports_then_closes_as_done()
{
    var (vm, jobSvc) = BuildVmWithActiveJob(status: JobStatus.InProgress);
    vm.MarkActiveJobDone();
    Assert.That(jobSvc.LastClosedStatus, Is.EqualTo(JobStatus.Done));
    Assert.That(jobSvc.CoverageExportedBeforeClose, Is.True);
}

[Test]
public void MarkActiveJobDone_noop_when_no_active_job()
{
    var (vm, jobSvc) = BuildVmNoJob();
    Assert.DoesNotThrow(() => vm.MarkActiveJobDone());
    Assert.That(jobSvc.CloseCalled, Is.False);
}
```

- [ ] **Step 2: Run the test, verify it fails** — Run: `dotnet test Tests/AgOpenWeb.ViewModels.Tests --filter JobDoneTests`. Expected: FAIL (`MarkActiveJobDone` not defined).

- [ ] **Step 3: Implement `MarkActiveJobDone()`** in `MainViewModel.CoverageExport.cs`, next to `ExportCoverageForActiveJob`:

```csharp
/// <summary>Operator explicitly finishes the current job: write the application
/// record, then mark it Done (vs the field-close path which only Suspends).</summary>
public void MarkActiveJobDone()
{
    if (_jobService.ActiveJob == null) return;
    ExportCoverageForActiveJob();                 // Tier-1 application record
    _jobService.CloseCurrentJob(JobStatus.Done);  // status → Done, clears ActiveJob
}
```

- [ ] **Step 4: Add the backend command case** in `RemoteServerWiring.cs`, immediately after the `job.exportCoverage` case (line ~437):

```csharp
case "job.done": // operator marks the active job complete (writes record, sets Done)
    vm.MarkActiveJobDone();
    return;
```

- [ ] **Step 5: Add the web button + handler.** In `index.html`, next to `#ft-exportcov`, add:

```html
<button class="fo-btn" id="ft-jobdone" title="Finish this job — writes the application record and marks it Done">Finish Job</button>
```
In `app.js` after the `#ft-exportcov` handler (line ~1271):

```js
document.getElementById('ft-jobdone').addEventListener('pointerdown', e => {
  e.stopPropagation();
  showConfirm('Finish job', 'Mark the current job done? Coverage is saved as the application record.',
    () => transport.send('job.done'));
});
```

- [ ] **Step 6: Run the test, verify it passes** — Run: `dotnet test Tests/AgOpenWeb.ViewModels.Tests --filter JobDoneTests`. Expected: PASS.

- [ ] **Step 7: Browser-verify** — rebuild Desktop head, open `:5174`, open a field+job, tap **Finish Job**, confirm the job shows Done in the Jobs list and `coverage.geojson` was written.

- [ ] **Step 8: Commit**

```bash
git add Shared/AgOpenWeb.ViewModels/MainViewModel.CoverageExport.cs Shared/AgOpenWeb.RemoteWiring/RemoteServerWiring.cs Shared/AgOpenWeb.RemoteServer/wwwroot/index.html Shared/AgOpenWeb.RemoteServer/wwwroot/app.js Tests/AgOpenWeb.ViewModels.Tests/JobDoneTests.cs
git commit -m "feat(jobs): explicit Mark Job Done action (job.done → CloseCurrentJob(Done))"
```

---

### Task 2: Product selector on the floating rate HUD

**Files:**
- Modify: `Shared/AgOpenWeb.RemoteServer/wwwroot/index.html` (add a `<select id="hud-prodsel">` to the floating rate/switchbox HUD markup)
- Modify: `Shared/AgOpenWeb.RemoteServer/wwwroot/app.js` (populate it from `rtProducts`; on change set the active channel `rtSel`)

**Interfaces:**
- Consumes: existing `rtProducts` (array from `/api/ratecontrol`), `rtSel` (selected channel index), `rtRefresh()`/HUD render loop.
- Produces: HUD-local `hudRenderProductSelector()` that lists enabled products (fallback: all 5 channels) and switches `rtSel` on change; no backend change.

- [ ] **Step 1: Add the selector element** to the floating HUD block in `index.html` (the switchbox+rate HUD container):

```html
<select id="hud-prodsel" class="hud-sel" title="Active product for the rate HUD"></select>
```

- [ ] **Step 2: Populate + wire it** in `app.js` where the HUD renders (alongside the switchbox/rate HUD update). Show enabled products by letter+name, default to all five if none enabled:

```js
function hudRenderProductSelector() {
  const sel = document.getElementById('hud-prodsel'); if (!sel) return;
  const list = (rtProducts && rtProducts.length ? rtProducts : []);
  sel.innerHTML = '';
  list.forEach((p, i) => {
    const o = document.createElement('option');
    o.value = i;
    o.textContent = String.fromCharCode(65 + i) + (p && p.name ? ' · ' + p.name : '');
    if (i === rtSel) o.selected = true;
    sel.appendChild(o);
  });
  sel.onchange = () => { rtSel = parseInt(sel.value) || 0; if (typeof rtRender === 'function') rtRender(); };
}
```
Call `hudRenderProductSelector()` from the same place the HUD refreshes (wherever `rtProducts` is applied after `/api/ratecontrol`).

- [ ] **Step 3: Browser-verify** — rebuild, open `:5174`, SIM on, open the floating rate HUD; confirm the selector lists A–E (+names), and picking one changes which product the HUD/rate panel targets (the Rate Control panel's active channel follows it).

- [ ] **Step 4: Commit**

```bash
git add Shared/AgOpenWeb.RemoteServer/wwwroot/index.html Shared/AgOpenWeb.RemoteServer/wwwroot/app.js
git commit -m "feat(rate): product selector on the floating rate HUD"
```

---

### Task 3: Group Tank Mix under the Job / Products heading

**Files:**
- Modify: `Shared/AgOpenWeb.RemoteServer/wwwroot/index.html` (present the Tank-Mix entry under the same Job/Products grouping as `#ft-setproduct`/`#ft-setapplied`)

**Interfaces:**
- Consumes: existing `#ft-tankmix` button + `#tankmix` panel + `openTankMix()`; no logic change.
- Produces: the Tank-Mix opener living under the Job/Products group so mix + product read as one concept.

- [ ] **Step 1: Move the opener into the group.** In `index.html`, relocate the `#ft-tankmix` button so it sits with `#ft-setproduct`, `#ft-setapplied`, `#ft-exportcov`, `#ft-jobdone` under a single "Job & Products" sub-heading in Field Operations. Keep the same `id`/handler so `openTankMix()` still fires. Add the sub-heading label above the group:

```html
<div class="fo-group-label">Job &amp; Products</div>
<!-- ft-setproduct, ft-setapplied, ft-tankmix, ft-exportcov, ft-jobdone buttons here -->
```

- [ ] **Step 2: Browser-verify** — rebuild, open `:5174`; confirm the Job Product / Applied / Tank Mix / Export / Finish buttons appear together under one "Job & Products" heading, and Tank Mix still opens and saves to the active job.

- [ ] **Step 3: Commit**

```bash
git add Shared/AgOpenWeb.RemoteServer/wwwroot/index.html
git commit -m "ui(jobs): group Tank Mix under the Job & Products heading"
```

---

## Self-Review

- **Spec coverage:** Phase 0's three bullets (mark-done, floating selector, tank-mix grouping) each map to Task 1/2/3. ✓
- **Placeholder scan:** test harness for Task 1 references the existing ViewModels.Tests fakes — the implementer confirms the exact builder names when writing it (the two asserts and behaviour are concrete). No TODO/TBD elsewhere.
- **Type consistency:** `MarkActiveJobDone()` used identically in the test, VM, and `job.done` case; `rtProducts`/`rtSel`/`rtRender` are the existing names from app.js.
- **Note:** confirm `showConfirm(title,msg,onOk)` is the existing confirm helper signature in app.js (used by the rate module-assign flow) before wiring Task 1 Step 5.
