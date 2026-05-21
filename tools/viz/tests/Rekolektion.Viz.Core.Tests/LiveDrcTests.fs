module Rekolektion.Viz.Core.Tests.LiveDrcTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout.Flatten
open Rekolektion.Viz.Core.Drc
open Rekolektion.Viz.Core.Routing
open Rekolektion.Viz.Core.Sidecar.Types

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

// --- Check.runLive end-to-end ------------------------------------------

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
    let v = Check.runLive units1nm cell draftFlat Map.empty Set.empty
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
    let v = Check.runLive units1nm cell draftFlat Map.empty Set.empty
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
    let v = Check.runLive units1nm cell draftFlat Map.empty Set.empty
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
    let v = Check.runLive units1nm cell draftFlat Map.empty Set.empty
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
    let nets : Map<string, NetEntry> =
        Map.ofList [
            "BL", { Name = "BL"; Class = Signal
                    Polygons = [ { Structure = "test"; Layer = 68
                                   DataType = 20; Index = 0 } ] }
            "WL", { Name = "WL"; Class = Signal
                    Polygons = [ { Structure = "test"; Layer = 68
                                   DataType = 20; Index = 1 } ] }
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
    let nets : Map<string, NetEntry> =
        Map.ofList [
            "BL", { Name = "BL"; Class = Signal
                    Polygons = [
                        { Structure = "test"; Layer = 68
                          DataType = 20; Index = 0 }
                        { Structure = "test"; Layer = 68
                          DataType = 20; Index = 1 }
                    ] }
        ]
    let v = Check.cellCrossNetOverlaps cell nets
    v |> Array.exists (fun x -> x.Rule.EndsWith ".overlap")
      |> should equal false

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
    let v = Check.runLive units1nm cell draftFlat Map.empty disabled
    v |> Array.exists (fun x -> x.Rule = "met1.2")
      |> should equal false
