# Handoff: coverage records ("jobs") → farm server — how it works, and how to build it into AgOpenGPS

**Audience:** a fresh session porting this feature onto AgOpenGPS (WinForms, `SourceCode/GPS`,
6.8.6 at `C:\Users\OEM\Downloads\AgOpenGPS-src-6.8.6\...\SourceCode\GPS`).
**Source of truth:** the AgOpenWeb code quoted below, and the LIVE server on the media box.
Everything here was verified against the running system on 2026-08-23 (a record driven that
afternoon — field `house n 2025-05-29`, job `2026-08-23`, 0.659 ha — was already indexed and
visible at `http://100.120.151.67:8082` with nothing but Syncthing in between).

Reference implementation files (AgOpenWeb repo):

| Concern | File |
|---|---|
| Record builder (the byte-exact spec) | `Shared/AgOpenWeb.Services/Coverage/CoverageExportService.cs` |
| When it is written, what it is fed | `Shared/AgOpenWeb.ViewModels/MainViewModel.CoverageExport.cs` |
| Job model + `job.json` | `Shared/AgOpenWeb.Models/Job/Job.cs`, `Shared/AgOpenWeb.Services/Fields/JobJsonService.cs`, `Shared/AgOpenWeb.Services/JobService.cs` |
| Product catalogue | `Shared/AgOpenWeb.Services/RateControl/RateCatalog.cs`, `RateControlService.cs` (`CatalogFile`) |
| Field boundary the server draws | `Shared/AgOpenWeb.Services/GeoJson/GeoJsonFieldService.cs`, `Shared/AgOpenWeb.Models/GeoJson/GeoJsonModels.cs` |
| Local↔WGS84 projection | `Shared/AgOpenWeb.Models/Base/GeoConversion.cs` |
| Server indexer + viewer (what the server accepts) | `deploy/pi-coverage/ingest.py`, `deploy/pi-coverage/viewer.py`, `deploy/pi-coverage/static/index.html` |
| User-facing description | `Docs/guides/fields-jobs-coverage.md` |

---

## 1. The architecture in one paragraph

The in-cab app never talks to the server. It writes **plain files into the field folder**:
one `coverage.geojson` per job (the application record — a WGS84 MultiPolygon of the area
actually painted, tagged with product/rate/area/times) and one `field.geojson` per field
(the boundary). **Syncthing** mirrors the whole Fields folder to the server (folder id
`agopen-fields`, receive-only on the server, staggered versioning). On the server a 60-second
scanner (`ingest.py`) upserts every `*/jobs/*/coverage.geojson` and `*/field.geojson` into
SQLite (`coverage.db`), and a tiny HTTP viewer (`viewer.py`, port 8082, Leaflet map) serves
them with filters by field / product / date and an actual-vs-target flag. **The files are the
record; the database is a rebuildable index.** There is no upload API to implement.

So "building the same feature into AgOpenGPS" means exactly three things:

1. Give AgOpenGPS a **job** concept (product, rate, unit, work type, applied total, start/end).
2. On job close (and on demand), **export the painted coverage as the `coverage.geojson` below**
   into `<Fields>/<FieldName>/jobs/<TaskName>/`, plus write `field.geojson` for the field.
3. Point Syncthing at AgOpenGPS's Fields folder (or merge it into the existing share).

The server needs **no changes** if the formats below are honoured.

---

## 2. On-disk layout the server expects

```
<FieldsRoot>/
  <FieldName>/                      ← folder name IS the field identity on the server
    field.geojson                   ← boundary (FeatureCollection, see §5)
    Field.txt, Boundary.txt, …      ← AgOpenGPS's own files, ignored by the server
    jobs/
      <TaskName>/                   ← e.g. 2026-08-23_spreading
        job.json                    ← job metadata (§4) — NOT read by the server, but keep it
        coverage.geojson            ← THE application record (§3) — read by the server
        elevation.geojson           ← optional, not read by the server
        coverage_detect.bin …       ← AgOpenWeb's own paint files, ignored by the server
```

Server globs (`ingest.py:231-232`):

```python
for pattern, fn in (("*/jobs/*/coverage.geojson", ingest_coverage),
                    ("*/field.geojson", ingest_field)):
```

Change detection is `(mtime, size)` per path; re-export = upsert (idempotent). Everything
else in the tree is ignored, so AgOpenGPS's existing `Field.txt`/`Sections.txt`/`Contour.txt`
can stay exactly as they are. **Always rewrite the whole file — never append.**

