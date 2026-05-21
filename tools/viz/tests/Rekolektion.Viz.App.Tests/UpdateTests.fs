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

/// Construct a PolyKey from a (cell, idx) tuple. Helper so tests
/// can keep their concise tuple style while the production model
/// uses the richer key.
let private pk (cell: string) (idx: int) : Flatten.PolyKey =
    { Cell = cell; Index = idx; TopInstance = None }

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
        RedoStack = []
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
    let model = { Model.empty with Selection = Set.singleton (pk "A" 1) }
    let next, _ = Update.update stubBackend
                    (Msg.SetPolygonSelection (Set.ofList [pk "B" 2; pk "C" 3]))
                    model
    next.Selection |> should equal (Set.ofList [pk "B" 2; pk "C" 3])

[<Fact>]
let ``ClearSelection empties Selection`` () =
    let model = { Model.empty with Selection = Set.ofList [pk "A" 1; pk "B" 2] }
    let next, _ = Update.update stubBackend Msg.ClearSelection model
    next.Selection |> should equal (Set.empty : Set<Flatten.PolyKey>)

[<Fact>]
let ``PolygonPicked replaces Selection with single`` () =
    let model = { Model.empty with Selection = Set.ofList [pk "A" 1; pk "B" 2] }
    let next, _ = Update.update stubBackend (Msg.PolygonPicked (pk "X" 9)) model
    next.Selection |> should equal (Set.singleton (pk "X" 9))

[<Fact>]
let ``MovePolygonsDbu translates every polygon in selection`` () =
    let model = fixtureModel ()
    let sel = Set.ofList [pk "TOP" 0; pk "TOP" 1]
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
    let sel = Set.singleton (pk "TOP" 0)
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
                    (Msg.MovePolygonsDbu (Set.singleton (pk "TOP" 0), 0L, 0L))
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
                (Msg.MovePolygonsDbu (Set.singleton (pk "TOP" 0), 5L, 0L)) model
    let macro = List.head next.OpenMacros
    macro.UndoStack.Length |> should equal 1
    macro.Dirty |> should equal true

[<Fact>]
let ``ToggleRatlines on an underived cell ARMS but does not sync-derive`` () =
    // The U key MUST NOT block the UI thread on cells without
    // pre-derived nets — the LabelFlood pass can run 10+ s on
    // production macros. ToggleRatlines now just flips
    // RatlinesArmed; the background derive that LoadComplete
    // already kicked off paints VisibleRatlines via NetsLoaded
    // when it returns.
    let macro : Model.LoadedMacro = {
        Path = "/tmp/labelled.gds"
        Document = emptyDocument
        FlatPolygons = [||]
        TopInstances = [||]
        Nets = Map.empty            // ← intentional: derive in flight
        Blocks = []
        NetsFromSidecar = false
        SidecarError = None
        OriginalPath = "/tmp/labelled.gds"
        Dirty = false
        UndoStack = []
        RedoStack = []
    }
    let model =
        { Model.empty with
            OpenMacros = [macro]
            ActiveMacroPath = Some macro.Path }
    let next, _ = Update.update stubBackend Msg.ToggleRatlines model
    next.RatlinesArmed |> should equal true
    // No sync derive happened → no ratlines visible yet.
    next.Toggle.VisibleRatlines |> should equal (Set.empty : Set<string>)

[<Fact>]
let ``NetsLoaded paints ratlines when RatlinesArmed and active path matches`` () =
    let macro : Model.LoadedMacro = {
        Path = "/tmp/labelled.gds"
        Document = emptyDocument
        FlatPolygons = [||]
        TopInstances = [||]
        Nets = Map.empty
        Blocks = []
        NetsFromSidecar = false
        SidecarError = None
        OriginalPath = "/tmp/labelled.gds"
        Dirty = false
        UndoStack = []
        RedoStack = []
    }
    let armed =
        { Model.empty with
            OpenMacros = [macro]
            ActiveMacroPath = Some macro.Path
            RatlinesArmed = true }
    let derivedNets : Map<string, Sidecar.Types.NetEntry> =
        Map.ofList [
            "BL_3", { Name = "BL_3"; Class = Sidecar.Types.Signal; Polygons = [] }
            "WL_5", { Name = "WL_5"; Class = Sidecar.Types.Signal; Polygons = [] }
        ]
    let next, _ =
        Update.update stubBackend
            (Msg.NetsLoaded (macro.Path, derivedNets)) armed
    next.Toggle.VisibleRatlines
    |> should equal (Set.ofList ["BL_3"; "WL_5"])

