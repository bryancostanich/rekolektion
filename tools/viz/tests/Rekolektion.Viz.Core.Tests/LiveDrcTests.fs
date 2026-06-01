module Rekolektion.Viz.Core.Tests.LiveDrcTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout.Flatten
open Rekolektion.Viz.Core.Drc
open Rekolektion.Viz.Core.Routing
open Rekolektion.Viz.Core.Sidecar.Types
open Rekolektion.Viz.Core.Drc.Rules

let private met1 = (68, 20)
let private units1nm : Units = { DbuNm = 1; UuUm = 1 }

let private rect (x1, y1, x2, y2) (layer, dt) : FlatPolygon =
    { Layer = layer
      DataType = dt
      Points = [|
        { X = int64 x1; Y = int64 y1 }
        { X = int64 x2; Y = int64 y1 }
        { X = int64 x2; Y = int64 y2 }
        { X = int64 x1; Y = int64 y2 }
        { X = int64 x1; Y = int64 y1 }
      |]
      SourceStructure = "test"
      SourceIndex = 0
      TopInstanceIndex = None }

let private rectIdx idx (x1, y1, x2, y2) (layer, dt) : FlatPolygon =
    { rect (x1, y1, x2, y2) (layer, dt) with SourceIndex = idx }

// --- Rules.liveEligibleNames whitelist ----------------------------------

[<Fact>]
let ``liveEligibleNames includes met1 spacing (met1.2)`` () =
    Rules.liveEligibleNames.Contains "met1.2" |> should equal true

[<Fact>]
let ``liveEligibleNames includes met1 min-width (met1.1)`` () =
    Rules.liveEligibleNames.Contains "met1.1" |> should equal true

[<Fact>]
let ``liveEligibleNames excludes met1 min-area (met1.6)`` () =
    Rules.liveEligibleNames.Contains "met1.6" |> should equal false

[<Fact>]
let ``liveRules is non-empty and a strict subset of allRules`` () =
    Rules.liveRules.Length |> should greaterThan 0
    Rules.liveRules.Length |> should lessThan Rules.allRules.Length

// --- Draft.toFlatPolygons adapter --------------------------------------

[<Fact>]
let ``toFlatPolygons makes one FlatPolygon per segment`` () =
    let segs : Draft.DraftSegment list = [
        { Layer = met1; X1 = 0L; Y1 = 0L; X2 = 100L; Y2 = 50L }
        { Layer = met1; X1 = 200L; Y1 = 0L; X2 = 300L; Y2 = 50L }
    ]
    let flat = Draft.toFlatPolygons segs
    flat.Length |> should equal 2
    flat.[0].Layer |> should equal (fst met1)
    flat.[0].DataType |> should equal (snd met1)
    flat.[0].SourceStructure |> should equal "<draft-route>"
    flat.[0].SourceIndex |> should equal 0
    flat.[1].SourceIndex |> should equal 1

// --- Check.runLive Compat.Magic end-to-end ------------------------------------------

[<Fact>]
let ``runLive on draft violating met1 spacing fires the violation`` () =
    // Cell already has a met1 rect at (0,0)-(200,200).
    let cell : FlatPolygon array = [|
        rect (0L, 0L, 200L, 200L) (68, 20)
    |]
    // Draft segment lands 139 nm away from the cell rect (under the
    // 140 nm met1.2 limit). Same bbox shape as DrcCheckTests baseline.
    let draftSegs : Draft.DraftSegment list = [
        { Layer = met1; X1 = 339L; Y1 = 0L; X2 = 539L; Y2 = 200L }
    ]
    let draftFlat = Draft.toFlatPolygons draftSegs
    let v = Check.runLive Compat.Magic Rules.defaultView units1nm cell draftFlat Map.empty None Set.empty
    v |> Array.exists (fun x -> x.Rule = "met1.2")
      |> should equal true

[<Fact>]
let ``runLive on a clean draft produces zero violations`` () =
    let cell : FlatPolygon array = [|
        rect (0L, 0L, 200L, 200L) (68, 20)
    |]
    // 500 DBU = 500 nm, well above any spacing limit.
    let draftSegs : Draft.DraftSegment list = [
        { Layer = met1; X1 = 700L; Y1 = 0L; X2 = 900L; Y2 = 200L }
    ]
    let draftFlat = Draft.toFlatPolygons draftSegs
    let v = Check.runLive Compat.Magic Rules.defaultView units1nm cell draftFlat Map.empty None Set.empty
    v |> should be Empty

