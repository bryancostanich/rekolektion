module Rekolektion.Viz.Core.Tests.RoutingObstaclesTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout.Flatten
open Rekolektion.Viz.Core.Sidecar.Types
open Rekolektion.Viz.Core.Routing
open Rekolektion.Viz.Core.Routing.Obstacles

// ---- Test helpers -----------------------------------------------

let private p (x : int) (y : int) : Point = { X = int64 x; Y = int64 y }

/// Build a FlatPolygon authored directly in the top cell.
let private flat (cell : string) (layer : int) (dt : int) (idx : int)
                 (pts : (int * int) list) : FlatPolygon =
    { Layer = layer
      DataType = dt
      Points = pts |> List.map (fun (x, y) -> p x y) |> List.toArray
      SourceStructure = cell
      SourceIndex = idx
      TopInstanceIndex = None }

/// Wrap a (cell, layer, dt, idx) tuple as the PolygonRef Sidecar uses.
let private pref cell layer dt idx : PolygonRef =
    { Structure = cell; Layer = layer; DataType = dt; Index = idx
      TopInstanceIndex = None }

let private netEntry (name : string) (cls : NetClass)
                     (refs : PolygonRef list) : NetEntry =
    // Treat every poly as direct-seeded in tests so the obstacle
    // classifier sees the test's intended ownership rather than
    // being subject to the flood-vs-seed priority pass.
    { Name = name; Class = cls; Polygons = refs; SeedPolygons = refs
      DirectLabelPolys = refs }

let private li1Layer  : LayerKey = { Number = 67; DataType = 20 }
let private met1Layer : LayerKey = { Number = 68; DataType = 20 }
let private polyLayer : LayerKey = { Number = 66; DataType = 20 }

// ---- isRoutingLayer ---------------------------------------------

[<Fact>]
let ``li1 / met1-4 are routing layers; everything else is not`` () =
    isRoutingLayer { Number = 67; DataType = 20 } |> should equal true
    isRoutingLayer { Number = 68; DataType = 20 } |> should equal true
    isRoutingLayer { Number = 69; DataType = 20 } |> should equal true
    isRoutingLayer { Number = 70; DataType = 20 } |> should equal true
    isRoutingLayer { Number = 71; DataType = 20 } |> should equal true
    // pin datatypes are not the drawing-layer key
    isRoutingLayer { Number = 67; DataType = 5  } |> should equal false
    // contacts / vias are obstacles, not routing layers
    isRoutingLayer { Number = 67; DataType = 44 } |> should equal false
    isRoutingLayer { Number = 68; DataType = 44 } |> should equal false
    // poly / diff
    isRoutingLayer { Number = 66; DataType = 20 } |> should equal false
    isRoutingLayer { Number = 65; DataType = 20 } |> should equal false

// ---- buildNetIndex / netOf --------------------------------------

[<Fact>]
let ``netOf returns the net name for a polygon claimed by an entry`` () =
    let nets =
        Map.ofList [
            "VGND", netEntry "VGND" Ground [ pref "top" 67 20 0 ]
            "BL",   netEntry "BL"   Signal [ pref "top" 67 20 1 ]
        ]
    let idx = buildNetIndex nets
    netOf idx (flat "top" 67 20 0 [ 0,0; 100,0; 100,100; 0,100; 0,0 ])
    |> should equal (Some "VGND")
    netOf idx (flat "top" 67 20 1 [ 200,0; 300,0; 300,100; 200,100; 200,0 ])
    |> should equal (Some "BL")

[<Fact>]
let ``netOf returns None for a polygon no entry claims`` () =
    let nets = Map.ofList [ "VGND", netEntry "VGND" Ground [ pref "top" 67 20 0 ] ]
    let idx = buildNetIndex nets
    // Same cell, different index → not claimed.
    netOf idx (flat "top" 67 20 42 [])
    |> should equal (None : string option)
    // Different cell entirely.
    netOf idx (flat "other" 67 20 0 [])
    |> should equal (None : string option)

[<Fact>]
let ``net claims key on (source + TopInstanceIndex), not source alone`` () =
    // PolyId includes TopInstanceIndex so two physical instances
    // of the same source polygon are distinguishable. Without this,
    // a polygon labeled SIGN in one top-instance collapses with the
    // same source polygon labeled drn_R in another top-instance —
    // walkaround then sees the SIGN polygon as drn_R's own and
    // routes through it.
    let nets = Map.ofList [ "VGND", netEntry "VGND" Ground [ pref "nfet" 67 20 0 ] ]
    let idx = buildNetIndex nets
    // `pref` defaults to TopInstanceIndex = None — matches a flat
    // polygon authored directly in the top cell.
    let a = flat "nfet" 67 20 0 []
    netOf idx a |> should equal (Some "VGND")
    // Different top-instance → different PolyId → no match.
    let b = { flat "nfet" 67 20 0 [] with TopInstanceIndex = Some 7 }
    netOf idx b |> should equal (None : string option)

