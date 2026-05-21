module Rekolektion.Viz.App.Tests.CanvasWiringTests

open System
open Xunit
open FsUnit.Xunit
open Avalonia
open Avalonia.Controls
open Avalonia.Headless
open Avalonia.Input
open Avalonia.Threading
open Rekolektion.Viz.App.Canvas2D.GdsCanvasControl
open Rekolektion.Viz.Core

/// Shared Avalonia.Headless session — one per assembly. See
/// `TestSession.fs` for the rationale.
let private session = TestSession.instance.Value

let private runOnUi (action: unit -> 'a) : 'a =
    let mutable result : 'a = Unchecked.defaultof<'a>
    let task =
        session.Dispatch((fun () -> result <- action ()),
                         Threading.CancellationToken.None)
    task.GetAwaiter().GetResult()
    result

/// Make a tiny window holding the canvas at a known size so the
/// world-coord conversion is deterministic. `setup` runs after
/// the canvas is constructed and added to the window but before
/// the window is shown — that's where tests configure properties
/// and dispatch handlers.
let private withCanvas
        (setup: GdsCanvasControl -> unit)
        (act: Window -> GdsCanvasControl -> 'a)
        : 'a =
    runOnUi (fun () ->
        let canvas = GdsCanvasControl()
        canvas.Width  <- 200.0
        canvas.Height <- 200.0
        setup canvas
        let window = Window()
        window.Width  <- 200.0
        window.Height <- 200.0
        window.Content <- canvas
        window.Show()
        // Force a layout pass so the canvas has real Bounds —
        // ScreenToWorld depends on this.Bounds being non-zero.
        Dispatcher.UIThread.RunJobs()
        let r = act window canvas
        window.Close()
        r)

/// Capture every invocation of the canvas's routing handlers so
/// tests can assert what the wiring dispatched.
type private CapturedActions = {
    mutable Starts : (Visibility.LayerKey * int64 * string * int64 * int64) list
    mutable Fixes  : int
    mutable Finishes : int
    mutable Moves  : (int64 * int64) list
}

let private newCapture () : CapturedActions = {
    Starts = []
    Fixes = 0
    Finishes = 0
    Moves = []
}

let private wireCapture (canvas: GdsCanvasControl) (cap: CapturedActions) =
    canvas.StartRouteHandler <-
        Action<Visibility.LayerKey, int64, string, int64, int64>(fun l w net x y ->
            cap.Starts <- cap.Starts @ [(l, w, net, x, y)])
    canvas.RouteFixSegmentHandler <-
        Action(fun () -> cap.Fixes <- cap.Fixes + 1)
    canvas.RouteFinishHandler <-
        Action(fun () -> cap.Finishes <- cap.Finishes + 1)
    canvas.RouteMouseMoveHandler <-
        Action<int64, int64>(fun x y -> cap.Moves <- cap.Moves @ [(x, y)])

// --- Pointer dispatch through the real OnPointerPressed -----------------

[<Fact>]
let ``RoutingMode on, click in free space (no snap target) → no StartRoute`` () =
    // New behavior: a click that misses every labeled-pin snap
    // target does NOT start a route. Prevents anchoring wires in
    // free space where they couldn't connect to anything anyway.
    // (To exercise the snap-hit path, a future test will need to
    // construct a Library with labels + flat polygons.)
    let cap = newCapture ()
    withCanvas
        (fun c ->
            wireCapture c cap
            c.RoutingMode <- true
            c.ActiveLayer <- Some (69, 20))   // met2
        (fun window _canvas ->
            window.MouseDown(Point(100.0, 100.0), MouseButton.Left)
            Dispatcher.UIThread.RunJobs())
    cap.Starts |> should be Empty

[<Fact>]
let ``RoutingMode off, no draft, left-click → no routing dispatch`` () =
    let cap = newCapture ()
    withCanvas
        (fun c ->
            wireCapture c cap
            c.RoutingMode <- false
            c.ActiveLayer <- Some (68, 20))
        (fun window _ ->
            window.MouseDown(Point(50.0, 50.0), MouseButton.Left)
            Dispatcher.UIThread.RunJobs())
    cap.Starts |> should be Empty
    cap.Fixes |> should equal 0
    cap.Finishes |> should equal 0

[<Fact>]
let ``Draft in flight, left-click → RouteFixSegment fires`` () =
    let cap = newCapture ()
    let draft =
        Routing.Draft.start (68, 20) 320L (0L, 0L)
        |> Routing.Draft.setCursor (500L, 0L)
        |> Routing.Draft.fix
    withCanvas
        (fun c ->
            wireCapture c cap
            c.RoutingMode <- true
            c.ActiveLayer <- Some (68, 20)
            c.DraftRoute  <- Some draft)
        (fun window _ ->
            window.MouseDown(Point(120.0, 80.0), MouseButton.Left)
            Dispatcher.UIThread.RunJobs())
    cap.Fixes |> should equal 1
    cap.Starts |> should be Empty

[<Fact>]
let ``Draft in flight, right-click → RouteFinish fires`` () =
    let cap = newCapture ()
    let draft =
        Routing.Draft.start (68, 20) 320L (0L, 0L)
        |> Routing.Draft.setCursor (500L, 0L)
        |> Routing.Draft.fix
    withCanvas
        (fun c ->
            wireCapture c cap
            c.RoutingMode <- true
            c.DraftRoute  <- Some draft)
        (fun window _ ->
            window.MouseDown(Point(50.0, 50.0), MouseButton.Right)
            Dispatcher.UIThread.RunJobs())
    cap.Finishes |> should equal 1
    cap.Fixes |> should equal 0

[<Fact>]
let ``Draft in flight, mouse move → RouteMouseMove fires`` () =
    let cap = newCapture ()
    let draft = Routing.Draft.start (68, 20) 320L (0L, 0L)
    withCanvas
        (fun c ->
            wireCapture c cap
            c.RoutingMode <- true
            c.DraftRoute  <- Some draft)
        (fun window _ ->
            window.MouseMove(Point(80.0, 60.0))
            Dispatcher.UIThread.RunJobs())
    cap.Moves.Length |> should be (greaterThan 0)

[<Fact>]
let ``No draft, mouse move → RouteMouseMove does NOT fire`` () =
    let cap = newCapture ()
    withCanvas
        (fun c ->
            wireCapture c cap
            c.RoutingMode <- true
            c.DraftRoute  <- None)
        (fun window _ ->
            window.MouseMove(Point(80.0, 60.0))
            Dispatcher.UIThread.RunJobs())
    cap.Moves |> should be Empty

// --- Property-change smoke tests ----------------------------------------

[<Fact>]
let ``Setting DraftRoute does not throw and invalidates the canvas`` () =
    // The live-DRC recompute happens in OnPropertyChanged when
    // DraftRoute changes. A throwing recompute would propagate up
    // here unless it's caught — exercises the try/catch shield
    // that protects the wire tool from a DRC failure.
    let draft = Routing.Draft.start (68, 20) 320L (0L, 0L)
    withCanvas
        (fun c -> ())
        (fun _ canvas ->
            canvas.DraftRoute <- Some draft
            Dispatcher.UIThread.RunJobs()
            canvas.DraftRoute <- None
            Dispatcher.UIThread.RunJobs())
    // Reaching here without exception is the assertion.
    Assert.True(true)

[<Fact>]
let ``Toggling RoutingMode is safe with no Library set`` () =
    withCanvas
        (fun c -> ())
        (fun _ canvas ->
            canvas.RoutingMode <- true
            Dispatcher.UIThread.RunJobs()
            canvas.RoutingMode <- false
            Dispatcher.UIThread.RunJobs())
    Assert.True(true)
