/// Shared camera state for the 2D canvas and the surrounding
/// viewport rulers. The canvas owns pan / zoom / resize and pushes
/// the resulting state here on every change; the rulers subscribe
/// to `Changed` and `InvalidateVisual` themselves so they redraw
/// the tick gutters at the new zoom without round-tripping through
/// the FuncUI dispatch loop (which would diff the entire view
/// tree at every pan tick — ~60 Hz during a drag).
///
/// Single shared `Snapshot` because the app hosts one main 2D
/// canvas + window. If we ever go multi-canvas this turns into a
/// per-canvas record on the canvas control itself, but for now a
/// module-level singleton matches the rest of `Services` (Config,
/// Recents, etc.).
module Rekolektion.Viz.App.Services.ViewportSync

/// Camera + viewport size snapshot. Center is in DBU (canvas's
/// native unit); `PixelsPerDbu` is the on-screen zoom; `DbuNm`
/// bridges DBU → µm for the ruler tick math; `PixelW` / `PixelH`
/// are the canvas's current render-surface size in pixels.
type Snapshot = {
    CenterDbuX   : float
    CenterDbuY   : float
    PixelsPerDbu : float
    DbuNm        : int
    PixelW       : float
    PixelH       : float
}

let empty : Snapshot = {
    CenterDbuX   = 0.0
    CenterDbuY   = 0.0
    PixelsPerDbu = 1.0
    DbuNm        = 1
    PixelW       = 0.0
    PixelH       = 0.0
}

let private state = ref empty
let private changed = Event<Snapshot>()

/// Current camera snapshot. Cheap value-copy read — safe to call
/// from any thread but Avalonia render passes only.
let current () : Snapshot = !state

/// Subscribe to camera changes. Subscribers should
/// `InvalidateVisual` themselves; the bus does no rendering.
let onChanged : IEvent<Snapshot> = changed.Publish

/// Replace the snapshot. No-op when the new value equals the
/// current one — keeps redundant fires from the canvas's
/// per-frame state writes from cascading through the subscribers.
let update (s: Snapshot) : unit =
    if s <> !state then
        state := s
        changed.Trigger s
