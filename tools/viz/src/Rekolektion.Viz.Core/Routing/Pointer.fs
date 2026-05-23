module Rekolektion.Viz.Core.Routing.Pointer

/// What the canvas should do in response to a pointer-press while
/// the routing tool is in some state. A pure decision separated
/// from the Canvas2D control so the matrix of click behaviours
/// can be unit-tested without an Avalonia harness.
type PointerAction =
    /// Begin a new draft route on `Layer` with `Width`, anchored
    /// at the world-coord `X`, `Y`. Fires when RoutingMode is on,
    /// the user left-clicks, and no draft is in flight yet.
    | StartRoute of Layer: (int * int) * Width: int64 * X: int64 * Y: int64
    /// Commit the tentative L of the in-flight draft as a fixed
    /// corner. Fires on a left-click in free space while a draft
    /// is active.
    | FixSegment
    /// Commit and end the in-flight draft. Fires on Enter, or on a
    /// left-click that lands on a labeled snap target (treat the
    /// pin as the route's endpoint). Right-click is reserved for
    /// canvas pan during wire mode and does NOT finish the route.
    /// Maps to RouteFinish at the dispatch layer.
    | Finish
    /// The click is not a routing action — the canvas should fall
    /// through to its normal selection / pan / etc. handling.
    | Ignore

/// Decide what should happen in response to a pointer press, given
/// the current routing state and the click details. Pure: no
/// dispatch, no I/O — the canvas wires the result into the Msg
/// pipeline.
///
/// `defaultLayer` is used as a fallback when `activeLayer` is None
/// and the user starts a new route — keeps the wire tool usable
/// out of the box (otherwise a fresh user who pressed W and clicked
/// would see nothing happen).
let decideAction
        (routingMode: bool)
        (draft: Draft.DraftRoute option)
        (activeLayer: (int * int) option)
        (isLeftClick: bool)
        (isRightClick: bool)
        (defaultLayer: int * int)
        (defaultWidth: int64)
        (worldPoint: int64 * int64)
        (onSnapTarget: bool)
        : PointerAction =
    let (x, y) = worldPoint
    // Right-click during wire mode is reserved for pan (the canvas
    // handler falls through to its pan path when this returns
    // Ignore). Enter and snap-target-landing left-click are the
    // commit paths.
    if (routingMode || draft.IsSome) && isLeftClick then
        match draft with
        | None ->
            let layer = activeLayer |> Option.defaultValue defaultLayer
            StartRoute(layer, defaultWidth, x, y)
        | Some _ when onSnapTarget ->
            // Click landed on a labeled pin — terminate the route
            // here. The user clicks the same pin again to start a
            // new route from it.
            Finish
        | Some _ ->
            FixSegment
    else
        Ignore
