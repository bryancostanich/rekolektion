module Rekolektion.Viz.App.Tests.UpdateTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.App.Model
open Rekolektion.Viz.Core

let private stubBackend : Update.ServiceBackend = {
    OpenGds = fun _ -> async { return Error "stub" }
    RunMacro = fun _ _ -> async { return Error 1 }
    DeriveNets = fun _ -> async { return Map.empty }
    SaveMacro = fun _ -> async { return Error "stub" }
}

[<Fact>]
let ``ToggleLayer updates Model.Toggle.Layers`` () =
    let init = Model.empty
    let next, _cmd = Update.update stubBackend (Msg.ToggleLayer ((68, 20), false)) init
    Visibility.isLayerVisible next.Toggle (68, 20) |> should equal false

[<Fact>]
let ``ToggleNetHighlight flips a net's membership in HighlightedNets`` () =
    let next, _ = Update.update stubBackend (Msg.ToggleNetHighlight "BL") Model.empty
    next.Toggle.HighlightedNets |> should equal (Set.singleton "BL")
    let next2, _ = Update.update stubBackend (Msg.ToggleNetHighlight "BL") next
    next2.Toggle.HighlightedNets |> should equal (Set.empty : Set<string>)

[<Fact>]
let ``SetHighlightedNets replaces the set wholesale`` () =
    let seeded =
        let next, _ = Update.update stubBackend (Msg.ToggleNetHighlight "stale") Model.empty
        next
    let next, _ =
        Update.update stubBackend
            (Msg.SetHighlightedNets (Set.ofList ["BL"; "WL"])) seeded
    next.Toggle.HighlightedNets |> should equal (Set.ofList ["BL"; "WL"])

[<Fact>]
let ``ToggleNetRatline flips a net's ratline visibility independently`` () =
    let next, _ = Update.update stubBackend (Msg.ToggleNetRatline "CLK") Model.empty
    next.Toggle.VisibleRatlines |> should equal (Set.singleton "CLK")
    next.Toggle.HighlightedNets |> should equal (Set.empty : Set<string>)

[<Fact>]
let ``ToggleRatlines master: empty set -> all nets, non-empty -> clear`` () =
    // Empty -> all nets in active macro. With no active macro, the
    // expected fallback is empty (no nets to enable).
    let next1, _ = Update.update stubBackend Msg.ToggleRatlines Model.empty
    next1.Toggle.VisibleRatlines |> should equal (Set.empty : Set<string>)
    // Non-empty -> clear regardless of active-macro state.
    let seeded =
        let m, _ = Update.update stubBackend (Msg.ToggleNetRatline "X") Model.empty
        m
    let next2, _ = Update.update stubBackend Msg.ToggleRatlines seeded
    next2.Toggle.VisibleRatlines |> should equal (Set.empty : Set<string>)

[<Fact>]
let ``SetTab changes ActiveTab`` () =
    let next, _ = Update.update stubBackend (Msg.SetTab Model.Tab.View3D) Model.empty
    next.ActiveTab |> should equal Model.Tab.View3D

// --- Polygon selection / move handlers ---------------------------------

open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout

let private mkRectPoly (x0: int64) (y0: int64) (x1: int64) (y1: int64) : Poly = {
    Layer = Named ("sky130", "met1")
    Points = [
        { X = x0; Y = y0 }
        { X = x1; Y = y0 }
        { X = x1; Y = y1 }
        { X = x0; Y = y1 }
        { X = x0; Y = y0 }
    ]
    Net = None
    Props = []
    Comments = []
}

let private fixtureDoc () : Document =
    { emptyDocument with
        Cells = [
            { Name = "TOP"
              Meta = None
              Comments = []
              Elements = [
                  PolyEl (mkRectPoly 0L 0L 100L 100L)
                  PolyEl (mkRectPoly 200L 0L 300L 100L)
              ] }
        ]
        TopCell = Some "TOP" }

let private fixtureModel () : Model.Model =
    let doc = fixtureDoc ()
    let macro : Model.LoadedMacro = {
        Path = "/tmp/fixture.gds"
        Document = doc
        FlatPolygons = Flatten.flatten doc
        TopInstances = Instances.enumerate doc
        Nets = Map.empty
        Blocks = []
        NetsFromSidecar = false
        SidecarError = None
        OriginalPath = "/tmp/fixture.gds"
        Dirty = false
        UndoStack = []
    }
    { Model.empty with
        OpenMacros = [macro]
        ActiveMacroPath = Some macro.Path }

let private runUntilQuiescent (msg: Msg.Msg) (model: Model.Model) : Model.Model =
    let mutable m = model
    let mutable pending : Msg.Msg list = [msg]
    let mutable steps = 0
    while not pending.IsEmpty && steps < 16 do
        steps <- steps + 1
        let head = List.head pending
        pending <- List.tail pending
        let m', cmd = Update.update stubBackend head m
        m <- m'
        for sub in cmd do
            sub (fun forwarded -> pending <- pending @ [forwarded])
    m

