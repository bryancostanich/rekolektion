module Rekolektion.Viz.Core.Tests.RoutingSnapTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout.Flatten
open Rekolektion.Viz.Core.Routing

let private rect (x1, y1, x2, y2) (layer, dt) idx : FlatPolygon =
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
      SourceIndex = idx
      TopInstanceIndex = None
      Net = None }

/// Same as `rect` but tags the polygon with a `(net …)` attribute
/// and no label — exercises the net-tagged-pad snap pass.
let private netRect (x1, y1, x2, y2) (layer, dt) idx net : FlatPolygon =
    { (rect (x1, y1, x2, y2) (layer, dt) idx) with Net = Some net }

let private label layer textType (x, y) text kind : FlatLabel =
    { Layer = layer
      TextType = textType
      Origin = { X = int64 x; Y = int64 y }
      Text = text
      Kind = kind }

// --- buildTargets -------------------------------------------------------

[<Fact>]
let ``buildTargets emits one target per labeled pin polygon`` () =
    let polys = [|
        rect (0L, 0L, 200L, 200L) (68, 20) 0     // met1 pin patch #0
        rect (500L, 0L, 700L, 200L) (68, 20) 1   // met1 pin patch #1
    |]
    let labels = [|
        // Label sits inside polygon 0 → snap target at its centroid (100, 100).
        label 68 5 (100L, 100L) "BL_3" NetName
        // Label sits inside polygon 1 → centroid (600, 100).
        label 68 5 (600L, 100L) "BL_4" NetName
    |]
    let targets = Snap.buildTargets labels polys
    targets.Length |> should equal 2
    targets.[0].X |> should equal 100L
    targets.[0].Y |> should equal 100L
    targets.[0].Net |> should equal "BL_3"
    targets.[1].X |> should equal 600L
    targets.[1].Y |> should equal 100L

[<Fact>]
let ``buildTargets returns the polygon center even when the label is at the polygon edge`` () =
    // Generated labels are usually centered, but Magic-extracted
    // labels can land at a polygon corner. Snap target should still
    // be the centroid so wires connect at the geometric middle.
    let polys = [| rect (0L, 0L, 1000L, 1000L) (68, 20) 0 |]
    let labels = [| label 68 5 (0L, 0L) "VPWR" NetName |]   // origin at corner
    let targets = Snap.buildTargets labels polys
    targets.[0].X |> should equal 500L

// --- net-tagged pads (no label) -----------------------------------------

[<Fact>]
let ``buildTargets snaps to a net-tagged routing pad that has no label`` () =
    // A met3 (70,20) via pad carrying (net s3) but NO NetName label.
    // Router output looks exactly like this; it must be snappable and
    // the target must carry the pad's net.
    let polys = [| netRect (0L, 0L, 490L, 490L) (70, 20) 0 "s3" |]
    let targets = Snap.buildTargets [||] polys
    targets.Length |> should equal 1
    targets.[0].X |> should equal 245L
    targets.[0].Y |> should equal 245L
    targets.[0].Net |> should equal "s3"
    targets.[0].Layer |> should equal 70

[<Fact>]
let ``buildTargets ignores a net-tagged polygon on a non-routing layer`` () =
    // diff (65,20) is NOT a routing layer — a net-tagged diff shape
    // must not become a wire snap target.
    let polys = [| netRect (0L, 0L, 490L, 490L) (65, 20) 0 "s3" |]
    Snap.buildTargets [||] polys |> Array.length |> should equal 0

[<Fact>]
let ``buildTargets ignores an untagged routing pad`` () =
    // met3 pad with neither label nor net attribute stays a non-target.
    let polys = [| rect (0L, 0L, 490L, 490L) (70, 20) 0 |]
    Snap.buildTargets [||] polys |> Array.length |> should equal 0

[<Fact>]
let ``buildTargets does not double-count a pad that has both a label and a net attribute`` () =
    // Label pass and net pass would both target the same centroid;
    // the net pass must de-dupe against the label pass → one target.
    let polys = [| netRect (0L, 0L, 490L, 490L) (70, 20) 0 "s3" |]
    let labels = [| label 70 5 (245L, 245L) "s3" NetName |]
    let targets = Snap.buildTargets labels polys
    targets.Length |> should equal 1
    targets.[0].Net |> should equal "s3"

[<Fact>]
let ``buildTargets emits both a labeled pad and a separate net-tagged pad`` () =
    let polys = [|
        rect    (0L, 0L, 490L, 490L)    (70, 20) 0          // labeled below
        netRect (1000L, 0L, 1490L, 490L) (70, 20) 1 "s3_b"  // net attr only
    |]
    let labels = [| label 70 5 (245L, 245L) "s3" NetName |]
    let targets = Snap.buildTargets labels polys
    targets.Length |> should equal 2
    (targets |> Array.map (fun t -> t.Net) |> Array.sort)
    |> should equal [| "s3"; "s3_b" |]

