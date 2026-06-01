module Rekolektion.Viz.App.Tests.RoutingForeignPolyFilterTests

// Integration test for the snap-pad "knuckle" suppression — drives the
// real `commitRouteWith` pipeline (StartRoute → RouteMouseMove →
// RouteSetEndLayer → RouteFinish) with a fixture that mirrors the
// tap_mux_input_inv.rkt bottom VSS route (user report 2026-05-31):
// a li1 wire dropping into a wide met1 VSS rail.
//
// Without the foreign-poly filter wired in, the via-stack emits a
// 290 nm met1 snap-pad on top of the rail — visible as a knuckle.
// The pad is redundant because the rail already fully encloses the
// mcon cut beneath it.
//
// Companion unit tests live in
// `Rekolektion.Viz.Core.Tests.RoutingPadsTests` — they pin down the
// `Pads.dropPadsContainedByForeignPolys` helper in isolation. This
// file verifies the helper is actually wired into the commit pipeline.

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.App.Model
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout

let private stubBackend : Update.ServiceBackend = {
    OpenGds = fun _ -> async { return Error "stub" }
    RunMacro = fun _ _ -> async { return Error 1 }
    DeriveNets = fun _ -> async { return Map.empty }
    SaveMacro = fun _ -> async { return Error "stub" }
    PersistSession = fun _ -> ()
}

let private li1  : Visibility.LayerKey = (67, 20)
let private met1 : Visibility.LayerKey = (68, 20)

let private railPoly () : Poly = {
    // Mirrors tap_mux_input_inv.rkt:11 — the parent VSS rail.
    // 1995 × 260 nm met1 strap that covers the mcon footprint
    // beneath it many times over on every axis.
    Layer = Named ("sky130", "met1")
    Points = [
        { X = -600L; Y = -1260L }
        { X = 1395L; Y = -1260L }
        { X = 1395L; Y = -1000L }
        { X = -600L; Y = -1000L }
        { X = -600L; Y = -1260L }
    ]
    Net = None
    Props = []
    Comments = []
    SubFormComments = Map.empty
}

let private fixtureModel () : Model.Model =
    let doc =
        { emptyDocument with
            Cells = [
                { Name = "TOP"
                  Meta = None
                  Comments = []
                  SubFormComments = Map.empty
                  Elements = [ PolyEl (railPoly ()) ] }
            ]
            TopCell = Some "TOP" }
    let macro : Model.LoadedMacro = {
        Path = "/tmp/foreign_filter_fixture.gds"
        Document = doc
        FlatPolygons = Flatten.flatten doc
        TopInstances = Instances.enumerate doc
        Nets = Map.empty
        Blocks = []
        NetsFromSidecar = false
        SidecarError = None
        OriginalPath = "/tmp/foreign_filter_fixture.gds"
        Dirty = false
        UndoStack = []
        RedoStack = []
        LibrarySnapshot = None
        LibraryMtimes = Map.empty
    }
    { Model.empty with
        OpenMacros = [macro]
        ActiveMacroPath = Some macro.Path
        DrcView = Drc.Rules.defaultView }

/// Mid-X/mid-Y of the rail — where the VSS label sits in the real
/// file and where the snap target would land.
let private snapX = 397L
let private snapY = -1130L

let private newRectsAfterCommit (m: Model.Model) : Rectangle list =
    let macro = List.head m.OpenMacros
    macro.Document.Cells.[0].Elements
    |> List.choose (function RectEl r -> Some r | _ -> None)

let private isMet1 (r: Rectangle) : bool =
    match r.Layer with
    | Named ("sky130", "met1") -> true
    | _ -> false

/// True iff `r`'s bbox is fully inside the rail's bbox on the same layer.
let private fullyInsideRail (r: Rectangle) : bool =
    isMet1 r
    && r.X1 >= -600L && r.X2 <= 1395L
    && r.Y1 >= -1260L && r.Y2 <= -1000L

[<Fact>]
let ``li1 wire dropping onto met1 rail emits NO synthetic met1 pad inside the rail`` () =
    // Drives the same path as the tap_mux_input_inv bottom VSS route:
    //   li1 wire on (67, 20), end snap layer = met1 (68, 20).
    // The via stack at the end emits mcon + a met1 snap-pad at the
    // snap point.  The met1 snap-pad sits inside the rail bbox →
    // redundant geometry → must be dropped by the foreign-poly
    // filter before the rects land in the doc.
    let model = fixtureModel ()
    let m1, _ = Update.update stubBackend
                  (Msg.StartRoute (li1, 170L, "VSS", 0L, 0L, li1)) model
    let m2, _ = Update.update stubBackend
                  (Msg.RouteMouseMove (snapX, snapY)) m1
    let m3, _ = Update.update stubBackend
                  (Msg.RouteSetEndLayer (Some met1)) m2
    let m4, _ = Update.update stubBackend Msg.RouteFinish m3

    let newRects = newRectsAfterCommit m4

    // Sanity: the commit produced SOMETHING (a wire body at minimum)
    // — guards against a fixture mistake that no-ops the pipeline.
    newRects |> List.isEmpty |> should equal false

    // The actual assertion: no newly-emitted met1 RectEl has its
    // CENTRE inside the rail's bbox.  The synthetic snap-pad's centre
    // sits at the snap point (which is by definition inside the
    // rail); the test is independent of whether the pad's bbox
    // sticks out a few nm past the rail edges.
    let unwantedMet1 =
        newRects
        |> List.filter isMet1
        |> List.filter (fun r ->
            let cx = (r.X1 + r.X2) / 2L
            let cy = (r.Y1 + r.Y2) / 2L
            cx >= -600L && cx <= 1395L
            && cy >= -1260L && cy <= -1000L)
    unwantedMet1
    |> List.iter (fun r ->
        eprintfn "  unexpected met1 rect with centre inside rail: X=[%d..%d] Y=[%d..%d]"
            r.X1 r.X2 r.Y1 r.Y2)
    unwantedMet1.Length |> should equal 0

[<Fact>]
let ``li1 wire dropping onto met1 rail DOES emit the mcon cut`` () =
    // Counterpart: the via cut itself MUST land — without it the
    // li1 → met1 transition is electrically broken.  Regression
    // guard against an over-eager filter that drops cuts too.
    let model = fixtureModel ()
    let m1, _ = Update.update stubBackend
                  (Msg.StartRoute (li1, 170L, "VSS", 0L, 0L, li1)) model
    let m2, _ = Update.update stubBackend
                  (Msg.RouteMouseMove (snapX, snapY)) m1
    let m3, _ = Update.update stubBackend
                  (Msg.RouteSetEndLayer (Some met1)) m2
    let m4, _ = Update.update stubBackend Msg.RouteFinish m3

    let mconCuts =
        newRectsAfterCommit m4
        |> List.filter (fun r ->
            match r.Layer with
            | Named ("sky130", "mcon") -> true
            | _ -> false)
    mconCuts |> List.isEmpty |> should equal false
