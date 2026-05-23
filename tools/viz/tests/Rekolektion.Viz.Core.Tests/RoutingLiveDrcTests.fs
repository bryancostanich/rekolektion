module Rekolektion.Viz.Core.Tests.RoutingLiveDrcTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Routing

// ---- Pure version-counter unit tests ----------------------------

[<Fact>]
let ``new state starts at version 0 with the supplied initial value`` () =
    let s = LiveDrc.create 42
    s.Version |> should equal 0
    s.Latest  |> should equal 42

[<Fact>]
let ``bumpVersion is monotonic and returns the new version`` () =
    let s = LiveDrc.create 0
    LiveDrc.bumpVersion s |> should equal 1
    LiveDrc.bumpVersion s |> should equal 2
    LiveDrc.bumpVersion s |> should equal 3
    s.Version |> should equal 3

[<Fact>]
let ``tryAccept writes Latest and fires onAccept when version matches`` () =
    let s = LiveDrc.create 0
    let captured = LiveDrc.bumpVersion s
    let fired = ref 0
    let accepted = LiveDrc.tryAccept s captured 99 (fun _ -> incr fired)
    accepted |> should equal true
    s.Latest |> should equal 99
    !fired   |> should equal 1

[<Fact>]
let ``tryAccept drops the result and does NOT fire onAccept when version is stale`` () =
    let s = LiveDrc.create 7
    let captured = LiveDrc.bumpVersion s    // = 1
    LiveDrc.bumpVersion s |> ignore         // bump again → 2, captured is now stale
    let fired = ref 0
    let accepted = LiveDrc.tryAccept s captured 99 (fun _ -> incr fired)
    accepted |> should equal false
    s.Latest |> should equal 7              // untouched
    !fired   |> should equal 0

// ---- Sync-postBack integration tests ----------------------------
//
// Inline `postBack` (run the action immediately on the calling
// thread). This isolates the scheduling + writeback contract from
// the Avalonia Dispatcher used in production.

/// Block until the background task has called `postBack` (or time
/// out — keeps a stuck test from hanging CI). The barrier is set
/// from inside `compute` so we know the Task.Run path actually ran.
let private waitFor (signal : ManualResetEventSlim) =
    signal.Wait(TimeSpan.FromSeconds 5.0) |> should equal true

[<Fact>]
let ``schedule bumps the version and returns it synchronously`` () =
    let s = LiveDrc.create 0
    let done_ = new ManualResetEventSlim(false)
    let captured =
        LiveDrc.schedule
            s
            (fun () -> 123)
            (fun action -> action (); done_.Set())
            ignore
    captured |> should equal 1
    s.Version |> should equal 1
    waitFor done_
    s.Latest |> should equal 123

[<Fact>]
let ``schedule returns BEFORE the compute finishes`` () =
    let s = LiveDrc.create 0
    let computeStarted = new ManualResetEventSlim(false)
    let releaseCompute = new ManualResetEventSlim(false)
    let writebackDone  = new ManualResetEventSlim(false)
    let compute () =
        computeStarted.Set()
        // Hold the compute open until the test thread releases us.
        releaseCompute.Wait(TimeSpan.FromSeconds 5.0) |> ignore
        77
    let postBack action =
        action (); writebackDone.Set()
    let captured = LiveDrc.schedule s compute postBack ignore
    // Confirm the compute is still in-flight when `schedule`
    // returned — i.e. the call did not block on it.
    computeStarted.Wait(TimeSpan.FromSeconds 5.0) |> should equal true
    captured |> should equal 1
    s.Latest |> should equal 0    // not yet written back
    releaseCompute.Set()
    waitFor writebackDone
    s.Latest |> should equal 77

[<Fact>]
let ``single-flight + coalesce: pending schedule waits for the in-flight one, then runs once the slow compute returns`` () =
    // Contract under single-flight: at most one compute runs at a
    // time. A schedule arriving while another is in flight stashes
    // its closure as Pending instead of starting a parallel Task.
    // When the in-flight task finishes:
    //   - its writeback runs but DROPS via tryAccept (version moved
    //     on), so its onAccept never fires;
    //   - the pending closure is dequeued and run via its OWN
    //     postBack + onAccept (the latest one wins).
    let s = LiveDrc.create 0
    let firstStarted   = new ManualResetEventSlim(false)
    let firstReleased  = new ManualResetEventSlim(false)
    let firstWriteback = new ManualResetEventSlim(false)
    let firstAccept    = ref false
    let slowCompute () =
        firstStarted.Set()
        firstReleased.Wait(TimeSpan.FromSeconds 5.0) |> ignore
        100
    let firstPostBack action = action (); firstWriteback.Set()
    let v1 = LiveDrc.schedule s slowCompute firstPostBack (fun _ -> firstAccept := true)
    firstStarted.Wait(TimeSpan.FromSeconds 5.0) |> should equal true
    // Second schedule arrives while the first compute is gated. It
    // does NOT start a parallel Task — its closure is stashed in
    // Pending. secondWriteback only fires after the slow compute
    // finishes AND the chained pending run completes.
    let secondWriteback = new ManualResetEventSlim(false)
    let secondAccept    = ref false
    let secondValue     = ref 0
    let v2 =
        LiveDrc.schedule
            s
            (fun () -> 200)
            (fun action -> action (); secondWriteback.Set())
            (fun v -> secondAccept := true; secondValue := v)
    v1 |> should equal 1
    v2 |> should equal 2
    // The pending hasn't run yet — Latest is still the initial 0
    // because the in-flight slow compute is gated.
    s.Latest |> should equal 0
    !secondAccept |> should equal false
    // Release the slow compute. Its writeback drops (version moved
    // from 1 to 2), then the chained pending fires through the
    // SECOND postBack / onAccept.
    firstReleased.Set()
    waitFor firstWriteback        // slow compute's writeback ran
    waitFor secondWriteback       // pending writeback ran (latest wins)
    s.Latest |> should equal 200
    !firstAccept |> should equal false    // older onAccept dropped
    !secondAccept |> should equal true
    !secondValue |> should equal 200

[<Fact>]
let ``onAccept fires exactly once per accepted writeback`` () =
    let s = LiveDrc.create 0
    let accepts = ref 0
    let done_ = new ManualResetEventSlim(false)
    LiveDrc.schedule
        s
        (fun () -> 1)
        (fun action -> action (); done_.Set())
        (fun _ -> incr accepts)
    |> ignore
    waitFor done_
    !accepts |> should equal 1
