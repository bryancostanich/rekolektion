module Rekolektion.Viz.Core.Tests.RoutingPointerTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Routing

let private met1 = (68, 20)
let private met2 = (69, 20)
let private defaultLayer = met1
let private defaultWidth = 320L
let private worldPoint = (100L, 200L)

let private decide
        routingMode draft activeLayer left right =
    Pointer.decideAction
        routingMode draft activeLayer left right
        defaultLayer defaultWidth worldPoint false

let private decideOnSnap
        routingMode draft activeLayer left right =
    Pointer.decideAction
        routingMode draft activeLayer left right
        defaultLayer defaultWidth worldPoint true

let private startedDraft () : Draft.DraftRoute =
    Draft.start met1 320L (0L, 0L)
    |> Draft.setCursor (500L, 0L)
    |> Draft.fix

// --- No-op cases ---------------------------------------------------------

[<Fact>]
let ``RoutingMode off, no draft, left-click → Ignore`` () =
    decide false None (Some met1) true false
    |> should equal Pointer.Ignore

[<Fact>]
let ``RoutingMode off, no draft, right-click → Ignore`` () =
    decide false None (Some met1) false true
    |> should equal Pointer.Ignore

[<Fact>]
let ``RoutingMode on, no draft, no click (drag-only) → Ignore`` () =
    decide true None (Some met1) false false
    |> should equal Pointer.Ignore

[<Fact>]
let ``Right-click without a draft → Ignore`` () =
    // Right-click is reserved for pan; not a routing action.
    decide true None (Some met1) false true
    |> should equal Pointer.Ignore

// --- StartRoute cases ----------------------------------------------------

[<Fact>]
let ``RoutingMode on, no draft, left-click, ActiveLayer Some → StartRoute on that layer`` () =
    decide true None (Some met2) true false
    |> should equal (Pointer.StartRoute (met2, defaultWidth, 100L, 200L))

[<Fact>]
let ``RoutingMode on, no draft, left-click, ActiveLayer None → StartRoute on default layer`` () =
    // Regression: the "wire doesn't work" UX bug — no active layer
    // used to silent no-op. Default fallback keeps the tool usable.
    decide true None None true false
    |> should equal (Pointer.StartRoute (defaultLayer, defaultWidth, 100L, 200L))

[<Fact>]
let ``StartRoute carries the world-coord click point through`` () =
    let action =
        Pointer.decideAction true None (Some met1)
            true false defaultLayer defaultWidth (42L, -17L) false
    action |> should equal (Pointer.StartRoute (met1, defaultWidth, 42L, -17L))

[<Fact>]
let ``StartRoute carries the default width through`` () =
    let action =
        Pointer.decideAction true None (Some met1)
            true false defaultLayer 170L (0L, 0L) false
    match action with
    | Pointer.StartRoute (_, w, _, _) -> w |> should equal 170L
    | other -> failwithf "expected StartRoute, got %A" other

// --- In-flight draft cases -----------------------------------------------

[<Fact>]
let ``Draft Some, left-click → FixSegment`` () =
    decide true (Some (startedDraft ())) (Some met1) true false
    |> should equal Pointer.FixSegment

[<Fact>]
let ``Draft Some, right-click → Ignore (right-click is reserved for pan)`` () =
    decide true (Some (startedDraft ())) (Some met1) false true
    |> should equal Pointer.Ignore

[<Fact>]
let ``Draft Some still dispatches even after RoutingMode toggled off`` () =
    // Leaves an in-flight draft completable even if the user
    // somehow flipped the mode off mid-route. Avoids stranding
    // unfixed segments.
    decide false (Some (startedDraft ())) (Some met1) true false
    |> should equal Pointer.FixSegment

[<Fact>]
let ``Both buttons pressed during a draft → FixSegment (left wins; right is pan)`` () =
    // Both buttons pressed simultaneously — left-click wins as a
    // free-space FixSegment. Right-click is reserved for pan and
    // doesn't commit. (Use Enter to finish, or land on snap target.)
    decide true (Some (startedDraft ())) (Some met1) true true
    |> should equal Pointer.FixSegment

[<Fact>]
let ``Draft Some, left-click on a snap target → Finish`` () =
    // Landing the wire on a labeled pin terminates the route. To
    // continue, the user must click the same pin again — that
    // starts a fresh draft (no draft = StartRoute).
    decideOnSnap true (Some (startedDraft ())) (Some met1) true false
    |> should equal Pointer.Finish

[<Fact>]
let ``Draft Some, left-click in free space (no snap target) → FixSegment`` () =
    // Explicit complement of the snap-target case: free-space click
    // adds a corner instead of committing.
    decide true (Some (startedDraft ())) (Some met1) true false
    |> should equal Pointer.FixSegment
