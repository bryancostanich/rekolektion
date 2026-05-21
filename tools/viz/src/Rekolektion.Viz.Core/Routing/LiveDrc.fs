/// Background-task scheduling for live DRC.
///
/// The wire-drawing path must never block the UI thread on the DRC
/// recompute (1.4–1.5 s on real cells). This module owns a
/// monotonic version counter and a thread-pool dispatch helper so
/// that:
///   1. Each `schedule` call returns immediately — the compute
///      itself runs on the thread pool.
///   2. When two computes are in flight, only the result from the
///      most-recent `schedule` is written back; older results are
///      dropped via the version-counter check.
///
/// `postBack` is injected so production can route through
/// `Dispatcher.UIThread.Post` while tests run synchronously.
module Rekolektion.Viz.Core.Routing.LiveDrc

type State<'T> =
    { mutable Version : int
      mutable Latest  : 'T }

let create<'T> (initial : 'T) : State<'T> =
    { Version = 0; Latest = initial }

/// Increment the counter and return the new version.
let bumpVersion (state : State<'T>) : int =
    state.Version <- state.Version + 1
    state.Version

/// Apply `result` to `state.Latest` and run `onAccept` only if
/// `captured` is still the latest version. Returns true on accept,
/// false on stale-drop.
let tryAccept
    (state    : State<'T>)
    (captured : int)
    (result   : 'T)
    (onAccept : 'T -> unit) : bool =
    if captured = state.Version then
        state.Latest <- result
        onAccept result
        true
    else
        false

/// Bump the version, kick `compute` off on the thread pool, then
/// route the writeback through `postBack`. The writeback path is
/// guarded by `tryAccept`, so a stale compute (overtaken by a
/// newer schedule) silently drops its result.
let schedule
    (state    : State<'T>)
    (compute  : unit -> 'T)
    (postBack : (unit -> unit) -> unit)
    (onAccept : 'T -> unit) : int =
    let captured = bumpVersion state
    System.Threading.Tasks.Task.Run(fun () ->
        let result = compute ()
        postBack (fun () ->
            tryAccept state captured result onAccept |> ignore))
    |> ignore
    captured
