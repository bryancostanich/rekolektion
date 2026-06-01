# Region-based Width / Spacing DRC rules — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace viz's per-polygon Width and Spacing rule dispatch with a connected-component-aware check that operates on a `Region`'s boolean union — matching Magic's tile-based semantics so the implant-close pipeline no longer produces phantom narrow strips that false-fire Width and phantom slab-pair Spacing fires inside merged regions.

**Architecture:** Add a new rule dispatch path that converts the layer's polygons to a `Region`, identifies connected components, and checks the rule against each component as a single feature. The existing per-polygon dispatch stays for backwards compatibility but the implant-aware layers (psdm, nsdm) and any layer that gets pre-merged (e.g. via `applyImplantClose`) routes through the new path. The Region module gets two new operations: `connectedComponents : Region -> Region array` (split into max-connected sub-regions) and `narrowestNeck : Region -> int64` (smallest opening of any sub-region's morphological "opening"), both reusing the existing slab-decomposed storage so we don't introduce a new geometry data model.

**Tech Stack:** F# / .NET 10, the existing `Drc.Geometry.Region` slab-decomposed data model, the existing `Drc.Check` rule-dispatch loop, xUnit + FsUnit for tests.

---

## Pre-flight

### Task 0: Branch + baseline

**Files:**
- N/A (git ops only)

- [ ] **Step 1: Confirm we're at the right commit**

Run from repo root:

```bash
git log --oneline -3
```

Expected: top commit is `1c4b8f7 viz drc: Region.toPolygons over-decomposition probe (no fix yet)` or later. If main has moved past, rebase / merge as the user directs.

- [ ] **Step 2: Capture the baseline parity numbers as truth**

Run:

```bash
cd tools/viz && dotnet test tests/Rekolektion.Viz.Core.Tests -c Release --filter "FullyQualifiedName~MagicVsViz" --logger "console;verbosity=detailed" 2>&1 | tee /tmp/baseline-parity.log
grep -E "TOTAL:" /tmp/baseline-parity.log
```

Expected output (record verbatim — every later step compares against this):

```
TOTAL: viz=14, magic=85, viz-only=0, magic-only=30   (opamp_buffer_r2r)
TOTAL: viz=37, magic=369, viz-only=4, magic-only=0   (bias_gen)
TOTAL: viz=12, magic=90, viz-only=1, magic-only=0    (b1_5_stage1)
TOTAL: viz=0, magic=0, viz-only=0, magic-only=0      (j_az_col, passes)
```

Any deviation here means the plan's acceptance criteria need updating before code work.

- [ ] **Step 3: Confirm the full Core suite is otherwise green**

Run:

```bash
cd tools/viz && dotnet test tests/Rekolektion.Viz.Core.Tests -c Release --nologo 2>&1 | tail -3
```

Expected: `Failed: 7, Passed: ~601` — the 7 failures must be exactly `MagicVsVizDrcTests` (4 cases) + `RatlinesProbe` (2 cases, pre-existing `Assert.Fail` diagnostics) + 1 other if any. If different, surface to user before proceeding.

---

## Phase A — `Region.connectedComponents`

A pure-function utility that splits a `Region` into one Region per maximal connected component. Foundation for both Width and Spacing rule rewrites.

### Task A1: Define the helper signature + first test

**Files:**
- Create: `tools/viz/src/Rekolektion.Viz.Core/Drc/Geometry/Components.fs` (add new public function; the file already exists for component-style helpers)
- Test: `tools/viz/tests/Rekolektion.Viz.Core.Tests/RegionComponentsTests.fs` (new)

- [ ] **Step 1: Write the failing test for the single-rect base case**

Create `RegionComponentsTests.fs`:

```fsharp
module Rekolektion.Viz.Core.Tests.RegionComponentsTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Drc.Geometry

[<Fact>]
let ``connectedComponents of a single rectangle returns one region`` () =
    let r =
        Region.ofRect 0L 0L 100L 50L
    let parts = Components.connectedComponents r
    parts.Length |> should equal 1
    Region.bbox parts.[0] |> should equal (Some (0L, 0L, 100L, 50L))
```

- [ ] **Step 2: Add the file to the test fsproj**

Edit `tools/viz/tests/Rekolektion.Viz.Core.Tests/Rekolektion.Viz.Core.Tests.fsproj`, add after the existing `RegionMaxRectProbeTests.fs` line:

```xml
    <Compile Include="RegionComponentsTests.fs" />
```

- [ ] **Step 3: Run the test — verify it fails**

Run:

```bash
cd tools/viz && dotnet test tests/Rekolektion.Viz.Core.Tests -c Release --filter "FullyQualifiedName~RegionComponentsTests" --nologo 2>&1 | tail -5
```