[<Fact>]
let ``SetPolygonSelection replaces Selection`` () =
    let model = { Model.empty with Selection = Set.singleton ("A", 1) }
    let next, _ = Update.update stubBackend
                    (Msg.SetPolygonSelection (Set.ofList [("B", 2); ("C", 3)]))
                    model
    next.Selection |> should equal (Set.ofList [("B", 2); ("C", 3)])

[<Fact>]
let ``ClearSelection empties Selection`` () =
    let model = { Model.empty with Selection = Set.ofList [("A", 1); ("B", 2)] }
    let next, _ = Update.update stubBackend Msg.ClearSelection model
    next.Selection |> should equal (Set.empty : Set<string * int>)

[<Fact>]
let ``PolygonPicked replaces Selection with single`` () =
    let model = { Model.empty with Selection = Set.ofList [("A", 1); ("B", 2)] }
    let next, _ = Update.update stubBackend (Msg.PolygonPicked ("X", 9)) model
    next.Selection |> should equal (Set.singleton ("X", 9))

[<Fact>]
let ``MovePolygonsDbu translates every polygon in selection`` () =
    let model = fixtureModel ()
    let sel = Set.ofList [("TOP", 0); ("TOP", 1)]
    let next = runUntilQuiescent (Msg.MovePolygonsDbu (sel, 50L, -25L)) model
    let macro = next.OpenMacros |> List.head
    let elems = (macro.Document.Cells |> List.head).Elements
    match elems.[0] with
    | PolyEl p ->
        p.Points |> should equal [
            { X = 50L;  Y = -25L }
            { X = 150L; Y = -25L }
            { X = 150L; Y = 75L }
            { X = 50L;  Y = 75L }
            { X = 50L;  Y = -25L }
        ]
    | _ -> failwith "expected PolyEl at index 0"
    match elems.[1] with
    | PolyEl p ->
        // Shifted from (200,0)-(300,100) to (250,-25)-(350,75).
        p.Points.Head |> should equal { X = 250L; Y = -25L }
    | _ -> failwith "expected PolyEl at index 1"

[<Fact>]
let ``MovePolygonsDbu only touches polygons in selection`` () =
    let model = fixtureModel ()
    let sel = Set.singleton ("TOP", 0)
    let next = runUntilQuiescent (Msg.MovePolygonsDbu (sel, 10L, 10L)) model
    let macro = next.OpenMacros |> List.head
    let elems = (macro.Document.Cells |> List.head).Elements
    match elems.[1] with
    | PolyEl p ->
        // Untouched: still at original (200,0)-(300,100).
        p.Points.Head |> should equal { X = 200L; Y = 0L }
    | _ -> failwith "expected PolyEl at index 1"

[<Fact>]
let ``MovePolygonsDbu with zero delta is a no-op`` () =
    let model = fixtureModel ()
    let originalDoc = (List.head model.OpenMacros).Document
    let next, _ = Update.update stubBackend
                    (Msg.MovePolygonsDbu (Set.singleton ("TOP", 0), 0L, 0L))
                    model
    let macro = next.OpenMacros |> List.head
    macro.Document |> should equal originalDoc
    macro.Dirty |> should equal false
    macro.UndoStack |> should equal ([] : Document list)

[<Fact>]
let ``MovePolygonsDbu with empty selection is a no-op`` () =
    let model = fixtureModel ()
    let originalDoc = (List.head model.OpenMacros).Document
    let next, _ = Update.update stubBackend
                    (Msg.MovePolygonsDbu (Set.empty, 50L, 50L)) model
    (List.head next.OpenMacros).Document |> should equal originalDoc

[<Fact>]
let ``MovePolygonDbu routes through MovePolygonsDbu and translates one`` () =
    let model = fixtureModel ()
    let next = runUntilQuiescent (Msg.MovePolygonDbu ("TOP", 1, 7L, 11L)) model
    let elems = (List.head (List.head next.OpenMacros).Document.Cells).Elements
    match elems.[1] with
    | PolyEl p ->
        p.Points.Head |> should equal { X = 207L; Y = 11L }
    | _ -> failwith "expected PolyEl at index 1"

[<Fact>]
let ``MovePolygonsDbu pushes an undo snapshot`` () =
    let model = fixtureModel ()
    let next = runUntilQuiescent
                (Msg.MovePolygonsDbu (Set.singleton ("TOP", 0), 5L, 0L)) model
    let macro = List.head next.OpenMacros
    macro.UndoStack.Length |> should equal 1
    macro.Dirty |> should equal true
