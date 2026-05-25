/// Background-task scheduling for live DRC and the walk-around
/// router.
///
/// The wire-drawing path must never block the UI thread on the DRC
/// or walk-around recompute. This module owns:
///
///   1. A monotonic version counter so stale results are dropped.
///   2. **Cooperative cancellation** of in-flight computes. Each
///      `schedule` call cancels the previous task's CancellationToken
///      and starts a fresh task with a new token. The cancelled
///      task is expected to poll its token (e.g. via
///      `ct.ThrowIfCancellationRequested()`) at hot loops and
///      bail out fast — much faster than waiting for it to finish
///      a useless computation against stale input.
///
/// The earlier pattern (single-flight + coalesce, no cancellation)
/// serialized computes: a 700 ms search against the latest cursor
/// only started after the prior search against an obsolete cursor
/// ran to completion. On dense macros, three serial computes for
/// the same final cursor took ~2.5 s when a single one was needed.
/// With cancellation the chain collapses: each new schedule kills
/// the in-flight work immediately and runs only the most recent
/// inputs.
///
/// `postBack` is injected so production can route writebacks
/// through `Dispatcher.UIThread.Post` while tests run synchronously.
module Rekolektion.Viz.Core.Routing.LiveDrc

type State<'T> =
    { mutable Version : int
      mutable Latest  : 'T
      /// CancellationTokenSource for the in-flight compute (if any).
      /// `schedule` cancels this and replaces it with a fresh one,
      /// so the running task observes cancellation via its CT and
      /// bails out at the next poll point.
      mutable CurrentCts : System.Threading.CancellationTokenSource option
      /// Lock guarding all mutable fields above. The body of
      /// `compute` runs OUTSIDE the lock.
      Lock : obj }

let create<'T> (initial : 'T) : State<'T> =
    { Version = 0
      Latest = initial
      CurrentCts = None
      Lock = obj() }

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

/// Schedule a compute. If a compute is already in flight, its
/// CancellationToken is signalled — the running task is expected
/// to poll the token (e.g. `ct.ThrowIfCancellationRequested()`)
/// at hot loops and throw `OperationCanceledException` quickly.
/// A fresh task starts immediately with a new token; the cancelled
/// task's eventual postBack is a no-op because its token reports
/// cancellation.
///
/// `compute` receives the token so the caller can thread it down
/// into the inner work (graph build, Dijkstra, etc).
let schedule
    (state    : State<'T>)
    (compute  : System.Threading.CancellationToken -> 'T)
    (postBack : (unit -> unit) -> unit)
    (onAccept : 'T -> unit) : int =
    let cts, captured =
        lock state.Lock (fun () ->
            // Cancel any in-flight compute (no-op if none).
            match state.CurrentCts with
            | Some old -> old.Cancel()
            | None -> ()
            let v = bumpVersion state
            let newCts = new System.Threading.CancellationTokenSource()
            state.CurrentCts <- Some newCts
            newCts, v)
    System.Threading.Tasks.Task.Run(fun () ->
        let result =
            try Some (compute cts.Token)
            with
            | :? System.OperationCanceledException -> None
            | _ -> None
        // Always invoke postBack so the UI thread runs the
        // writeback (cheap when no result). Skip applying when
        // our token was cancelled by a subsequent schedule.
        postBack (fun () ->
            if not cts.Token.IsCancellationRequested then
                match result with
                | Some r ->
                    tryAccept state captured r onAccept |> ignore
                | None -> ()))
    |> ignore
    captured