Live server: media box `100.120.151.67` (house LAN `192.168.5.134`), data at
`/srv/agdata` (`fields` → symlink `/srv/backup/agopen-fields`), services
`agdata-ingest`, `agdata-viewer`, `syncthing@keritapu`. A second identical instance runs
on the cowshed Pi (`rtk-pi:8082`) when its bridge is up.

---

## 3. `coverage.geojson` — exact format

### 3.1 Shape

- Top level is a **bare `Feature`** — NOT a FeatureCollection. The ingest reads
  `doc["geometry"]` and `doc["properties"]` directly.
- `geometry.type` is **always `MultiPolygon`** (even for one polygon). A `Polygon` is
  rejected: `raise ValueError("not a MultiPolygon coverage record")` (`ingest.py:132-133`).
- `coordinates[poly][ring][pt] = [lon, lat]` — **WGS84, lon first**, 7 decimals
  (`"0.0000000"`, ≈1 cm), `InvariantCulture`. Ring 0 of each polygon is the outer ring,
  rings 1..n are holes. Rings are explicitly closed (first point repeated last).
- No `crs`, no `bbox`, no `id`, no whitespace — single line.
- Write nothing (return null) if there is no painted area.

### 3.2 Properties (emission order; server reads every one)

| Property | Type | Present | Format | Meaning |
|---|---|---|---|---|
| `schema` | int | always | `1` | record schema version |
| `field` | string | always | JSON-escaped | **must equal the field folder name** (server joins applications ↔ boundaries on it) |
| `job` | string | always | | the job folder name (`TaskName`) |
| `product` | string | always (may be `""`) | | product name, e.g. `"ammo 36n"` — matched to the catalogue by name, case-insensitive |
| `rate` | number | always | `0.###` | target rate per hectare, in `rateUnit` |
| `rateUnit` | string | always (may be `""`) | | one of `kg/ha`, `L/ha`, `seeds/ha`, `t/ha` (free text, but the UI offers these) |
| `workType` | string | always (may be `""`) | | free text: `fertilizing`, `spraying`, `seeding`, `cultivating`, `tillage`, `harvesting`, … |
| `workedHa` | number | always | `0.####` | **hectares, from the painted-cell count** (not the polygon area — see gotchas) |
| `toolWidthM` | number | always | `0.##` | metres, sum of active section widths |
| `appliedAmount` | number | only if > 0 | `0.###` | measured total if known, else `rate × workedHa` |
| `appliedUnit` | string | with `appliedAmount` | | measured unit, else `rateUnit` before the `/` (`kg/ha` → `kg`) |
| `appliedMeasured` | bool | with `appliedAmount` | `true`/`false` | true only when the amount came from a scale/flowmeter/tank total |
| `actualRate` | number | with `appliedAmount` and `workedHa > 0` | `0.###` | `appliedAmount / workedHa` |
| `startedAt` | string | always | ISO-8601 round-trip `"o"` | local time **with offset**, e.g. `2026-08-23T13:14:44.9368210+12:00` (`DateTime.Now.ToString("o")`) |
| `endedAt` | string | only when the job was closed | `"o"` | same |
| `exportedAt` | string | always | `"o"` | `DateTime.Now` at export |

Server-side mapping (`ingest.py:156-161`): `field, job, product, rate, rateUnit, workType,
workedHa, toolWidthM, appliedAmount, appliedUnit, appliedMeasured, actualRate, startedAt,
endedAt, exportedAt`. Missing optionals default to `0` / `""`. Primary key is
**`(field, job, product)`**.

### 3.3 Worked example (one polygon, no holes)

```json
{"type":"Feature","geometry":{"type":"MultiPolygon","coordinates":[[[[175.2911234,-37.9012345],[175.2916000,-37.9012345],[175.2916000,-37.9008000],[175.2911234,-37.9008000],[175.2911234,-37.9012345]]]]},"properties":{"schema":1,"field":"house n 2025-05-29","job":"2026-08-23_spreading","product":"ammo 36n","rate":120,"rateUnit":"kg/ha","workType":"fertilizing","workedHa":0.659,"toolWidthM":12,"appliedAmount":79.08,"appliedUnit":"kg","appliedMeasured":false,"actualRate":120,"startedAt":"2026-08-23T13:14:44.9368210+12:00","endedAt":"2026-08-23T14:02:10.1234567+12:00","exportedAt":"2026-08-23T14:02:10.2000000+12:00"}}
```