[<Fact>]
let ``NetsLoaded does NOT paint ratlines when RatlinesArmed is false`` () =
    // User hasn't pressed U yet; background derive arrives. Cache
    // the nets on the macro, but don't surprise-paint.
    let macro : Model.LoadedMacro = {
        Path = "/tmp/labelled.gds"
        Document = emptyDocument
        FlatPolygons = [||]
        TopInstances = [||]
        Nets = Map.empty
        Blocks = []
        NetsFromSidecar = false
        SidecarError = None
        OriginalPath = "/tmp/labelled.gds"
        Dirty = false
        UndoStack = []
        RedoStack = []
    }
    let model =
        { Model.empty with
            OpenMacros = [macro]
            ActiveMacroPath = Some macro.Path
            RatlinesArmed = false }
    let derivedNets : Map<string, Sidecar.Types.NetEntry> =
        Map.ofList [
            "BL_3", { Name = "BL_3"; Class = Sidecar.Types.Signal; Polygons = [] }
        ]
    let next, _ =
        Update.update stubBackend
            (Msg.NetsLoaded (macro.Path, derivedNets)) model
    next.Toggle.VisibleRatlines |> should equal (Set.empty : Set<string>)
    // But the nets cache was updated on the macro.
    (List.head next.OpenMacros).Nets |> should equal derivedNets

// --- ADR-0002 routing tool ---------------------------------------------

let private met1 : Visibility.LayerKey = (68, 20)

[<Fact>]
let ``StartRoute initialises DraftRoute at the anchor`` () =
    let model = fixtureModel ()
    let next, _ = Update.update stubBackend
                    (Msg.StartRoute (met1, 320L, 100L, 200L)) model
    match next.DraftRoute with
    | None -> failwith "expected DraftRoute to be Some after StartRoute"
    | Some d ->
        d.Layer |> should equal met1
        d.Width |> should equal 320L
        d.Points |> should equal [(100L, 200L)]
        d.Cursor |> should equal (None : (int64 * int64) option)

[<Fact>]
let ``StartRoute is a no-op when no active macro`` () =
    let next, _ = Update.update stubBackend
                    (Msg.StartRoute (met1, 320L, 0L, 0L)) Model.empty
    next.DraftRoute |> should equal (None : Routing.Draft.DraftRoute option)

[<Fact>]
let ``RouteMouseMove updates the live cursor`` () =
    let model = fixtureModel ()
    let m1, _ = Update.update stubBackend
                  (Msg.StartRoute (met1, 320L, 0L, 0L)) model
    let m2, _ = Update.update stubBackend
                  (Msg.RouteMouseMove (500L, 300L)) m1
    (Option.get m2.DraftRoute).Cursor |> should equal (Some (500L, 300L))

[<Fact>]
let ``RouteMouseMove is a no-op when DraftRoute is None`` () =
    let next, _ = Update.update stubBackend
                    (Msg.RouteMouseMove (500L, 300L)) Model.empty
    next.DraftRoute |> should equal (None : Routing.Draft.DraftRoute option)

[<Fact>]
let ``RouteFixSegment appends cursor to Points and clears cursor`` () =
    let model = fixtureModel ()
    let m1, _ = Update.update stubBackend
                  (Msg.StartRoute (met1, 320L, 0L, 0L)) model
    let m2, _ = Update.update stubBackend
                  (Msg.RouteMouseMove (1000L, 0L)) m1
    let m3, _ = Update.update stubBackend Msg.RouteFixSegment m2
    let d = Option.get m3.DraftRoute
    d.Points |> should equal [(0L, 0L); (1000L, 0L)]
    d.Cursor |> should equal (None : (int64 * int64) option)

[<Fact>]
let ``RouteAbort discards DraftRoute without touching the document`` () =
    let model = fixtureModel ()
    let originalDoc = (List.head model.OpenMacros).Document
    let m1, _ = Update.update stubBackend
                  (Msg.StartRoute (met1, 320L, 0L, 0L)) model
    let m2, _ = Update.update stubBackend Msg.RouteAbort m1
    m2.DraftRoute |> should equal (None : Routing.Draft.DraftRoute option)
    (List.head m2.OpenMacros).Document |> should equal originalDoc
    (List.head m2.OpenMacros).Dirty |> should equal false
    (List.head m2.OpenMacros).UndoStack |> should equal ([] : Document list)

[<Fact>]
let ``RouteFinish commits segments to the active macro and pushes undo`` () =
    let model = fixtureModel ()
    let before = (List.head model.OpenMacros).Document.Cells.[0].Elements.Length
    let m1, _ = Update.update stubBackend
                  (Msg.StartRoute (met1, 320L, 0L, 0L)) model
    let m2, _ = Update.update stubBackend
                  (Msg.RouteMouseMove (1000L, 0L)) m1
    let m3, _ = Update.update stubBackend Msg.RouteFinish m2
    // Draft cleared.
    m3.DraftRoute |> should equal (None : Routing.Draft.DraftRoute option)
    let macro = List.head m3.OpenMacros
    // Straight horizontal segment + DRC-driven endpoint pads on
    // met1 → 1 wire RectEl + 2 pad RectEls. Pads come first in the
    // batch; the wire is the last appended element.
    let elems = macro.Document.Cells.[0].Elements
    elems.Length |> should equal (before + 3)
    match List.last elems with
    | RectEl r ->
        r.Layer |> should equal (Named ("sky130", "met1"))
        r.X1 |> should equal -160L
        r.X2 |> should equal 1160L
    | _ -> failwith "expected wire RectEl as the last appended element"
    // Undo pushed, dirty flagged.
    macro.UndoStack.Length |> should equal 1
    macro.Dirty |> should equal true