// ---- obstacleSet — same-layer foreign-net polygons --------------

[<Fact>]
let ``direct-label authority: poly labeled X is foreign to Y even if Y's flood claimed it`` () =
    // User-reported: drn_R's contact flood reached mag_drain_3's
    // li1 pin (different label, same FET via shared diff/contacts).
    // Pre-fix `isOurs(drn_R, mag_drain_3_pin) = true` because drn_R's
    // claim set included it; routing happily ran the user's wire
    // through the foreign pin. Direct-label authority: label intent
    // wins — the poly is directly labeled `mag_drain_3`, so it is
    // foreign to drn_R regardless of flood claims.
    let pinRef : PolygonRef = pref "nfet" 67 20 14
    // Construct an over-claim scenario: BOTH drn_R and mag_drain_3
    // have the pin in their Polygons list (mimics over-flood). Only
    // mag_drain_3 has it in DirectLabelPolys (the actual label sits
    // inside this poly).
    let drnRPin : PolygonRef = pref "nfet" 67 20 100   // drn_R's own pin
    let nets =
        Map.ofList [
            "drn_R",
            { Name = "drn_R"; Class = Signal
              Polygons = [ pinRef; drnRPin ]      // over-claim by flood
              SeedPolygons = [ pinRef; drnRPin ]
              DirectLabelPolys = [ drnRPin ] }    // only drn_R's own pin is directly labeled
            "mag_drain_3",
            { Name = "mag_drain_3"; Class = Signal
              Polygons = [ pinRef ]
              SeedPolygons = [ pinRef ]
              DirectLabelPolys = [ pinRef ] }     // mag_drain_3's label is inside `pinRef`
        ]
    let idx = buildNetIndex nets
    let pinFlat = flat "nfet" 67 20 14 [ 0,0; 100,0; 100,100; 0,100; 0,0 ]
    // drn_R's own pin: directly labeled drn_R → ours for drn_R.
    let drnRPinFlat = flat "nfet" 67 20 100 [ 0,0; 100,0; 100,100; 0,100; 0,0 ]
    Obstacles.isOurs idx "drn_R" drnRPinFlat |> should equal true
    // mag_drain_3's pin: directly labeled mag_drain_3 → foreign to
    // drn_R (this is the bug fix — was returning true via flood claim).
    Obstacles.isOurs idx "drn_R" pinFlat |> should equal false
    // And mag_drain_3 still owns its own pin.
    Obstacles.isOurs idx "mag_drain_3" pinFlat |> should equal true

[<Fact>]
let ``same-layer foreign-net polygon is an obstacle`` () =
    let bl = flat "top" 67 20 0 [ 0,0; 100,0; 100,100; 0,100; 0,0 ]
    let nets = Map.ofList [ "BL", netEntry "BL" Signal [ pref "top" 67 20 0 ] ]
    let idx = buildNetIndex nets
    Obstacles.obstacleSet li1Layer "VGND" idx [| bl |] |> Obstacles.polygonsOf
    |> Array.length |> should equal 1

[<Fact>]
let ``same-layer SAME-net polygon is NOT an obstacle`` () =
    let same = flat "top" 67 20 0 [ 0,0; 100,0; 100,100; 0,100; 0,0 ]
    let nets = Map.ofList [ "VGND", netEntry "VGND" Ground [ pref "top" 67 20 0 ] ]
    let idx = buildNetIndex nets
    Obstacles.obstacleSet li1Layer "VGND" idx [| same |] |> Obstacles.polygonsOf
    |> should be Empty

// ---- obstacleSet — bridge layers --------------------------------

[<Fact>]
let ``foreign-net licon UNDER a li1 wire is an obstacle`` () =
    // licon1 = 66/44. A wire on li1 (67/20) passing over this licon
    // would short the wire's net to whatever the licon belongs to.
    let foreignLicon = flat "top" 66 44 0 [ 0,0; 50,0; 50,50; 0,50; 0,0 ]
    let nets = Map.ofList [ "BL", netEntry "BL" Signal [ pref "top" 66 44 0 ] ]
    let idx = buildNetIndex nets
    Obstacles.obstacleSet li1Layer "VGND" idx [| foreignLicon |] |> Obstacles.polygonsOf
    |> Array.length |> should equal 1

[<Fact>]
let ``same-net licon under a li1 wire is NOT an obstacle`` () =
    let sameLicon = flat "top" 66 44 0 []
    let nets = Map.ofList [ "VGND", netEntry "VGND" Ground [ pref "top" 66 44 0 ] ]
    let idx = buildNetIndex nets
    Obstacles.obstacleSet li1Layer "VGND" idx [| sameLicon |] |> Obstacles.polygonsOf
    |> should be Empty

[<Fact>]
let ``foreign-net mcon (li1->met1) is an obstacle for a li1 wire`` () =
    // mcon = 67/44 — bridge between li1 and met1.
    let foreignMcon = flat "top" 67 44 0 []
    let nets = Map.ofList [ "X", netEntry "X" Signal [ pref "top" 67 44 0 ] ]
    let idx = buildNetIndex nets
    Obstacles.obstacleSet li1Layer "VGND" idx [| foreignMcon |] |> Obstacles.polygonsOf
    |> Array.length |> should equal 1