### 3.4 Reference emitter (C#, AgOpenWeb `CoverageExportService.cs:126-171`)

```csharp
var sb = new StringBuilder();
sb.Append("{\"type\":\"Feature\",\"geometry\":{\"type\":\"MultiPolygon\",\"coordinates\":[");
for (int pi = 0; pi < polys.Count; pi++)
{
    if (pi > 0) sb.Append(',');
    sb.Append('[');
    for (int ri = 0; ri < polys[pi].Count; ri++)
    {
        if (ri > 0) sb.Append(',');
        sb.Append(Ring(polys[pi][ri]));          // "[[lon,lat],…,[lon0,lat0]]"
    }
    sb.Append(']');
}
sb.Append("]},\"properties\":{")
  .Append("\"schema\":1")
  .Append(",\"field\":\"").Append(J(fieldName)).Append('"')
  .Append(",\"job\":\"").Append(J(taskName)).Append('"')
  .Append(",\"product\":\"").Append(J(product)).Append('"')
  .Append(",\"rate\":").Append(rate.ToString("0.###", inv))
  .Append(",\"rateUnit\":\"").Append(J(rateUnit)).Append('"')
  .Append(",\"workType\":\"").Append(J(workType)).Append('"')
  .Append(",\"workedHa\":").Append(workedHa.ToString("0.####", inv))
  .Append(",\"toolWidthM\":").Append(toolWidthM.ToString("0.##", inv));
double applied = appliedAmount > 0 ? appliedAmount
               : (rate > 0 && workedHa > 0 ? rate * workedHa : 0);
if (applied > 0)
{
    string unit = appliedAmount > 0 && !string.IsNullOrWhiteSpace(appliedUnit)
        ? appliedUnit : rateUnit.Split('/')[0];
    sb.Append(",\"appliedAmount\":").Append(applied.ToString("0.###", inv))
      .Append(",\"appliedUnit\":\"").Append(J(unit)).Append('"')
      .Append(",\"appliedMeasured\":").Append(appliedAmount > 0 ? "true" : "false");
    if (workedHa > 0)
        sb.Append(",\"actualRate\":").Append((applied / workedHa).ToString("0.###", inv));
}
sb.Append(",\"startedAt\":\"").Append(startedAt.ToString("o", inv)).Append('"');
if (endedAt.HasValue)
    sb.Append(",\"endedAt\":\"").Append(endedAt.Value.ToString("o", inv)).Append('"');
sb.Append(",\"exportedAt\":\"").Append(DateTime.Now.ToString("o", inv)).Append('"')
  .Append("}}");
// J(s) = s.Replace("\\","\\\\").Replace("\"","\\\"");  inv = CultureInfo.InvariantCulture
```

### 3.5 Geometry pipeline (how the painted area becomes a MultiPolygon)

AgOpenWeb paints coverage into a display grid; the export walks the painted cells:

1. Per grid row, merge runs of painted cells into axis-aligned rectangles (local E/N metres).
2. Union all rectangles with **Clipper2** (`Clipper64`, `FillRule.NonZero`, output to a
   `PolyTree64` so holes nest correctly). Integer scale `S = 100` (cm precision).
3. `Clipper.SimplifyPath(ring, 0.4 m)`; drop rings with < 3 points or area < 4 m².
4. Walk the PolyTree: even depth = outer polygon, its children = holes, a hole's children
   start new outer polygons.
5. Convert each vertex local → WGS84 (§6) and emit.
6. `workedHa = paintedCellCount × cellSize² / 10000`.

**For AgOpenGPS** the equivalent raw material is the section triangle strips
(`CPatches` / `patchList` — `List<List<vec3>>` per section, `Sections.txt` on disk). Options,
in order of preference:

- **Union the triangles** with Clipper2 exactly as above (treat each triangle of each strip
  as a polygon; Clipper's NonZero union handles the overlaps). This gives the same output
  class as AgOpenWeb and the viewer renders it identically. `workedHa` can then be
  AgOpenGPS's own `fd.workedAreaTotal / 10000` (it already tracks non-overlapping worked area)
  — closest in spirit to "painted cells".
- Rasterise the strips to a grid (e.g. 0.25 m) and run the row-merge + union above — slower
  to write, but it's literally the AgOpenWeb algorithm and you get holes for free.

Either way the server only cares that it receives a valid WGS84 MultiPolygon.

Clipper2 NuGet: `Clipper2` (Angus Johnson). AgOpenWeb uses `Clipper2Lib` namespace
(`Clipper64`, `Path64`, `Paths64`, `PolyTree64`, `Clipper.Union`, `Clipper.SimplifyPath`,
`Clipper.Area`).

### 3.6 When to write it

AgOpenWeb writes it at four points (`MainViewModel.CoverageExport.cs`, `MainViewModel.cs:1926`):

1. **Field close** — while the paint is still in memory (the primary path).
2. **On field open, if missing** and there is paint on disk ("self-healing" after a crash).
3. **When the operator enters an Applied Total** for the job.
4. **Manual "Export Coverage"** button in Field Tools.

It is **not** written on section-off and **not** periodically. For AgOpenGPS: hook
`FileSaveEverythingBeforeClosingField()` (and the `Job → Close` menu) plus a button in the
field menu. Writing it also on the 30 s autosave is fine if cheap — the server upserts.

---

## 4. The job model and `job.json`

The server doesn't read `job.json`, but the export is built from it and it's what lets a
job be resumed across days and keep one record. Keep it.

Path: `<Field>/jobs/<TaskName>/job.json`. A job "exists" iff its folder contains a `job.json`.

`Job` (`Shared/AgOpenWeb.Models/Job/Job.cs`) — the fields that matter:

```csharp
public class Job
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FieldName { get; set; } = "";      // field folder name
    public string TaskName { get; set; } = "";       // job folder name, default yyyy-MM-dd_<worktype>
    public string WorkType { get; set; } = "";       // free text
    public string Notes { get; set; } = "";
    public string Product { get; set; } = "";        // e.g. "ammo 36n"
    public double Rate { get; set; }                 // per ha, in RateUnit
    public string RateUnit { get; set; } = "";       // "kg/ha" | "L/ha" | "seeds/ha" | "t/ha"
    public double AppliedAmount { get; set; }        // measured total; 0 = not measured
    public string AppliedUnit { get; set; } = "";    // "kg" | "L" | "t"
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? EndedAt { get; set; }           // set on close, cleared on resume
    public DateTime LastOpenedAt { get; set; } = DateTime.Now;
    public JobStatus Status { get; set; } = JobStatus.InProgress;   // InProgress | Done | Abandoned
}
```

Serialised camelCase, indented, nulls omitted, enums as strings, `schemaVersion: 1`:

```json
{
  "schemaVersion": 1,
  "id": "9f2c5c0e-4b7a-4d1e-9a3c-0c2f9d1f1a11",
  "fieldName": "house n 2025-05-29",
  "taskName": "2026-08-23_spreading",
  "workType": "fertilizing",
  "product": "ammo 36n",
  "rate": 120,
  "rateUnit": "kg/ha",
  "appliedAmount": 0,
  "appliedUnit": "",
  "notes": "",
  "startedAt": "2026-08-23T13:14:44.9368210+12:00",
  "lastOpenedAt": "2026-08-23T13:14:44.9368210+12:00",
  "status": "InProgress",
  "distanceTraveledMeters": 0,
  "areaWorkedHectares": 0,
  "uTurnCount": 0
}
```

Default task name (`JobService.cs:270-292`): `yyyy-MM-dd` + `_` + work-type slug (lowercase,
spaces → `_`, anything not `[a-z0-9-_]` → `_`); uniqueness by appending `_2`, `_3`, ….

Lifecycle rules that the record depends on (`Plans/Completed/FIELDS_AND_JOBS_PLAN.md`):
- **Resume** clears `EndedAt`, sets `InProgress`, and **appends to the same coverage** — one
  job's record can span several days. Don't create a new job per day automatically.
- **Close** sets `EndedAt = now`, `Status = Done`, then exports.
- Product/rate can be set or changed at any time before export; the record is tagged with
  whatever the job holds at export time.

### 4.1 Product catalogue

AgOpenWeb keeps one global list at `<DataRoot>/RateController/products-catalog.json`
(`DataRoot` = parent of the Fields folder; on the tablet `~/Documents/AgOpenWeb`), PascalCase:

```json
{
  "Items": [
    { "Name": "ammo 36n", "Units": "kg", "DefaultRate": 120 },
    { "Name": "urea",     "Units": "kg", "DefaultRate": 100 },
    { "Name": "Glyph",    "Units": "L",  "DefaultRate": 2.5 }
  ]
}
```

`Units` is the dispensed unit; the job's `rateUnit` is `Units + "/ha"`. Match by name,
case-insensitive. The server derives its own product list from the records it has ingested
(`/api/products` — currently `["Glyph","Moore Unidrill Barley","ammo 36n","urea"]`), so the
catalogue is purely an in-cab convenience: **the only thing that must agree is the spelling
of `product` across records**, or the viewer's product filter splits them. For AgOpenGPS,
either read this same file (recommended — one list for both apps, and it syncs if you put
`RateController/` in the share) or ship a small picker that writes the same format.

---

## 5. `field.geojson` — the boundary the server draws

Path: `<Field>/field.geojson`. FeatureCollection, camelCase, indented. The server uses
**only the feature whose `properties.role == "outer-boundary"`** (`ingest.py:170-178`) and
identifies the field by the **folder name** (`path.parent.name`). Everything else in the
file is optional for the server.

Minimum the server needs:

```json
{
  "type": "FeatureCollection",
  "features": [
    {
      "type": "Feature",
      "geometry": { "type": "Point", "coordinates": [175.2913, -37.9010] },
      "properties": {
        "role": "metadata",
        "name": "house n 2025-05-29",
        "originLatitude": -37.9010,
        "originLongitude": 175.2913,
        "convergence": 0.0,
        "areaHectares": 3.21,
        "createdDate": "2025-05-29T10:00:00.0000000+12:00",
        "lastModifiedDate": "2026-08-23T14:02:10.0000000+12:00"
      }
    },
    {
      "type": "Feature",
      "geometry": {
        "type": "Polygon",
        "coordinates": [[[175.2911234,-37.9012345,0.0],[175.2916000,-37.9012345,1.5708],[175.2916000,-37.9008000,3.1416],[175.2911234,-37.9008000,4.7124],[175.2911234,-37.9012345,0.0]]]
      },
      "properties": {
        "role": "outer-boundary",
        "isDriveThrough": false,
        "isHard": false,
        "stopCoverageAtEdge": false,
        "areaHectares": 3.21
      }
    }
  ]
}
```

Notes:
- Boundary ring points are `[lon, lat, heading]` — the third ordinate is the point heading in
  **radians** (a deliberate deviation from GeoJSON's altitude; the server's `bbox_of` reads
  only `c[0]`/`c[1]`, Leaflet ignores it). Emitting plain `[lon, lat]` is also fine.
- One ring per Polygon. Inner boundaries (holes/obstacles) are separate features with
  `role: "inner-boundary"` — the server ignores them.
- Other roles AgOpenWeb writes (`headland`, `track`, `background-image`) are ignored by the
  server; don't bother emitting them from AgOpenGPS.
- Source for AgOpenGPS: `bnd.bndList[0].fenceLine` (local E/N) + `pn.latStart/lonStart` →
  WGS84 via §6. `Boundary.txt` already has the ring; this is just a second encoding of it.

Role/key constants (`GeoJsonModels.cs:86-117`): `role, name, originLatitude, originLongitude,
convergence, areaHectares, createdDate, lastModifiedDate, isDriveThrough, isHard,
stopCoverageAtEdge`; roles `metadata, outer-boundary, inner-boundary, headland, track,
background-image`.

---

## 6. Local metres → WGS84 (must match, or polygons shift)

AgOpenWeb and AgOpenGPS both use the flat "CNMEA" local plane about the field origin. In
6.8.6 that lives in `AgOpenGPS.Core/Models/Base/LocalPlane.cs`:

```csharp
Wgs84 LocalPlane.ConvertGeoCoordToWgs84(GeoCoord geoCoord)   // geoCoord = (Northing, Easting)
// instance: mf.AppModel.LocalPlane  (origin set by CNMEA.DefineLocalPlane on field open)
```

Use it directly — `new GeoCoord(northing, easting)` from each `vec3`/`vec2` (`.northing`,
`.easting`), then `.Latitude`/`.Longitude` on the returned `Wgs84`. Two things to know:

- It **adds `SharedFieldProperties.DriftCompensation`** before converting. That is correct
  for coverage (the paint was laid down in drift-compensated local space), so don't subtract
  it; just be aware the boundary and the paint go through the same call.
- The origin must be the **field's** origin (`mf.AppModel.LocalPlane.Origin`, i.e. the one
  `Field.txt` records) — not the current GPS fix.