[<Fact>]
let ``RouteFinish on a degenerate draft (no segments) just clears DraftRoute`` () =
    let model = fixtureModel ()
    let originalDoc = (List.head model.OpenMacros).Document
    let m1, _ = Update.update stubBackend
                  (Msg.StartRoute (met1, 320L, 0L, 0L)) model
    // No mouse move → no cursor → finishSegments = [] (only anchor).
    let m2, _ = Update.update stubBackend Msg.RouteFinish m1
    m2.DraftRoute |> should equal (None : Routing.Draft.DraftRoute option)
    (List.head m2.OpenMacros).Document |> should equal originalDoc
    (List.head m2.OpenMacros).UndoStack |> should equal ([] : Document list)

[<Fact>]
let ``RouteStop commits ONLY fixed corners, drops the tentative L`` () =
    // Draft: anchor at (0,0), fix a corner at (500,0), then move
    // the cursor to (1000,500). At this point finishSegments would
    // commit two segments (the fixed straight + the tentative L
    // to the cursor), but RouteStop should commit ONLY the fixed
    // straight segment.
    let model = fixtureModel ()
    let before =
        (List.head model.OpenMacros).Document.Cells.[0].Elements.Length
    let m1, _ = Update.update stubBackend
                  (Msg.StartRoute (met1, 320L, 0L, 0L)) model
    let m2, _ = Update.update stubBackend
                  (Msg.RouteMouseMove (500L, 0L)) m1
    let m3, _ = Update.update stubBackend Msg.RouteFixSegment m2
    let m4, _ = Update.update stubBackend
                  (Msg.RouteMouseMove (1000L, 500L)) m3
    let m5, _ = Update.update stubBackend Msg.RouteStop m4
    m5.DraftRoute |> should equal (None : Routing.Draft.DraftRoute option)
    let macro = List.head m5.OpenMacros
    let elems = macro.Document.Cells.[0].Elements
    // 1 fixed wire (0,0)→(500,0) + 2 pads at anchor + last fixed.
    // RouteFinish would have produced more elements (extra L from
    // tentative + extra pad shifted to cursor).
    elems.Length |> should equal (before + 3)
    // Last appended element is the wire — should span anchor→(500,0).
    match List.last elems with
    | RectEl r ->
        r.X1 |> should equal -160L
        r.X2 |> should equal 660L
    | _ -> failwith "expected wire RectEl as last appended element"
    // Last pad (second of two) lands at the LAST FIXED point (500,0),
    // not at (1000,500) where the cursor was at Esc. Pads come first
    // in the batch — index `before + 1` is the second pad.
    match elems.[before + 1] with
    | RectEl r ->
        r.X1 |> should equal 355L   // 500 - half(290) = 355
        r.X2 |> should equal 645L
        r.Y1 |> should equal -145L  // pad centered at Y=0
        r.Y2 |> should equal 145L
    | _ -> failwith "expected pad RectEl at last fixed point"

[<Fact>]
let ``RouteFinish emits DRC-driven endpoint pads on the active layer`` () =
    // met1 pad is 290 nm per Rules.allRules (mcon enclosure
    // dominates min-area). A straight horizontal route from
    // (0,0) → (1000,0) emits 1 wire RectEl + 2 pad RectEls (one
    // at each end) → 3 new elements appended to the cell.
    let model =
        let m = fixtureModel ()
        { m with DrcView = Rekolektion.Viz.Core.Drc.Rules.defaultView }
    let before =
        (List.head model.OpenMacros).Document.Cells.[0].Elements.Length
    let m1, _ = Update.update stubBackend
                  (Msg.StartRoute (met1, 320L, 0L, 0L)) model
    let m2, _ = Update.update stubBackend
                  (Msg.RouteMouseMove (1000L, 0L)) m1
    let m3, _ = Update.update stubBackend Msg.RouteFinish m2
    let macro = List.head m3.OpenMacros
    let elems = macro.Document.Cells.[0].Elements
    elems.Length |> should equal (before + 3)   // wire + 2 pads
    // Pads are emitted FIRST in the batch (before the wire) per the
    // RouteFinish arm; check that the first new element is a square
    // centered at (0,0) with side 290 nm.
    match elems.[before] with
    | RectEl r ->
        r.X1 |> should equal -145L
        r.X2 |> should equal 145L
        r.Y1 |> should equal -145L
        r.Y2 |> should equal 145L
    | _ -> failwith "expected RectEl pad at first appended slot"

[<Fact>]
let ``RouteFinish with no active macro clears DraftRoute and does nothing else`` () =
    // Manually inject a DraftRoute since StartRoute would refuse
    // without an active macro.
    let draft = Routing.Draft.start met1 320L (0L, 0L) |> Routing.Draft.setCursor (1000L, 0L)
    let model = { Model.empty with DraftRoute = Some draft }
    let next, _ = Update.update stubBackend Msg.RouteFinish model
    next.DraftRoute |> should equal (None : Routing.Draft.DraftRoute option)
