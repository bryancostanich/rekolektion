/// Background-task scheduling for live DRC and the walk-around
/// router.
///
/// The wire-drawing path must never block the UI thread on the DRC
/// or walk-around recompute. This module owns:
///
///   1. A monotonic version counter so stale results are dropped.
///   2. **Cooperative cancellation** of in-flight computes
///      (`schedule`). Each call cancels the previous task's
///      CancellationToken and starts a fresh task with a new token.
///      The cancelled task is expected to poll its token at hot
///      loops and bail out fast.
///   3. **Coalescing dispatch** (`scheduleCoalesce`) for work where
///      cancellation is counter-productive (long builds that don't
///      poll the token). In-flight work is NOT cancelled; latest
///      inputs are stored and run when the current task finishes.
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
      /// Latest pending (compute, onAccept, version) for coalescing
      /// mode (`scheduleCoalesce`). Each call overwrites the previous
      /// pending so only the latest survives.
      mutable Pending : ((System.Threading.CancellationToken -> 'T) * ('T -> unit) * int) option
      /// True when a task is running in coalescing mode.
      mutable Running : bool
      /// Lock guarding all mutable fields above. The body of
      /// `compute` runs OUTSIDE the lock.
      Lock : obj }

let create<'T> (initial : 'T) : State<'T> =
    { Version = 0
      Latest = initial
      CurrentCts = None
      Pending = None
      Running = false
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

let rec private runCoalesced
    (state    : State<'T>)
    (postBack : (unit -> unit) -> unit) : unit =
    let work =
        lock state.Lock (fun () ->
            match state.Pending with
            | Some (c, o, v) ->
                state.Pending <- None
                Some (c, o, v)
            | None ->
                state.Running <- false
                None)
    match work with
    | None -> ()
    | Some (compute, onAccept, captured) ->
        let result =
            try Some (compute System.Threading.CancellationToken.None)
            with
            | :? System.OperationCanceledException -> None
            | _ -> None
        postBack (fun () ->
            match result with
            | Some r ->
                tryAccept state captured r onAccept |> ignore
            | None -> ()
            System.Threading.Tasks.Task.Run(fun () ->
                runCoalesced state postBack)
            |> ignore)

/// Schedule a compute with coalescing semantics — does NOT cancel
/// in-flight work. If a task is already running, `compute`/`onAccept`
/// are stored as pending. When the current task finishes, the LATEST
/// pending pair runs next (intermediate calls are coalesced away).
/// Suitable for long-running computes where cancellation would
/// prevent any result from reaching the caller (e.g. walk-around
/// routing with a ~700ms cold graph build).
let scheduleCoalesce
    (state    : State<'T>)
    (compute  : System.Threading.CancellationToken -> 'T)
    (postBack : (unit -> unit) -> unit)
    (onAccept : 'T -> unit) : int =
    let captured =
        lock state.Lock (fun () ->
            let v = bumpVersion state
            state.Pending <- Some (compute, onAccept, v)
            if not state.Running then
                state.Running <- true
                System.Threading.Tasks.Task.Run(fun () ->
                    runCoalesced state postBack)
                |> ignore
            v)
    captured