For reference, AgOpenWeb's `GeoConversion.cs:36-75` is the same arithmetic (it uses the
origin latitude for metres-per-degree-longitude where `LocalPlane` uses the point's own
latitude — a sub-centimetre difference at paddock scale):

```csharp
metersPerDegLat = 111132.92 - 559.82*Cos(2*lat0) + 1.175*Cos(4*lat0) - 0.0023*Cos(6*lat0);
metersPerDegLon = 111412.84*Cos(lat0) - 93.5*Cos(3*lat0) + 0.118*Cos(5*lat0);
lat = lat0 + northing / metersPerDegLat;
lon = lon0 + easting  / metersPerDegLon;
```

## 7. Transport: Syncthing

No code. On the in-cab device the Fields folder is a Syncthing `sendreceive` share with id
**`agopen-fields`**; on the server it's `receiveonly` at `/srv/agdata/fields`
(→ `/srv/backup/agopen-fields`) with staggered versioning (`maxAge` 365 d). Scripts:
`deploy/server/setup-fzg1.sh:69-85` (device side), `deploy/server/setup-linux-server.sh:56-66`
(server side).

For an AgOpenGPS Windows install:
- Easiest: make AgOpenGPS's Fields directory **the same folder** as the share, or add
  `C:\Users\<user>\Documents\AgOpenGPS\Fields` as another `sendreceive` share with a new id
  (e.g. `agopengps-fields`) to the media box, pointed at a second server path, and add that
  path to `ingest.py`'s `DATA_DIR` (it's one `Path` — simplest is to symlink the second tree
  into `/srv/agdata/fields/` since the globs are one level deep).