[<Fact>]
let ``buildTargets ignores DeviceTerminal labels (not user nets)`` () =
    let polys = [| rect (0L, 0L, 200L, 200L) (68, 20) 0 |]
    let labels = [|
        label 68 5 (100L, 100L) "G" DeviceTerminal   // FET gate annotation
    |]
    Snap.buildTargets labels polys |> should be Empty

[<Fact>]
let ``buildTargets accepts PortName labels (sub-block external pins)`` () =
    // Sub-blocks (FETs, NAND2 etc.) declare their external pins
    // with `(kind port-name)`.  Routing must snap to those — they
    // ARE the pins the user wants to wire to.  Pre-fix the snap
    // pass took NetName-only and rejected every port-name'd label,
    // leaving the user with two snap targets on a dense FET wall.
    let polys = [| rect (0L, 0L, 200L, 200L) (68, 20) 0 |]
    let labels = [|
        label 68 5 (100L, 100L) "v_out_pre" PortName
    |]
    let targets = Snap.buildTargets labels polys
    targets.Length |> should equal 1
    targets.[0].Net |> should equal "v_out_pre"

[<Fact>]
let ``buildTargets ignores labels whose origin falls in no same-layer polygon`` () =
    let polys = [| rect (0L, 0L, 200L, 200L) (68, 20) 0 |]
    // Label is on met1 but origin (500,500) misses the only met1 poly.
    let labels = [| label 68 5 (500L, 500L) "BL" NetName |]
    Snap.buildTargets labels polys |> should be Empty

[<Fact>]
let ``buildTargets does NOT match labels to polygons on a different layer`` () =
    let polys = [|
        rect (0L, 0L, 200L, 200L) (68, 20) 0     // met1 at origin
    |]
    // Label is on met2 (layer 69) but origin sits over the met1 poly.
    let labels = [| label 69 5 (100L, 100L) "BL" NetName |]
    Snap.buildTargets labels polys |> should be Empty

// --- nearest -----------------------------------------------------------

let private mkTarget x y net : Snap.SnapTarget = {
    X = int64 x; Y = int64 y
    Net = net; Layer = 68; DataType = 20
    Source = "test", 0
}

[<Fact>]
let ``nearest returns None when no target is within radius`` () =
    let targets = [| mkTarget 0 0 "A"; mkTarget 1000 1000 "B" |]
    Snap.nearest targets (5000L, 5000L) 100L
    |> should equal (None : Snap.SnapTarget option)

[<Fact>]
let ``nearest picks the closest target inside the radius`` () =
    let targets = [|
        mkTarget 0 0 "A"
        mkTarget 100 100 "B"
        mkTarget 200 200 "C"
    |]
    // Cursor at (120,120). Distances: A=√28800, B=√800, C=√12800.
    let pick = Snap.nearest targets (120L, 120L) 1000L
    pick |> Option.map (fun t -> t.Net) |> should equal (Some "B")

[<Fact>]
let ``nearest with empty target list returns None`` () =
    Snap.nearest [||] (0L, 0L) 1000L
    |> should equal (None : Snap.SnapTarget option)

[<Fact>]
let ``nearest is inclusive at the radius boundary`` () =
    let targets = [| mkTarget 1000 0 "edge" |]
    // Cursor at (0,0), target at (1000,0) → distance exactly 1000.
    Snap.nearest targets (0L, 0L) 1000L
    |> Option.isSome |> should equal true

// --- forStartNet -------------------------------------------------------

[<Fact>]
let ``forStartNet keeps only same-net targets`` () =
    let targets = [|
        mkTarget 0 0 "drn_R"
        mkTarget 100 100 "drn_L"
        mkTarget 200 200 "drn_R"
        mkTarget 300 300 "VSS"
    |]
    let kept = Snap.forStartNet "drn_R" targets
    kept.Length |> should equal 2
    kept |> Array.forall (fun t -> t.Net = "drn_R") |> should equal true

[<Fact>]
let ``forStartNet with empty startNet returns input unchanged`` () =
    // No-draft / unknown-net path: behave as a pass-through so the
    // hover and snap-on-click paths keep their legacy behaviour
    // outside of an active route.
    let targets = [| mkTarget 0 0 "A"; mkTarget 100 100 "B" |]
    Snap.forStartNet "" targets |> should equal targets

[<Fact>]
let ``forStartNet with no matching net returns empty`` () =
    let targets = [| mkTarget 0 0 "drn_L"; mkTarget 100 100 "VSS" |]
    Snap.forStartNet "drn_R" targets |> should be Empty