Expected: build failure (`Components` module doesn't expose `connectedComponents`).

- [ ] **Step 4: Implement the minimal stub**

Edit `tools/viz/src/Rekolektion.Viz.Core/Drc/Geometry/Components.fs`, append:

```fsharp
/// Split a Region into its maximal connected components. Two
/// `(slab, intervalIdx)` cells are connected when they share a Y
/// edge (one is directly above the other) AND their X-intervals
/// overlap, OR they are in the same slab AND adjacent intervals
/// share an X endpoint. The Region invariant (interval lists are
/// sorted, non-overlapping, non-adjacent) means same-slab intervals
/// are NEVER connected on their own — they need a bridging slab
/// above or below.
///
/// Returns one Region per component, each in canonical slab form.
let connectedComponents (r: Region.Region) : Region.Region array =
    if r.Slabs.Length = 0 then [||]
    else [| r |]   // stub: pass everything through as one component
```

- [ ] **Step 5: Run the test — verify it passes**

Run:

```bash
cd tools/viz && dotnet test tests/Rekolektion.Viz.Core.Tests -c Release --filter "FullyQualifiedName~RegionComponentsTests" --nologo 2>&1 | tail -5
```

Expected: PASS. The stub is OK for the single-rect case.

- [ ] **Step 6: Commit**

```bash
git add tools/viz/src/Rekolektion.Viz.Core/Drc/Geometry/Components.fs tools/viz/tests/Rekolektion.Viz.Core.Tests/RegionComponentsTests.fs tools/viz/tests/Rekolektion.Viz.Core.Tests/Rekolektion.Viz.Core.Tests.fsproj
git commit -m "viz drc: Region.connectedComponents stub + first test (single rect)"
```

### Task A2: Two disjoint rects → two components

**Files:**
- Modify: `tools/viz/tests/Rekolektion.Viz.Core.Tests/RegionComponentsTests.fs`
- Modify: `tools/viz/src/Rekolektion.Viz.Core/Drc/Geometry/Components.fs`

- [ ] **Step 1: Add the failing test**

Append to `RegionComponentsTests.fs`:

```fsharp
[<Fact>]
let ``connectedComponents of two disjoint rectangles returns two regions`` () =
    let r =
        Boolean.union
            (Region.ofRect 0L 0L 100L 50L)
            (Region.ofRect 200L 0L 300L 50L)
    let parts = Components.connectedComponents r |> Array.sortBy (fun p ->
        match Region.bbox p with
        | Some (x1, _, _, _) -> x1
        | None -> 0L)
    parts.Length |> should equal 2
    Region.bbox parts.[0] |> should equal (Some (0L, 0L, 100L, 50L))
    Region.bbox parts.[1] |> should equal (Some (200L, 0L, 300L, 50L))
```

- [ ] **Step 2: Run — verify it fails**

```bash
cd tools/viz && dotnet test tests/Rekolektion.Viz.Core.Tests -c Release --filter "FullyQualifiedName~RegionComponentsTests" --nologo 2>&1 | tail -5
```

Expected: FAIL on the new test (`parts.Length` is 1 from the stub, expected 2).

- [ ] **Step 3: Implement the DSU-over-slab-intervals**

Replace the stub in `Components.fs` with the real implementation:

```fsharp
let connectedComponents (r: Region.Region) : Region.Region array =
    if r.Slabs.Length = 0 then [||]
    else
    let slabs = r.Slabs
    // Per-slab flat index for each interval.
    let perSlabOffsets = Array.zeroCreate<int> slabs.Length
    let mutable total = 0
    for i in 0 .. slabs.Length - 1 do
        perSlabOffsets.[i] <- total
        total <- total + slabs.[i].Intervals.Length
    let n = total
    if n = 0 then [||]
    else
    let parent = Array.init n id
    let rec find x =
        if parent.[x] = x then x
        else
            let r = find parent.[x]
            parent.[x] <- r
            r
    let union a b =
        let ra = find a
        let rb = find b
        if ra <> rb then parent.[ra] <- rb
    // Vertical adjacency: directly butted slabs whose intervals
    // overlap in X are connected.
    for sIdx in 0 .. slabs.Length - 2 do
        let s = slabs.[sIdx]
        let t = slabs.[sIdx + 1]
        if s.Y + s.Height = t.Y then
            for i in 0 .. s.Intervals.Length - 1 do
                let si = s.Intervals.[i]
                let sFlat = perSlabOffsets.[sIdx] + i
                for j in 0 .. t.Intervals.Length - 1 do
                    let tj = t.Intervals.[j]
                    if si.X1 < tj.X2 && tj.X1 < si.X2 then
                        union sFlat (perSlabOffsets.[sIdx + 1] + j)
    // Group (slab, intervalIdx) pairs by component root and rebuild
    // one Region per group via Boolean.union of one-interval slabs.
    let groups =
        System.Collections.Generic.Dictionary<int,
            ResizeArray<int * int>>()
    for sIdx in 0 .. slabs.Length - 1 do
        let s = slabs.[sIdx]
        for i in 0 .. s.Intervals.Length - 1 do
            let root = find (perSlabOffsets.[sIdx] + i)
            match groups.TryGetValue root with
            | true, list -> list.Add (sIdx, i)
            | _ ->
                let list = ResizeArray<int * int>()
                list.Add (sIdx, i)
                groups.[root] <- list
    let result = ResizeArray<Region.Region>()
    for kv in groups do
        let cells = kv.Value
        // Build a Region from this component's cells.
        let cellRegions =
            cells
            |> Seq.map (fun (sIdx, i) ->
                let s = slabs.[sIdx]
                let iv = s.Intervals.[i]
                Region.ofRect iv.X1 s.Y iv.X2 (s.Y + s.Height))
        let merged =
            cellRegions
            |> Seq.fold Boolean.union Region.empty
        result.Add merged
    result.ToArray()
```

- [ ] **Step 4: Run — verify both tests pass**

```bash
cd tools/viz && dotnet test tests/Rekolektion.Viz.Core.Tests -c Release --filter "FullyQualifiedName~RegionComponentsTests" --nologo 2>&1 | tail -5
```

Expected: 2 passing.

- [ ] **Step 5: Commit**

```bash
git add tools/viz/src/Rekolektion.Viz.Core/Drc/Geometry/Components.fs tools/viz/tests/Rekolektion.Viz.Core.Tests/RegionComponentsTests.fs
git commit -m "viz drc: Region.connectedComponents real DSU implementation + disjoint-pair test"
```

### Task A3: Bridge + nested + L-shape cases

**Files:**
- Modify: `tools/viz/tests/Rekolektion.Viz.Core.Tests/RegionComponentsTests.fs`

- [ ] **Step 1: Add three more tests**

Append:

```fsharp
[<Fact>]
let ``connectedComponents of an L-shape returns one region`` () =
    let r =
        Boolean.union
            (Region.ofRect 0L 0L 200L 100L)
            (Region.ofRect 0L 0L 100L 500L)
    let parts = Components.connectedComponents r
    parts.Length |> should equal 1

[<Fact>]
let ``connectedComponents of П-bridged shape returns one region`` () =
    // Two vertical arms joined at top by a horizontal bar.
    let r =
        Region.empty
        |> Boolean.union (Region.ofRect 0L 0L 50L 500L)       // left arm
        |> Boolean.union (Region.ofRect 150L 0L 200L 500L)    // right arm
        |> Boolean.union (Region.ofRect 0L 450L 200L 500L)    // top bar
    let parts = Components.connectedComponents r
    parts.Length |> should equal 1

[<Fact>]
let ``connectedComponents of corner-touching rectangles returns two regions`` () =
    // 4-connectivity (Magic-compatible): pure corner touch
    // leaves the rectangles as separate components.
    let r =
        Boolean.union
            (Region.ofRect 0L 0L 100L 100L)
            (Region.ofRect 100L 100L 200L 200L)
    let parts = Components.connectedComponents r
    parts.Length |> should equal 2
```

- [ ] **Step 2: Run — verify all 5 pass**

```bash
cd tools/viz && dotnet test tests/Rekolektion.Viz.Core.Tests -c Release --filter "FullyQualifiedName~RegionComponentsTests" --nologo 2>&1 | tail -5
```

Expected: 5 passing. If the П-bridged or corner-touch case fails, the union semantics in Region.fs (touching intervals stay separate per the `mergeSortedIntervals` comment) are at play; debug by dumping `r.Slabs` and tracing the DSU.

- [ ] **Step 3: Commit**

```bash
git add tools/viz/tests/Rekolektion.Viz.Core.Tests/RegionComponentsTests.fs
git commit -m "viz drc: connectedComponents tests — L-shape, П-bridge, corner-touch"
```

### Task A4: Round-trip against bias_gen post-close

**Files:**
- Modify: `tools/viz/tests/Rekolektion.Viz.Core.Tests/RegionMaxRectProbeTests.fs` (add a regression assertion to the existing probe)

- [ ] **Step 1: Add a count assertion that the bias_gen post-close has FAR fewer components than slab polygons**

Edit `RegionMaxRectProbeTests.fs`, find the test ``run applyImplantClose equivalent on bias_gen PSDM, dump output near viz-only psdm.2`` and at the end add:

```fsharp
        let components =
            Components.connectedComponents closed
        out.WriteLine (sprintf
            "connected components in close-merged PSDM: %d" components.Length)
        Assert.True (components.Length < 30,
            sprintf "expected < 30 components (one per merged region), got %d"
                components.Length)
```

- [ ] **Step 2: Add `open Rekolektion.Viz.Core.Drc.Geometry` to the file's opens (if not already present)**

Verify the file has `open Rekolektion.Viz.Core.Drc.Geometry` near the top (it already imports `Region` so likely yes). If missing, add it.

- [ ] **Step 3: Run — verify the assertion passes**

```bash
cd tools/viz && dotnet test tests/Rekolektion.Viz.Core.Tests -c Release --filter "FullyQualifiedName~RegionMaxRectProbe" --logger "console;verbosity=detailed" --nologo 2>&1 | grep -E "components|slabs"
```

Expected: the dump line shows e.g. `connected components in close-merged PSDM: 6` (a count between 1 and 28 — the number of merged regions in bias_gen's PSDM).

- [ ] **Step 4: Commit**

```bash
git add tools/viz/tests/Rekolektion.Viz.Core.Tests/RegionMaxRectProbeTests.fs
git commit -m "viz drc: connectedComponents regression assertion on bias_gen post-close"
```

---

## Phase B — Region-based Width rule

Reuse `connectedComponents` to check Width against each merged feature as a unit. The narrow-neck of an L-shape still fires; phantom slab strips do not.

### Task B1: Define the narrowest-neck primitive

**Files:**
- Modify: `tools/viz/src/Rekolektion.Viz.Core/Drc/Geometry/Components.fs`
- Test: `tools/viz/tests/Rekolektion.Viz.Core.Tests/RegionNarrowestNeckTests.fs` (new)

- [ ] **Step 1: Write the failing test for a single rectangle**

Create `RegionNarrowestNeckTests.fs`:

```fsharp
module Rekolektion.Viz.Core.Tests.RegionNarrowestNeckTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Drc.Geometry

[<Fact>]
let ``narrowestNeck of single 100x500 rectangle is 100`` () =
    let r = Region.ofRect 0L 0L 100L 500L
    Components.narrowestNeck r |> should equal 100L

[<Fact>]
let ``narrowestNeck of L-shape returns the narrow arm width`` () =
    // Bottom horizontal arm 200x100; vertical arm 100x500.
    // Narrow dimension of either arm = 100.
    let r =
        Boolean.union
            (Region.ofRect 0L 0L 200L 100L)
            (Region.ofRect 0L 0L 100L 500L)
    Components.narrowestNeck r |> should equal 100L

[<Fact>]
let ``narrowestNeck of П-bridge returns the arm width`` () =
    let r =
        Region.empty
        |> Boolean.union (Region.ofRect 0L 0L 50L 500L)
        |> Boolean.union (Region.ofRect 150L 0L 200L 500L)
        |> Boolean.union (Region.ofRect 0L 450L 200L 500L)
    // Narrow dimension of the vertical arms = 50 (top bar is 50 tall).
    Components.narrowestNeck r |> should equal 50L
```

Register the file in the test fsproj after `RegionComponentsTests.fs`.

- [ ] **Step 2: Run — verify build fails on missing `narrowestNeck`**

```bash
cd tools/viz && dotnet test tests/Rekolektion.Viz.Core.Tests -c Release --filter "FullyQualifiedName~RegionNarrowestNeck" --nologo 2>&1 | tail -5
```

Expected: build error.

- [ ] **Step 3: Implement `narrowestNeck`**

Append to `Components.fs`:

```fsharp
/// Smallest min(width, height) of any slab interval in the region.
/// For an L-shape or П-shape with a 100 nm-thick arm, this returns
/// 100 — Magic's region-based Width check measures the same thing
/// (the minimum over the region's edge-pair distances). Returns
/// Int64.MaxValue for an empty region.
let narrowestNeck (r: Region.Region) : int64 =
    let mutable best = System.Int64.MaxValue
    for slab in r.Slabs do
        let h = slab.Height
        for iv in slab.Intervals do
            let w = iv.X2 - iv.X1
            let m = if w < h then w else h
            if m < best then best <- m
    best
```

- [ ] **Step 4: Run — verify all 3 pass**

```bash
cd tools/viz && dotnet test tests/Rekolektion.Viz.Core.Tests -c Release --filter "FullyQualifiedName~RegionNarrowestNeck" --nologo 2>&1 | tail -5
```

Expected: 3 passing.

- [ ] **Step 5: Commit**

```bash
git add tools/viz/src/Rekolektion.Viz.Core/Drc/Geometry/Components.fs tools/viz/tests/Rekolektion.Viz.Core.Tests/RegionNarrowestNeckTests.fs tools/viz/tests/Rekolektion.Viz.Core.Tests/Rekolektion.Viz.Core.Tests.fsproj
git commit -m "viz drc: Region.narrowestNeck for region-based Width rule"
```

### Task B2: Wire Width rule to use Region for implant layers

**Files:**
- Modify: `tools/viz/src/Rekolektion.Viz.Core/Drc/Check.fs:274-340` (the existing `Rules.Width` dispatch branch)

- [ ] **Step 1: Identify the existing dispatch block**

In `Check.fs`, locate the `| Rules.Width (name, layer, minUm) ->` case. Read lines 274-340 to refresh on the existing per-polygon + merge-coverage waiver logic.

- [ ] **Step 2: Add a region-based path for psdm / nsdm specifically**

Replace the existing case body with the dual-path version. The existing per-polygon path stays for non-implant layers; psdm (94/20) and nsdm (93/44) route through the region-based path:

```fsharp
        | Rules.Width (name, layer, minUm) ->
            let limit = umToDbu umPerDbu minUm
            if limit > 0L then
                let isImplant =
                    (layer.Number = 94 && layer.DataType = 20)
                    || (layer.Number = 93 && layer.DataType = 44)
                if isImplant then
                    // Region-based: collect ALL polygons on this
                    // layer (originals only — close output stays
                    // for Enclosure rules elsewhere), build one
                    // Region, split into connected components, and
                    // fire one violation per component whose
                    // narrowest neck is sub-spec. This matches
                    // Magic's tile-based view: a merged feature
                    // with one narrow neck fires once at that neck,
                    // not once per slab.
                    let polys =
                        polysOnLayer idx layer
                        |> Array.choose (fun (p, _, _) ->
                            if p.SourceStructure <> "drc-implant-closed"
                            then Some p else None)
                    if polys.Length > 0 then
                        let region = Geometry.Region.ofPolygons polys
                        let components = Geometry.Components.connectedComponents region
                        for c in components do
                            let neck = Geometry.Components.narrowestNeck c
                            if neck < limit then
                                match Geometry.Region.bbox c with
                                | Some (x1, y1, x2, y2) ->
                                    result.Add {
                                        Rule = name
                                        LayerNumber = layer.Number
                                        LayerType   = layer.DataType
                                        LimitDbu    = limit
                                        MeasuredDbu = neck
                                        BboxA = (x1, y1, x2, y2)
                                        BboxB = None }
                                | None -> ()
                else
                    // Non-implant layers: existing per-polygon
                    // dispatch with merge-coverage waiver — keep
                    // verbatim to avoid regressing j_az_col and
                    // similar layouts that rely on the polygon-
                    // level check.
                    let polys = polysOnLayer idx layer
                    // ... rest of original case body
                    for i in 0 .. polys.Length - 1 do
                        // [keep the existing for-loop verbatim]
                        ...
```

(Important: preserve the existing per-polygon for-loop body for the non-implant branch. The placeholder above is the structure; copy the actual 60-line body in place.)

- [ ] **Step 3: Build and run the bias_gen parity test only**

```bash
cd tools/viz && dotnet test tests/Rekolektion.Viz.Core.Tests -c Release --filter "FullyQualifiedName~bias_gen" --logger "console;verbosity=detailed" --nologo 2>&1 | grep -E "TOTAL:|viz\[(nsdm|psdm)"
```

Expected: `TOTAL: viz=N, magic=369, viz-only=2, magic-only=0` (was viz-only=4). The nsdm.1 + psdm.1 width false positives should be gone (count would go 4 → 2).

If viz-only=3 or 4: the dispatch didn't enter the implant branch, or the close output is still leaking in. Re-check that `applyImplantClose` doesn't tag originals as `drc-implant-closed`.

- [ ] **Step 4: Run b1_5_stage1 + j_az_col + opamp to check no regressions**

```bash
cd tools/viz && dotnet test tests/Rekolektion.Viz.Core.Tests -c Release --filter "FullyQualifiedName~MagicVsViz" --nologo 2>&1 | grep -E "TOTAL:|Passed|Failed!"
```

Expected:
- `opamp`: viz-only=0, magic-only=30 (unchanged — Width on non-implant layers)
- `bias_gen`: viz-only=2, magic-only=0 (closed 2 of 4)
- `b1_5_stage1`: viz-only=1, magic-only=0 (unchanged — its viz-only is met1.5, not psdm/nsdm)
- `j_az_col`: viz-only=0, magic-only=0 (must not regress)

If j_az_col regresses, the implant-branch is now firing on non-implant polygons that happened to land on psdm/nsdm layer — debug by dumping `polys.Length` for j_az_col.

- [ ] **Step 5: Commit**

```bash
git add tools/viz/src/Rekolektion.Viz.Core/Drc/Check.fs
git commit -m "viz drc: Width rule on Region-components for psdm / nsdm"
```

### Task B3: Extend region-based Width to all layers that pre-merge

**Files:**
- Modify: `tools/viz/src/Rekolektion.Viz.Core/Drc/Check.fs` (the `isImplant` check just added)

- [ ] **Step 1: Replace the hard-coded implant-layer test with a policy**

Locate the `let isImplant = ...` line. Replace with:

```fsharp
                // Layers where the source layout commonly authors
                // multiple abutting rectangles for one logical
                // feature (and Magic's region view sees one tile).
                // Width false-fires on rectangles that are part of
                // these merged features — route through the region-
                // based check instead of per-polygon.
                let isRegionLayer =
                    let k = (layer.Number, layer.DataType)
                    k = (94, 20)        // psdm
                    || k = (93, 44)     // nsdm
                    || k = (66, 13)     // polyres
```

(polyres is added because the opamp rpm.1 magic-only edge case — the one Magic finds inside an L-shape — is on layer 66/13.)

- [ ] **Step 2: Run opamp parity, expect rpm.1 magic-only to drop**

```bash
cd tools/viz && dotnet test tests/Rekolektion.Viz.Core.Tests -c Release --filter "FullyQualifiedName~opamp_buffer_r2r" --logger "console;verbosity=detailed" --nologo 2>&1 | grep -E "TOTAL:|rpm.1"
```

Expected: `viz[rpm.1] = 3` (was 2) and the magic-only count for rpm.1 drops to 0. opamp `magic-only` should be 29 (was 30).

- [ ] **Step 3: Run all four parity cases, confirm no regressions**

```bash
cd tools/viz && dotnet test tests/Rekolektion.Viz.Core.Tests -c Release --filter "FullyQualifiedName~MagicVsViz" --nologo 2>&1 | grep -E "TOTAL:"
```

Expected:
- opamp: viz-only=0, magic-only=29
- bias_gen: viz-only=2, magic-only=0
- b1_5_stage1: viz-only=1, magic-only=0
- j_az_col: viz-only=0, magic-only=0

- [ ] **Step 4: Commit**

```bash
git add tools/viz/src/Rekolektion.Viz.Core/Drc/Check.fs
git commit -m "viz drc: route polyres (66/13) Width through region-based check"
```

---

## Phase C — Region-based Spacing rule

The bias_gen viz-only psdm.2 fires + the b1_5_stage1 met1.5 fire all come from the same class: phantom sub-spec gaps between slab decompositions of one merged region. Spacing-on-region fixes them.

### Task C1: Define the `smallestInterComponentGap` primitive

**Files:**
- Modify: `tools/viz/src/Rekolektion.Viz.Core/Drc/Geometry/Components.fs`
- Test: `tools/viz/tests/Rekolektion.Viz.Core.Tests/RegionSpacingTests.fs` (new)

- [ ] **Step 1: Write the failing test**

Create `RegionSpacingTests.fs`:

```fsharp
module Rekolektion.Viz.Core.Tests.RegionSpacingTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Drc.Geometry

[<Fact>]
let ``smallestInterComponentGap of two rects 200 nm apart returns 200`` () =
    let r =
        Boolean.union
            (Region.ofRect 0L 0L 100L 100L)
            (Region.ofRect 300L 0L 400L 100L)
    Components.smallestInterComponentGap r |> should equal 200L

[<Fact>]
let ``smallestInterComponentGap of merged П-shape returns Int64.MaxValue`` () =
    // All slabs are part of one component — no inter-component gap.
    let r =
        Region.empty
        |> Boolean.union (Region.ofRect 0L 0L 50L 500L)
        |> Boolean.union (Region.ofRect 150L 0L 200L 500L)
        |> Boolean.union (Region.ofRect 0L 450L 200L 500L)
    Components.smallestInterComponentGap r |> should equal System.Int64.MaxValue

[<Fact>]
let ``smallestInterComponentGap of three rects 100 and 200 apart returns 100`` () =
    let r =
        Region.empty
        |> Boolean.union (Region.ofRect 0L 0L 100L 100L)
        |> Boolean.union (Region.ofRect 200L 0L 300L 100L)
        |> Boolean.union (Region.ofRect 500L 0L 600L 100L)
    Components.smallestInterComponentGap r |> should equal 100L
```

Register in fsproj after `RegionNarrowestNeckTests.fs`.

- [ ] **Step 2: Run — verify build fails**

```bash
cd tools/viz && dotnet test tests/Rekolektion.Viz.Core.Tests -c Release --filter "FullyQualifiedName~RegionSpacingTests" --nologo 2>&1 | tail -5
```

Expected: build error on missing `smallestInterComponentGap`.

- [ ] **Step 3: Implement**

Append to `Components.fs`:

```fsharp
/// Smallest orthogonal bbox-to-bbox gap between any two distinct
/// connected components of the region. Reuses the same metric
/// `Drc.Check.bboxOrthoGapAndRegion` uses: only Manhattan-facing
/// edge pairs count, diagonal pairs return Int64.MaxValue. Returns
/// Int64.MaxValue when there's only one component (no inter-
/// component gap exists).
let smallestInterComponentGap (r: Region.Region) : int64 =
    let components = connectedComponents r
    if components.Length < 2 then System.Int64.MaxValue
    else
    let bboxes =
        components
        |> Array.choose Region.bbox
    if bboxes.Length < 2 then System.Int64.MaxValue
    else
    let mutable best = System.Int64.MaxValue
    for i in 0 .. bboxes.Length - 1 do
        let (ax1, ay1, ax2, ay2) = bboxes.[i]
        for j in i + 1 .. bboxes.Length - 1 do
            let (bx1, by1, bx2, by2) = bboxes.[j]
            let xOverlap = (min ax2 bx2) > (max ax1 bx1)
            let yOverlap = (min ay2 by2) > (max ay1 by1)
            let g =
                if xOverlap then
                    if ay2 <= by1 then by1 - ay2
                    elif by2 <= ay1 then ay1 - by2
                    else 0L
                elif yOverlap then
                    if ax2 <= bx1 then bx1 - ax2
                    elif bx2 <= ax1 then ax1 - bx2
                    else 0L
                else System.Int64.MaxValue
            if g > 0L && g < best then best <- g
    best
```

- [ ] **Step 4: Run — verify all 3 tests pass**

```bash
cd tools/viz && dotnet test tests/Rekolektion.Viz.Core.Tests -c Release --filter "FullyQualifiedName~RegionSpacingTests" --nologo 2>&1 | tail -5
```

Expected: 3 passing.

- [ ] **Step 5: Commit**

```bash
git add tools/viz/src/Rekolektion.Viz.Core/Drc/Geometry/Components.fs tools/viz/tests/Rekolektion.Viz.Core.Tests/RegionSpacingTests.fs tools/viz/tests/Rekolektion.Viz.Core.Tests/Rekolektion.Viz.Core.Tests.fsproj
git commit -m "viz drc: Components.smallestInterComponentGap for region-based Spacing"
```

### Task C2: Wire Spacing rule to region-components for the same layers as Width

**Files:**
- Modify: `tools/viz/src/Rekolektion.Viz.Core/Drc/Check.fs` (the `Rules.Spacing` case)

- [ ] **Step 1: Add the region-based path mirroring Task B2's structure**

Locate the `| Rules.Spacing (name, layer, minUm) ->` case. At the top of its `if limit > 0L then` block, branch on `isRegionLayer` (extract the helper from Width so both rules share it — put a `let isRegionLayer = ...` at the top of `checkRule` to avoid duplication):

```fsharp
        | Rules.Spacing (name, layer, minUm) ->
            let limit = umToDbu umPerDbu minUm
            if limit > 0L then
                if isRegionLayer layer then
                    let polys =
                        polysOnLayer idx layer
                        |> Array.choose (fun (p, _, _) ->
                            if p.SourceStructure <> "drc-implant-closed"
                            then Some p else None)
                    if polys.Length > 0 then
                        let region = Geometry.Region.ofPolygons polys
                        let components = Geometry.Components.connectedComponents region
                        // For each pair of distinct components,
                        // check sub-spec gap. Magic merges any pair
                        // that the implant-close would have bridged,
                        // so we filter to components that survived
                        // the close — i.e. the gap is genuinely real.
                        let bboxes =
                            components
                            |> Array.choose Geometry.Region.bbox
                        for i in 0 .. bboxes.Length - 1 do
                            for j in i + 1 .. bboxes.Length - 1 do
                                let bbA = bboxes.[i]
                                let bbB = bboxes.[j]
                                match bboxOrthoGapAndRegion bbA bbB with
                                | Some (g, gapBb) when g > 0L && g < limit ->
                                    result.Add {
                                        Rule = name
                                        LayerNumber = layer.Number
                                        LayerType   = layer.DataType
                                        LimitDbu    = limit
                                        MeasuredDbu = g
                                        BboxA = gapBb
                                        BboxB = None }
                                | _ -> ()
                else
                    // Non-region-layer: existing per-polygon dispatch
                    // with the containment check. Keep verbatim.
                    ...
```

(Copy the actual existing per-polygon body in place of the `...`.)

- [ ] **Step 2: Refactor — hoist `isRegionLayer` to a local at the top of `checkRule`**

Edit the `let checkRule (rule: Rules.Rule) = ...` opening. Just after `let ruleName = ...`, add:

```fsharp
        let isRegionLayer (layer: Rules.LayerKey) =
            let k = (layer.Number, layer.DataType)
            k = (94, 20)        // psdm
            || k = (93, 44)     // nsdm
            || k = (66, 13)     // polyres
```

Then both the `Width` and `Spacing` branches use `isRegionLayer layer` directly.

- [ ] **Step 3: Run all four parity cases**

```bash
cd tools/viz && dotnet test tests/Rekolektion.Viz.Core.Tests -c Release --filter "FullyQualifiedName~MagicVsViz" --nologo 2>&1 | grep -E "TOTAL:"
```

Expected:
- opamp: viz-only=0, magic-only=29 (rpm.1 already closed in Phase B; nothing more from C)
- bias_gen: viz-only=0, magic-only=0 (both psdm.2 viz-only items closed — Magic merges them via implant-close so they're one component in viz too)
- b1_5_stage1: viz-only=0 if the met1.5 issue was also region-class, magic-only=0
- j_az_col: viz-only=0, magic-only=0

If b1_5_stage1 viz-only stays at 1: the met1.5 issue is a different class (asymmetric enclosure on met1↔mcon, not a Width/Spacing merger). Move it to Phase D.

- [ ] **Step 4: Commit**

```bash
git add tools/viz/src/Rekolektion.Viz.Core/Drc/Check.fs
git commit -m "viz drc: Spacing rule on Region-components for psdm / nsdm / polyres"
```

---

## Phase D — Verification + regression sweep

### Task D1: Full Core suite green

**Files:**
- N/A (test run only)

- [ ] **Step 1: Run the full Core suite**

```bash
cd tools/viz && dotnet test tests/Rekolektion.Viz.Core.Tests -c Release --nologo 2>&1 | tail -5
```

Expected: `Passed: N+1` where `N+1` is the baseline pass count plus the new tests added in Phases A-C, with the same 2 pre-existing `RatlinesProbe` failures and 0-N MagicVsViz failures (depends on how many subsystems were closed).

If any test failed that was previously passing, surface the failure and triage before committing further.

- [ ] **Step 2: Run the App + Render + Mcp suites (no DRC code changes there, but the rule-dispatch lives in Core which they reference)**

```bash
cd tools/viz && dotnet test tests/Rekolektion.Viz.App.Tests -c Release --nologo 2>&1 | tail -3
cd tools/viz && dotnet test tests/Rekolektion.Viz.Render.Tests -c Release --nologo 2>&1 | tail -3
cd tools/viz && dotnet test tests/Rekolektion.Viz.Mcp.Tests -c Release --nologo 2>&1 | tail -3
```

Expected: all three projects pass (App ~140, Render 14, Mcp 4).

### Task D2: Regenerate bundled `sky130.yaml` (Width / Spacing rule kinds didn't change; this is a sanity check)

**Files:**
- N/A (script run)

- [ ] **Step 1: Run the regenerator**

```bash
cd tools/viz && dotnet fsi scripts/dump_drc_yaml.fsx 2>&1 | tail -3
```

Expected: `Wrote N rules to ... sky130.yaml`. The byte count and rule count should match the pre-refactor values — we didn't add or remove any rule entries, only changed dispatch.

- [ ] **Step 2: Confirm `git status` shows no change to the YAML (or only a deterministic no-op change)**

```bash
git diff --stat tools/viz/drc/base/sky130.yaml
```

Expected: no changes, or a single-line whitespace shuffle. If meaningful changes appear, the dispatch code unintentionally affected serialization — re-investigate.

### Task D3: Update the regression test that pins the bias_gen + b1_5 + opamp counts

**Files:**
- Modify: `tools/viz/tests/Rekolektion.Viz.Core.Tests/MagicVsVizDrcTests.fs`

- [ ] **Step 1: Each of the four `MagicVsVizDrc` cases asserts `vizOnly = 0 && magicOnly = 0` already**

If Phases A-C closed bias_gen to 0/0 and b1_5 to 0/0, those tests will start passing without further edit. opamp will still fail because of the 28 LU tiles — that's expected and left for the LU plan.

- [ ] **Step 2: Run the four parity cases to confirm the new green state**

```bash
cd tools/viz && dotnet test tests/Rekolektion.Viz.Core.Tests -c Release --filter "FullyQualifiedName~MagicVsViz" --nologo 2>&1 | tail -5
```

Expected:
- `Passed: 3, Failed: 1` (only opamp_buffer_r2r still failing, due to the 28 LU magic-only tiles)
- OR `Passed: 4, Failed: 0` if a Magic-deck-version difference also picked up the LU tiles as out-of-scope

### Task D4: Final commit

**Files:**
- N/A

- [ ] **Step 1: Tag the end of the Region-based-rules milestone**

```bash
git log --oneline -10
```

Confirm the commit sequence from this plan is clean: Task A1 → A2 → A3 → A4 → B1 → B2 → B3 → C1 → C2.

- [ ] **Step 2: Update the spec/plan files' status header (if any) to reflect "implemented"**

Edit the existing spec for the DRC parity work (in `docs/superpowers/specs/` if one exists from earlier sessions) to add an "Implementation status" line pointing at this plan and the resulting commits.

---

## Out of scope — separate plans needed

Each item below has its own multi-day scope and should be a separate plan when prioritized:

- **LU.2 / LU.3 latch-up rules** (28 magic-only tiles on opamp). Requires reading Magic's `drc(full)` deck to reverse-engineer the actual algorithm — bbox-Euclidean distance was probed and confirmed not to match. Likely involves the per-well/substrate region context plus a "metal-connected tap" net-trace. Plan should start with a Magic-deck-source-reading research task before any code.

- **`nwell.4` connectivity** (1-2 magic-only tiles). Requires a net-tracing pass: for each nwell, follow contacts → li1 → met1 to confirm at least one n-tap is wired to a VDD-class net. The geometry containment check alone catches most cases; the connectivity check is what makes it production-grade. Plan should add a `Net.Connectivity` module first.

- **`licon.9` composite sliver atomization** (2 magic-only tiles). Magic atomizes the composite violation tile into ~30 nm vertical slivers per facing-edge segment; viz emits one violation per polygon pair, so the bbox-with-slop pairing in `MagicVsVizDrcTests` misses the slivers. Plan should either (a) emit per-segment violations in viz to match Magic's atomization, or (b) widen the pairing slop in the test framework with a per-rule policy.

## Self-review

**Spec coverage:** The implicit spec is "close the bias_gen + b1_5 viz-only items and the opamp rpm.1 magic-only without regressing j_az_col or other tests." Each of those acceptance criteria has at least one task that closes it (B2 → bias_gen Width, B3 → opamp rpm.1, C2 → bias_gen Spacing + likely b1_5 met1.5). LU.2/LU.3/nwell.4/licon.9 are explicitly out of scope.

**Placeholder scan:** Tasks B2 and C2 contain `...` placeholders for "copy the existing for-loop body verbatim" — those are intentional callouts to preserve existing code, not absent content. Every other task has its full code block.

**Type consistency:** `connectedComponents : Region -> Region array` and `narrowestNeck : Region -> int64` and `smallestInterComponentGap : Region -> int64` are defined in A1, B1, C1 respectively and used in B2, B3, C2. Names match across tasks.

**Scope:** Three of the four out-of-scope items (LU, nwell.4, licon.9) need separate plans — flagged at the bottom. The fourth, rpm.1, is closed inline by Task B3.