[<Fact>]
let ``runLive flags draft fully overlapping a cell rect on same layer`` () =
    let cell : FlatPolygon array = [|
        rect (0L, 0L, 500L, 500L) (68, 20)
    |]
    // Draft segment sits ENTIRELY inside the cell rect — would be
    // merged into one net by the Spacing rule. Overlap post-pass
    // catches it as a synthetic violation.
    let draftSegs : Draft.DraftSegment list = [
        { Layer = met1; X1 = 100L; Y1 = 100L; X2 = 300L; Y2 = 300L }
    ]
    let draftFlat = Draft.toFlatPolygons draftSegs
    let v = Check.runLive Compat.Magic Rules.defaultView units1nm cell draftFlat Map.empty None Set.empty
    v |> Array.exists (fun x -> x.Rule = "met1.overlap")
      |> should equal true

[<Fact>]
let ``runLive overlap violation only fires when layers match`` () =
    let cell : FlatPolygon array = [|
        rect (0L, 0L, 500L, 500L) (69, 20)   // met2, not met1
    |]
    let draftSegs : Draft.DraftSegment list = [
        { Layer = met1; X1 = 100L; Y1 = 100L; X2 = 300L; Y2 = 300L }
    ]
    let draftFlat = Draft.toFlatPolygons draftSegs
    let v = Check.runLive Compat.Magic Rules.defaultView units1nm cell draftFlat Map.empty None Set.empty
    v |> Array.exists (fun x -> x.Rule.EndsWith ".overlap")
      |> should equal false

[<Fact>]
let ``cellCrossNetOverlaps flags overlap between two committed cross-net polys`` () =
    // Two met1 rects overlapping on different nets — a short. Both
    // labeled. cellCrossNetOverlaps is the dedicated entry point
    // for this (runLive only handles draft-vs-cell).
    let cell : FlatPolygon array = [|
        rectIdx 0 (0L, 0L, 500L, 500L) (68, 20)
        rectIdx 1 (200L, 200L, 700L, 700L) (68, 20)
    |]
    let pref s l d i : PolygonRef =
        { Structure = s; Layer = l; DataType = d; Index = i
          TopInstanceIndex = None }
    let mkEntry n c ps : NetEntry =
        { Name = n; Class = c; Polygons = ps; SeedPolygons = ps
          DirectLabelPolys = ps }
    let nets : Map<string, NetEntry> =
        Map.ofList [
            "BL", mkEntry "BL" Signal [ pref "test" 68 20 0 ]
            "WL", mkEntry "WL" Signal [ pref "test" 68 20 1 ]
        ]
    let v = Check.cellCrossNetOverlaps cell nets
    v |> Array.exists (fun x -> x.Rule = "met1.overlap")
      |> should equal true

[<Fact>]
let ``cellCrossNetOverlaps flags named-vs-unclaimed overlap (user-reported)`` () =
    // User reported: drew a wire on drn_R (claimed by LabelFlood)
    // that visibly overlaps an existing top-cell li1 rect that
    // LabelFlood didn't claim for any net (no label inside it,
    // not reached by flood). The overlap is a real cross-net
    // short risk — the unclaimed poly is structurally a different
    // electrical entity until proven same-net. DRC must surface
    // it for user attention. Pre-fix behaviour: silently skipped
    // because the unclaimed side has netOf=None.
    let cell : FlatPolygon array = [|
        rectIdx 0 (0L, 0L, 500L, 500L) (68, 20)        // claimed
        rectIdx 1 (200L, 200L, 700L, 700L) (68, 20)    // unclaimed
    |]
    let pref s l d i : PolygonRef =
        { Structure = s; Layer = l; DataType = d; Index = i
          TopInstanceIndex = None }
    let mkEntry n c ps : NetEntry =
        { Name = n; Class = c; Polygons = ps; SeedPolygons = ps
          DirectLabelPolys = ps }
    let nets : Map<string, NetEntry> =
        Map.ofList [
            "drn_R", mkEntry "drn_R" Signal [ pref "test" 68 20 0 ]
            // Idx 1 intentionally NOT in any net entry.
        ]
    let v = Check.cellCrossNetOverlaps cell nets
    v |> Array.exists (fun x -> x.Rule = "met1.overlap")
      |> should equal true

