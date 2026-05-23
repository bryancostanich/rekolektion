/// Background-task scheduling for live DRC and the walk-around
/// router.
///
/// The wire-drawing path must never block the UI thread on the DRC
/// or walk-around recompute (1.4–1.5 s on real cells, 100 ms on
/// every cursor frame for the walk-around). This module owns:
///
///   1. A monotonic version counter so stale results are dropped.
///   2. A **single-flight + coalesce** dispatch policy: at most ONE
///      compute is in flight at any time; while it runs, additional
///      `schedule` calls overwrite a single `Pending` slot. When the
///      in-flight task finishes, the writeback drops if a newer
///      schedule arrived (version mismatch) and then re-fires the
///      most-recent pending compute.
///
/// The earlier pattern (one `Task.Run` per call, version-counter
/// drop at writeback only) leaked CPU: a 200 ms drag fired 76
/// Tasks, all ran their full visibility-graph build, results all
/// dropped except the last — but the user still waited ~4.5 s for
/// the thread pool to drain. Coalescing at dispatch keeps the
/// in-flight count at 1 and serves the latest cursor as soon as the
/// current compute returns.
///
/// `postBack` is injected so production can route writebacks
/// through `Dispatcher.UIThread.Post` while tests run synchronously.
module Rekolektion.Viz.Core.Routing.LiveDrc

type State<'T> =
    { mutable Version : int
      mutable Latest  : 'T
      /// True while a compute is on the thread pool. New `schedule`
      /// calls during this window stash their closure in `Pending`
      /// instead of starting a fresh Task.
      mutable Running : bool
      /// Most-recent compute closure that arrived while `Running`.
      /// Overwritten by every subsequent schedule — only the latest
      /// closure survives, matching the "drop stale" intent at the
      /// dispatch layer instead of the writeback layer.
      mutable Pending : (unit -> 'T) option
      /// Latest writeback callback to pair with `Pending`. Stored
      /// alongside so the re-fire path uses the caller's most-recent
      /// onAccept rather than the one captured when the in-flight
      /// task started.
      mutable PendingOnAccept : ('T -> unit) option
      /// Latest `postBack` for the pending compute. Stored so the
      /// re-fire path posts to the caller's most-recent dispatch
      /// (in production these are all `Dispatcher.UIThread.Post`;
      /// in tests each schedule passes its own, and the chained
      /// run must use the pending one's).
      mutable PendingPostBack : ((unit -> unit) -> unit) option
      /// Lock guarding all mutable fields above. The body of
      /// `compute` runs OUTSIDE the lock.
      Lock : obj }

let create<'T> (initial : 'T) : State<'T> =
    { Version = 0
      Latest = initial
      Running = false
      Pending = None
      PendingOnAccept = None
      PendingPostBack = None
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

/// Single-flight + coalesce dispatch. Returns the version captured
/// for THIS call (matches the old API).
///
/// Behaviour:
///   - No compute running → bump version, mark Running, start the
///     Task. Captured version travels with the Task for the
///     writeback drop.
///   - Compute already running → bump version, store this
///     (compute, onAccept) pair as Pending (overwriting any prior
///     pending). The in-flight Task is left alone; its writeback
///     will drop (version mismatch) and then re-fire the latest
///     Pending.
let rec schedule
    (state    : State<'T>)
    (compute  : unit -> 'T)
    (postBack : (unit -> unit) -> unit)
    (onAccept : 'T -> unit) : int =
    let startNow, captured =
        lock state.Lock (fun () ->
            let v = bumpVersion state
            if state.Running then
                state.Pending <- Some compute
                state.PendingOnAccept <- Some onAccept
                state.PendingPostBack <- Some postBack
                false, v
            else
                state.Running <- true
                true, v)
    if startNow then
        runTask state captured compute postBack onAccept
    captured

and private runTask
    (state    : State<'T>)
    (captured : int)
    (compute  : unit -> 'T)
    (postBack : (unit -> unit) -> unit)
    (onAccept : 'T -> unit) : unit =
    System.Threading.Tasks.Task.Run(fun () ->
        let result =
            try compute ()
            with _ -> state.Latest
        postBack (fun () ->
            tryAccept state captured result onAccept |> ignore
            // Coalesce step. After the writeback settles on the UI
            // thread, see if a newer schedule arrived while we ran.
            // If so, pop it and chain another Task.Run with the
            // current Version as its captured value — the chained
            // task is the "latest one wins" pass.
            let nextWork =
                lock state.Lock (fun () ->
                    match state.Pending with
                    | Some c ->
                        let oa = state.PendingOnAccept
                        let pb = state.PendingPostBack
                        state.Pending <- None
                        state.PendingOnAccept <- None
                        state.PendingPostBack <- None
                        // Running stays true — we're handing off
                        // straight to the next Task. Capture the
                        // current Version: it's the version that
                        // was bumped when the Pending was stashed,
                        // unless further schedules bumped it again
                        // (in which case those also overwrote
                        // Pending, so this still routes to the
                        // freshest closure).
                        Some (c, oa, pb, state.Version)
                    | None ->
                        state.Running <- false
                        None)
            match nextWork with
            | Some (c, oa, pb, v) ->
                let oa' = defaultArg oa onAccept
                let pb' = defaultArg pb postBack
                runTask state v c pb' oa'
            | None -> ()))
    |> ignore