[<Fact>]
let ``foreign-net mcon AND via are obstacles for a met1 wire`` () =
    let mcon = flat "top" 67 44 0 []   // foreign mcon below met1
    let via  = flat "top" 68 44 1 []   // foreign via above met1
    let nets =
        Map.ofList [
            "Foreign", netEntry "Foreign" Signal
                [ pref "top" 67 44 0; pref "top" 68 44 1 ]
        ]
    let idx = buildNetIndex nets
    Obstacles.obstacleSet met1Layer "Ours" idx [| mcon; via |] |> Obstacles.polygonsOf
    |> Array.length |> should equal 2

// ---- obstacleSet — unclaimed polygons (defensive) ---------------

[<Fact>]
let ``polygon with no net claim is treated as a foreign obstacle`` () =
    // Nothing claims this polygon — could be a stray label-free
    // feature. We can't prove it's ours, so route around it.
    let stray = flat "top" 67 20 99 []
    let idx = buildNetIndex Map.empty
    Obstacles.obstacleSet li1Layer "VGND" idx [| stray |] |> Obstacles.polygonsOf
    |> Array.length |> should equal 1

// ---- obstacleSet — out-of-scope layers --------------------------

[<Fact>]
let ``polygons on layers unrelated to the wire are NOT obstacles`` () =
    // diff (65/20), poly (66/20), nwell (64/20) — none touch a li1
    // wire electrically. They don't make the obstacle set.
    let diff   = flat "top" 65 20 0 []
    let poly   = flat "top" 66 20 1 []
    let nwell  = flat "top" 64 20 2 []
    let nets =
        Map.ofList [
            "Stuff", netEntry "Stuff" Signal
                [ pref "top" 65 20 0
                  pref "top" 66 20 1
                  pref "top" 64 20 2 ]
        ]
    let idx = buildNetIndex nets
    Obstacles.obstacleSet li1Layer "VGND" idx [| diff; poly; nwell |] |> Obstacles.polygonsOf
    |> should be Empty

// ---- obstaclesInRegionCached — bbox filter ----------------------

[<Fact>]
let ``obstaclesInRegion drops polygons whose bbox sits outside the region`` () =
    // Two foreign-net features on the routing layer: one near the
    // origin (intersects the region), one 100 µm away (does not).
    let near = flat "top" 67 20 0 [ 0,0; 100,0; 100,100; 0,100; 0,0 ]
    let far  = flat "top" 67 20 1
                  [ 50_000,0; 50_100,0; 50_100,100; 50_000,100; 50_000,0 ]
    let nets =
        Map.ofList [
            "BL", netEntry "BL" Signal
                [ pref "top" 67 20 0; pref "top" 67 20 1 ]
        ]
    let idx = buildNetIndex nets
    let region : Region =
        { XMin = -200L; YMin = -200L; XMax = 200L; YMax = 200L }
    let set = Obstacles.obstacleSet li1Layer "VGND" idx [| near; far |]
    let obs = Obstacles.obstaclesInRegionCached set region
    obs |> Array.length |> should equal 1
    obs.[0].SourceIndex |> should equal 0

[<Fact>]
let ``obstaclesInRegionCached keeps the same net classification as the full set`` () =
    let foreign = flat "top" 66 44 0
                    [ 100,0; 200,0; 200,100; 100,100; 100,0 ]    // foreign licon
    let ours    = flat "top" 66 44 1
                    [ 300,0; 400,0; 400,100; 300,100; 300,0 ]    // our licon
    let nets =
        Map.ofList [
            "BL",   netEntry "BL"   Signal [ pref "top" 66 44 0 ]
            "VGND", netEntry "VGND" Ground [ pref "top" 66 44 1 ]
        ]
    let idx = buildNetIndex nets
    let region : Region = { XMin = 0L; YMin = 0L; XMax = 1000L; YMax = 1000L }
    let set = Obstacles.obstacleSet li1Layer "VGND" idx [| foreign; ours |]
    let obs = Obstacles.obstaclesInRegionCached set region
    // Foreign in, ours out.
    obs |> Array.length |> should equal 1
    obs.[0].SourceIndex |> should equal 0

[<Fact>]
let ``obstacleSet on a non-routing layer returns an empty set`` () =
    // We don't route on poly. Calling the function with a poly layer
    // is a configuration mistake; rather than crash, we return [||]
    // and let the caller's straight-L behaviour take over.
    let anything = flat "top" 67 20 0 []
    let nets = Map.ofList [ "X", netEntry "X" Signal [ pref "top" 67 20 0 ] ]
    let idx = buildNetIndex nets
    Obstacles.obstacleSet polyLayer "X" idx [| anything |] |> Obstacles.polygonsOf
    |> should be Empty