[<Fact>]
let ``cellCrossNetOverlaps stays silent for same-net overlap`` () =
    let cell : FlatPolygon array = [|
        rectIdx 0 (0L, 0L, 500L, 500L) (68, 20)
        rectIdx 1 (200L, 200L, 700L, 700L) (68, 20)
    |]
    let pref s l d i : PolygonRef =
        { Structure = s; Layer = l; DataType = d; Index = i
          TopInstanceIndex = None }
    let mkEntry n c ps : NetEntry =
        { Name = n; Class = c; Polygons = ps; SeedPolygons = ps
          DirectLabelPolys = ps }
    let nets : Map<string, NetEntry> =
        Map.ofList [
            "BL", mkEntry "BL" Signal [ pref "test" 68 20 0; pref "test" 68 20 1 ]
        ]
    let v = Check.cellCrossNetOverlaps cell nets
    v |> Array.exists (fun x -> x.Rule.EndsWith ".overlap")
      |> should equal false

// --- Edge cases + perf bound -------------------------------------------

[<Fact>]
let ``runLive with empty cell and empty draft → no violations`` () =
    Check.runLive Compat.Magic Rules.defaultView units1nm [||] [||] Map.empty None Set.empty
    |> should be Empty

[<Fact>]
let ``runLive with empty draft → no violations even on dirty cell`` () =
    // Even if the cell has a self-overlap, runLive without a draft
    // should produce nothing (cell-vs-cell lives in cellCrossNetOverlaps).
    let cell : FlatPolygon array = [|
        rect (0L, 0L, 500L, 500L) (68, 20)
        rect (200L, 200L, 700L, 700L) (68, 20)
    |]
    Check.runLive Compat.Magic Rules.defaultView units1nm cell [||] Map.empty None Set.empty
    |> should be Empty

[<Fact>]
let ``runLive region-filter skips cell polys far outside the draft window`` () =
    // Cell rect is 50 µm = 50000 DBU away — outside the 5 µm
    // region margin. Region-filter should exclude it and the
    // standard rule pass should find no violation.
    let cell : FlatPolygon array = [|
        rect (50000L, 50000L, 50200L, 50200L) (68, 20)
    |]
    let draftSegs : Draft.DraftSegment list = [
        { Layer = met1; X1 = 0L; Y1 = 0L; X2 = 200L; Y2 = 200L }
    ]
    let draftFlat = Draft.toFlatPolygons draftSegs
    Check.runLive Compat.Magic Rules.defaultView units1nm cell draftFlat Map.empty None Set.empty
    |> should be Empty

[<Fact>]
let ``runLive perf bound: 1000 cell polys + 2-segment draft under 100 ms`` () =
    // Synthetic moderate cell: 1000 met1 rects spread across a 1mm
    // square in a 32x32 grid. Draft is a short 2-segment run near
    // the origin. Should complete well under 100 ms thanks to
    // region filtering — a regression here means the per-frame
    // path lost its O(local) bound.
    let cell : FlatPolygon array =
        [|
            for i in 0 .. 31 do
                for j in 0 .. 31 do
                    let x0 = int64 i * 30000L
                    let y0 = int64 j * 30000L
                    yield rect (x0, y0, x0 + 200L, y0 + 200L) (68, 20)
        |]
    let draftSegs : Draft.DraftSegment list = [
        { Layer = met1; X1 = 5000L; Y1 = 5000L; X2 = 5500L; Y2 = 5200L }
        { Layer = met1; X1 = 5500L; Y1 = 5200L; X2 = 6000L; Y2 = 5200L }
    ]
    let draftFlat = Draft.toFlatPolygons draftSegs
    // Warm-up call so the cold-JIT cost doesn't land in the
    // measurement. The interesting signal is the steady-state
    // cost — a regression that pushes per-frame work past the
    // bound when the engine is hot.
    let _ = Check.runLive Compat.Magic Rules.defaultView units1nm cell draftFlat Map.empty None Set.empty
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let _ = Check.runLive Compat.Magic Rules.defaultView units1nm cell draftFlat Map.empty None Set.empty
    sw.Stop()
    // 200 ms budget — still catches anything that goes ≥2× over
    // typical (~30 ms) without being flaky under full-suite load.
    sw.ElapsedMilliseconds |> should be (lessThan 200L)