- Set `ignorePerms = true` on the share (Linux perm sync from a Windows peer made 188 field
  dirs read-only on the tablet on 2026-08-23 — that bit us).
- Don't share OneDrive-backed folders; Syncthing and OneDrive fight.

---

## 8. Server API (for checking your output)

`http://100.120.151.67:8082` (`viewer.py`):

| Endpoint | Returns |
|---|---|
| `/` | Leaflet map; product/field/date filters; popup shows product, field, job, rate, workedHa, applied, actual rate; **red outline when actual is >10 % off target** |
| `/api/fields` | FeatureCollection of outer boundaries |
| `/api/products` | distinct product names |
| `/api/applications?field=&product=&from=YYYY-MM-DD&to=YYYY-MM-DD` | FeatureCollection of records (the stored MultiPolygon + properties) |
| `/api/summary` | per-product totals: jobs, ha, applied, avgRate, first/last |
| `/api/loads` (GET/POST) | loader-scale scoop records — unrelated to this feature |

Verify a port like this: write the two files, wait ≤ 60 s after Syncthing shows "Up to
Date", then `curl "…/api/applications?field=<FieldName>"` and check the polygon count and
`workedHa`. Errors from a bad file land in `journalctl -u agdata-ingest` on the media box
(`ssh -i ~/.ssh/agpc_ed25519 keritapu@100.120.151.67`).

Date filters compare the **raw `startedAt` string** lexically (`viewer.py:53-58`), so keep the
`yyyy-MM-ddTHH:mm:ss…` ISO form; `_parse_iso` accepts an offset or `Z` and treats naive
timestamps as server-local.

---

## 9. Gotchas (each one has already cost a debugging session)

1. **`Feature`, not `FeatureCollection`**, at the top of `coverage.geojson` — but
   `field.geojson` IS a FeatureCollection.
2. **`MultiPolygon` is mandatory.** Wrap a single polygon in one more array level.
3. **`[lon, lat]` order**, 7 dp, `InvariantCulture` (a `70,5` from a de-DE culture kills
   `json.loads`).
4. **`field` must equal the folder name**, byte for byte (spaces and dates included —
   `"house n 2025-05-29"`), or the record and its boundary never line up.
5. **Upsert key is `(field, job, product)`.** Re-exporting after changing the product leaves
   the old row behind as an orphan — either keep the product fixed once driving has started,
   or delete the old `coverage.geojson` row server-side.
6. Rewrite the whole file each time; never append. Use a temp file + rename if you can
   (Syncthing may pick up a half-written file otherwise; the ingest just retries next scan,
   so this is cosmetic).
7. `workedHa` is the painted/worked area, **not** the simplified polygon's area — they differ
   by a few percent. Use AgOpenGPS's `workedAreaTotal` for parity of meaning.
8. Timestamps: `DateTime.Now.ToString("o")` → local with offset (`+12:00`). Don't emit UTC
   `Z` for some records and local for others — the lexical date filter will misorder them.
9. `appliedAmount`/`appliedUnit`/`appliedMeasured`/`actualRate` are **omitted** (not zero)
   when there's nothing to say; the server defaults them.
10. Minimum viable record for the server is: `field, job, product, rate, rateUnit, workedHa,
    startedAt` + the MultiPolygon. Everything else is decoration.

---

## 10. Suggested AgOpenGPS implementation plan (compact, names verified against 6.8.6 source)

Source tree: `SourceCode/GPS` (WinForms app, `FormGPS` partial class), `SourceCode/AgOpenGPS.Core`
(models — `LocalPlane`, `Wgs84`, `GeoCoord`; references **Newtonsoft.Json 13.0.4**, so use that).

1. **`CJob` + `job.json`** — new class in `GPS/Classes/` mirroring §4 (Newtonsoft, camelCase
   via `CamelCasePropertyNamesContractResolver`, `StringEnumConverter`). Folder
   `<field>/jobs/<task>/`. Hold it as `public CJob currentJob` on `FormGPS`.
2. **Job dialog** — 6.8.6 already has `GPS/Forms/Field/FormJob.cs` (the open/new/resume field
   chooser; it calls `mf.FileSaveEverythingBeforeClosingField()` at line 112). Extend it
   (or add a `FormJobDetails` it opens after a field is chosen) with: product picker reading
   `products-catalog.json` (§4.1), rate + unit (`kg/ha | L/ha | seeds/ha | t/ha`), work type,
   notes; and an "Applied total" prompt on close. Resume = reopen the same job folder and
   clear `endedAt`.
3. **`CCoverageExport.BuildGeoJson(...)`** — port §3.4 verbatim (StringBuilder, invariant
   culture; don't serialise via Newtonsoft for the coordinates — 7-dp formatting matters).
   Inputs: every triangle of `tool.section[i].patchList` (`CPatches.patchList`,
   `List<List<vec3>>` — each inner list is a triangle strip; triangle *k* is points
   `k, k+1, k+2`), unioned with Clipper2 (`Clipper2` NuGet, `Clipper64` at scale 100,
   `FillRule.NonZero`, `PolyTree64`, simplify 0.4 m, drop < 4 m²);
   `workedHa = fd.workedAreaTotal * glm.m2ha` (`CFieldData.cs:12`); `toolWidthM =
   tool.width`; projection `mf.AppModel.LocalPlane.ConvertGeoCoordToWgs84` (§6).
   Note the patch lists are also what `Sections.txt` is written from, so a retro-export on
   field open can rebuild from disk the same way `FileOpenField` reloads them.
4. **`field.geojson` writer** — §5, from `bnd.bndList[0].fenceLine` (`CBoundaryList.cs:9`,
   `List<vec3>` with `.heading` in radians) through the same projection; call it next to the
   existing `FileSaveBoundary()`.
5. **Hooks**: export in `FormGPS.FileSaveEverythingBeforeClosingField()`
   (`GPS/Forms/Controls.Designer.cs:641` — it is `async Task`, already awaited by every
   close path), on Applied-total entry, and from an "Export coverage" button; retro-export on
   field open if `coverage.geojson` is missing but `Sections.txt` has patches.
6. **Syncthing**: add the AOG Fields folder as a share to the media box (§7), `ignorePerms`.
7. **Verify** against `/api/applications` (§8) with a short real drive.

Things NOT to reuse: `GPS/Classes/AgShare/*` (`AgShareUploader`, `GeoConverter`) is the
cloud AgShare feature — a different server and DTOs. Its `GeoConverter.ToLocal` is the same
maths as §6 but the upload format is unrelated to ours.

Open question the owner should answer before step 2: should AgOpenGPS and AgOpenWeb share
one Fields tree (then a field worked in both apps has one `jobs/` folder — fine, the server
doesn't care which app wrote a record) or stay separate trees both synced to the server?
Either works; separate trees avoid AOG's legacy writers clobbering AgOpenWeb's geojson.