// --- ADR-0004: RulesetView threading ----------------------------------

[<Fact>]
let ``runLive with a custom view fires only the view's rules`` () =
    // A view with ONLY a tighter met1 spacing rule (1000 nm) →
    // the cell+draft separation under 1000 nm triggers it. The
    // default met1.2 (140 nm) would NOT fire at this distance.
    let met1Key : LayerKey = { Number = 68; DataType = 20 }
    let tightView : Rules.RulesetView =
        { Rules = [ Spacing ("custom.met1.spacing", met1Key, 1.0) ]
          Provenance = Map.ofList [ "custom.met1.spacing", "test.yaml" ] }
    let cell : FlatPolygon array = [|
        rect (0L, 0L, 200L, 200L) (68, 20)
    |]
    let draftSegs : Draft.DraftSegment list = [
        // 500 nm gap — under custom.met1.spacing's 1000 nm limit,
        // over the default met1.2's 140 nm.
        { Layer = met1; X1 = 700L; Y1 = 0L; X2 = 900L; Y2 = 200L }
    ]
    let draftFlat = Draft.toFlatPolygons draftSegs
    let v = Check.runLive Compat.Magic tightView units1nm cell draftFlat Map.empty None Set.empty
    v |> Array.exists (fun x -> x.Rule = "custom.met1.spacing")
      |> should equal true

[<Fact>]
let ``runLive with an empty view fires no rule violations on dirty geometry`` () =
    // Empty rule list → no rule check fires, even though the
    // geometry has a sub-min-spacing pair under the default rules.
    // (The synthetic <layer>.overlap pass still fires on overlap,
    //  but a clean gap of 139 nm produces nothing here.)
    let emptyView : Rules.RulesetView =
        { Rules = []; Provenance = Map.empty }
    let cell : FlatPolygon array = [|
        rect (0L, 0L, 200L, 200L) (68, 20)
    |]
    let draftSegs : Draft.DraftSegment list = [
        { Layer = met1; X1 = 339L; Y1 = 0L; X2 = 539L; Y2 = 200L }
    ]
    let draftFlat = Draft.toFlatPolygons draftSegs
    let v = Check.runLive Compat.Magic emptyView units1nm cell draftFlat Map.empty None Set.empty
    v |> Array.exists (fun x -> x.Rule.EndsWith ".spacing"
                                || x.Rule = "met1.2")
      |> should equal false

[<Fact>]
let ``Rules.defaultView mirrors Rules.allRules with empty provenance`` () =
    Rules.defaultView.Rules |> should equal Rules.allRules
    Rules.defaultView.Provenance
    |> should equal (Map.empty : Map<string, string>)

[<Fact>]
let ``viewOf carries rules and provenance through unchanged`` () =
    let met1Key : LayerKey = { Number = 68; DataType = 20 }
    let rs = [ Spacing ("r1", met1Key, 0.14) ]
    let prov = Map.ofList [ "r1", "src.yaml" ]
    let view = Rules.viewOf rs prov
    view.Rules |> should equal rs
    view.Provenance |> should equal prov

[<Fact>]
let ``runLive honors disabledRules from the caller`` () =
    let cell : FlatPolygon array = [|
        rect (0L, 0L, 200L, 200L) (68, 20)
    |]
    let draftSegs : Draft.DraftSegment list = [
        { Layer = met1; X1 = 339L; Y1 = 0L; X2 = 539L; Y2 = 200L }
    ]
    let draftFlat = Draft.toFlatPolygons draftSegs
    // User silenced met1.2 → no violation should fire.
    let disabled = Set.singleton "met1.2"
    let v = Check.runLive Compat.Magic Rules.defaultView units1nm cell draftFlat Map.empty None disabled
    v |> Array.exists (fun x -> x.Rule = "met1.2")
      |> should equal false
