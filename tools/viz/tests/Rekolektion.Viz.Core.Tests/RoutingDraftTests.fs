module Rekolektion.Viz.Core.Tests.RoutingDraftTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Routing

let private met1 = (68, 20)
let private width = 320L

[<Fact>]
let ``start initializes anchor, no cursor, no segments`` () =
    let r = Draft.start met1 width (0L, 0L)
    r.Points |> should equal [(0L, 0L)]
    r.Cursor |> should equal (None : (int64 * int64) option)
    Draft.fixedSegments r |> should be Empty
    Draft.tentativeSegments r |> should be Empty
    Draft.allSegments r |> should be Empty

[<Fact>]
let ``setCursor produces tentative L from anchor`` () =
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (1000L, 500L)
    r.Cursor |> should equal (Some (1000L, 500L))
    // Diagonal under HorizontalFirst posture → 2 rects.
    let segs = Draft.tentativeSegments r
    segs.Length |> should equal 2
    Draft.fixedSegments r |> should be Empty
    Draft.allSegments r |> should haveLength 2

[<Fact>]
let ``fix appends cursor to Points and clears cursor`` () =
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (1000L, 0L)
        |> Draft.fix
    r.Points |> should equal [(0L, 0L); (1000L, 0L)]
    r.Cursor |> should equal (None : (int64 * int64) option)
    // Fixed straight-line → 1 rect, no tentative.
    Draft.fixedSegments r |> should haveLength 1
    Draft.tentativeSegments r |> should be Empty

[<Fact>]
let ``fix is a no-op when cursor is None`` () =
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.fix
    r.Points |> should equal [(0L, 0L)]

[<Fact>]
let ``pop removes the last fixed point`` () =
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (1000L, 0L)
        |> Draft.fix
        |> Draft.setCursor (1000L, 500L)
        |> Draft.fix
        |> Draft.pop
    r.Points |> should equal [(0L, 0L); (1000L, 0L)]

[<Fact>]
let ``pop preserves the anchor (never removes it)`` () =
    let r = Draft.start met1 width (0L, 0L) |> Draft.pop |> Draft.pop
    r.Points |> should equal [(0L, 0L)]

[<Fact>]
let ``flipPosture alternates HorizontalFirst and VerticalFirst`` () =
    let r1 = Draft.start met1 width (0L, 0L)
    r1.Posture |> should equal Draft.HorizontalFirst
    let r2 = Draft.flipPosture r1
    r2.Posture |> should equal Draft.VerticalFirst
    let r3 = Draft.flipPosture r2
    r3.Posture |> should equal Draft.HorizontalFirst

[<Fact>]
let ``setCursor auto-flips posture to HorizontalFirst on dominant X motion`` () =
    // Starting posture VerticalFirst; user drags strongly right
    // (dx >> dy) → posture should flip to HorizontalFirst so the L
    // tracks the direction the cursor is moving in.
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.flipPosture          // VerticalFirst
        |> Draft.setCursor (10L, 5L)  // seed cursor; no flip (first set)
        |> Draft.setCursor (1000L, 6L) // dx=990, dy=1 → HorizontalFirst
    r.Posture |> should equal Draft.HorizontalFirst

[<Fact>]
let ``setCursor auto-flips posture to VerticalFirst on dominant Y motion`` () =
    let r =
        Draft.start met1 width (0L, 0L)  // HorizontalFirst by default
        |> Draft.setCursor (5L, 10L)
        |> Draft.setCursor (6L, 1000L)   // dy=990, dx=1 → VerticalFirst
    r.Posture |> should equal Draft.VerticalFirst

[<Fact>]
let ``setCursor keeps posture when motion is roughly diagonal`` () =
    // dx and dy comparable — neither axis dominates by 2× — posture
    // sticks. Prevents jitter on natural diagonal drags.
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (10L, 10L)
        |> Draft.setCursor (500L, 400L)  // dx=490, dy=390 → ratio < 2
    r.Posture |> should equal Draft.HorizontalFirst  // unchanged

[<Fact>]
let ``axis-aligned segment produces exactly one rect`` () =
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (1000L, 0L)
    let segs = Draft.tentativeSegments r
    segs.Length |> should equal 1
    let s = segs.[0]
    s.Layer |> should equal met1
    // 0 → 1000 horizontal sweep, width=320 → half=160; bbox y spans ±160.
    s.X1 |> should equal -160L
    s.X2 |> should equal 1160L
    s.Y1 |> should equal -160L
    s.Y2 |> should equal 160L

[<Fact>]
let ``diagonal segment produces two rects under HorizontalFirst`` () =
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (1000L, 500L)
    let segs = Draft.tentativeSegments r
    segs.Length |> should equal 2
    // First rect runs along Y=0 from X=0 to X=1000 (horizontal first).
    let h = segs.[0]
    h.X1 |> should equal -160L
    h.X2 |> should equal 1160L
    h.Y1 |> should equal -160L
    h.Y2 |> should equal 160L
    // Second rect runs along X=1000 from Y=0 to Y=500 (vertical second).
    let v = segs.[1]
    v.X1 |> should equal 840L
    v.X2 |> should equal 1160L
    v.Y1 |> should equal -160L
    v.Y2 |> should equal 660L

[<Fact>]
let ``diagonal segment produces two rects under VerticalFirst`` () =
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.flipPosture
        |> Draft.setCursor (1000L, 500L)
    let segs = Draft.tentativeSegments r
    segs.Length |> should equal 2
    // First rect runs along X=0 from Y=0 to Y=500 (vertical first).
    let v = segs.[0]
    v.X1 |> should equal -160L
    v.X2 |> should equal 160L
    v.Y1 |> should equal -160L
    v.Y2 |> should equal 660L

[<Fact>]
let ``degenerate zero-length segment produces no rects`` () =
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (0L, 0L)
    Draft.tentativeSegments r |> should be Empty

[<Fact>]
let ``finishSegments includes tentative when cursor set`` () =
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (1000L, 0L)
        |> Draft.fix
        |> Draft.setCursor (1000L, 500L)
    // Two fixed points + a tentative third → 1 fixed straight + 1 tentative straight.
    let segs = Draft.finishSegments r
    segs.Length |> should equal 2

[<Fact>]
let ``finishSegments uses just Points when cursor is None`` () =
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (1000L, 0L)
        |> Draft.fix
    Draft.finishSegments r |> should haveLength 1

// --- Edge cases --------------------------------------------------------

[<Fact>]
let ``pop after fix leaves cursor None`` () =
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (1000L, 0L)
        |> Draft.fix
        |> Draft.pop
    r.Points |> should equal [(0L, 0L)]
    r.Cursor |> should equal (None : (int64 * int64) option)

[<Fact>]
let ``flipPosture does NOT change committed segments (WYSIWYG commit)`` () =
    // Pre-2026-06-10 fixedSegments used the live r.Posture when
    // rendering committed pairs — so flipPosture after a commit
    // visually rotated the committed L's bend direction.  Bryan
    // reported that as "viz changed the committed route to
    // straight up" because auto-posture-lock on a subsequent
    // cursor move silently flipped Posture.  fix() now
    // materialises the L-corner at commit time so committed
    // segments are posture-independent; flipPosture only steers
    // the tentative draft going forward.
    let baseRoute =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (1000L, 500L)
        |> Draft.fix
    let before = Draft.fixedSegments baseRoute
    let after  = Draft.fixedSegments (Draft.flipPosture baseRoute)
    after |> should equal before

[<Fact>]
let ``setCursor twice without fix keeps only the latest cursor`` () =
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (100L, 100L)
        |> Draft.setCursor (500L, 500L)
    r.Cursor |> should equal (Some (500L, 500L))
    r.Points |> should equal [(0L, 0L)]

[<Fact>]
let ``allSegments concatenates fixed then tentative in order`` () =
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (1000L, 0L)
        |> Draft.fix
        |> Draft.setCursor (1000L, 500L)
    let fixed' = Draft.fixedSegments r
    let tent = Draft.tentativeSegments r
    let all = Draft.allSegments r
    all |> should haveLength (fixed'.Length + tent.Length)
    all.[0] |> should equal fixed'.[0]
    all.[fixed'.Length] |> should equal tent.[0]

[<Fact>]
let ``finishSegments on degenerate same-point fix produces no segments`` () =
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (0L, 0L)
    Draft.finishSegments r |> should be Empty

// --- Regression: committed L shape must not change after fix ---------
//
// Bryan reported 2026-06-10: ran a wire up, committed a corner, then
// went right; the previously-committed L re-rendered as a straight-up
// run because automatic posture flip (driven by the post-fix
// dominant-axis cursor motion) silently rotated the committed
// segments' L direction.  Root cause: `fixedSegments` builds rects
// pairwise using the CURRENT `r.Posture`, which can change between
// commit and render.  Fix: `fix` now materialises the L-corner under
// the posture active at commit time so committed pairs are axis-
// aligned and posture-independent thereafter.

[<Fact>]
let ``committed L-shape stays put after subsequent cursor flips posture`` () =
    // Posture VerticalFirst (locked) at commit time; the user then
    // moves cursor strongly in the X direction which would normally
    // flip Posture to HorizontalFirst.  Committed segments must NOT
    // change.
    let r0 =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (50L, 1000L)   // dominant Y → lock VerticalFirst
        |> Draft.fix                        // commit at (50, 1000)
    let segsBefore = Draft.fixedSegments r0
    // Now drag strongly right — would flip posture to HorizontalFirst.
    let r1 =
        r0
        |> Draft.setCursor (500L, 1010L)   // dominant X → flip
        |> Draft.setCursor (2000L, 1020L)  // hold the lock
    r1.Posture |> should equal Draft.HorizontalFirst
    let segsAfter = Draft.fixedSegments r1
    // The committed segments must be byte-identical — the user's
    // post-fix cursor motion does not rewrite committed history.
    segsAfter |> should equal segsBefore

[<Fact>]
let ``fix materialises an explicit corner under VerticalFirst posture`` () =
    // Starting from anchor (0, 0) → cursor (100, 500) under
    // VerticalFirst.  Post-fix Points should include an explicit
    // (0, 500) corner so the pair (0,0)-(0,500) renders as a
    // vertical leg and (0,500)-(100,500) as horizontal — independent
    // of `r.Posture` at render time.
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.flipPosture               // VerticalFirst
        |> Draft.setCursor (100L, 500L)
        |> Draft.fix
    r.Points |> should equal [(0L, 0L); (0L, 500L); (100L, 500L)]

[<Fact>]
let ``fix materialises an explicit corner under HorizontalFirst posture`` () =
    // Start defaults to HorizontalFirst.  A dominant-Y first move
    // would auto-lock posture to VerticalFirst, so seed with a
    // diagonal that doesn't trigger a lock and then commit while
    // HorizontalFirst still holds.
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (50L, 50L)      // diagonal, no lock
        |> Draft.setCursor (100L, 100L)    // still diagonal, no lock
        |> Draft.fix
    r.Posture |> should equal Draft.HorizontalFirst
    r.Points |> should equal [(0L, 0L); (100L, 0L); (100L, 100L)]

[<Fact>]
let ``fix does not insert a corner when last-point→cursor is axis-aligned`` () =
    // Same X — the (0,0)→(0,500) pair is already vertical, no corner
    // needed; the materialiser should be a no-op for axis-aligned cases.
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (0L, 500L)
        |> Draft.fix
    r.Points |> should equal [(0L, 0L); (0L, 500L)]

[<Fact>]
let ``toFlatPolygons emits closed 5-point rectangles`` () =
    let segs : Draft.DraftSegment list = [
        { Layer = met1; X1 = 0L; Y1 = 0L; X2 = 100L; Y2 = 50L }
    ]
    let flat = Draft.toFlatPolygons segs
    flat.[0].Points.Length |> should equal 5
    // First and last point coincide (closed polygon convention).
    flat.[0].Points.[0] |> should equal flat.[0].Points.[4]

// ---- ADR-0006 — walk-around corner integration -----------------

[<Fact>]
let ``new route starts with Auto = []`` () =
    let r = Draft.start met1 width (0L, 0L)
    r.Auto |> should be Empty

[<Fact>]
let ``setAuto stores the corner list on the route`` () =
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (1000L, 1000L)
        |> Draft.setAuto [ (500L, 0L); (500L, 1000L) ]
    r.Auto |> should equal [ (500L, 0L); (500L, 1000L) ]

[<Fact>]
let ``setCursor preserves Auto corners (live walk-around stays visible)`` () =
    // Cursor moves don't invalidate Auto — the most recent BG
    // walk-around result keeps rendering while the next compute
    // is in flight. Without this, mouse-move clobbers Auto faster
    // than the BG task can replace it and the tentative segment
    // degrades to a straight L for nearly the entire drag.
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (1000L, 1000L)
        |> Draft.setAuto [ (500L, 0L); (500L, 1000L) ]
        |> Draft.setCursor (2000L, 2000L)
    r.Auto |> should equal [ (500L, 0L); (500L, 1000L) ]
    r.Cursor |> should equal (Some (2000L, 2000L))

[<Fact>]
let ``tentativeSegments routes through Auto corners when set`` () =
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (1000L, 1000L)
        |> Draft.setAuto [ (500L, 0L); (500L, 1000L) ]
    // Polyline (0,0) → (500,0) → (500,1000) → (1000,1000): three
    // straight-axis links → three segments under any posture.
    let segs = Draft.tentativeSegments r
    segs.Length |> should equal 3

[<Fact>]
let ``tentativeSegments falls back to straight L when Auto is empty`` () =
    // No Auto = original two-segment L behaviour.
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (1000L, 500L)
    let segs = Draft.tentativeSegments r
    segs.Length |> should equal 2

[<Fact>]
let ``simplifyCollinear drops middle points sharing X with neighbours`` () =
    let pts = [ (0L, 0L); (0L, 100L); (0L, 200L); (50L, 200L) ]
    Draft.simplifyCollinear pts
    |> should equal [ (0L, 0L); (0L, 200L); (50L, 200L) ]

[<Fact>]
let ``simplifyCollinear drops middle points sharing Y with neighbours`` () =
    let pts = [ (0L, 50L); (100L, 50L); (200L, 50L); (200L, 0L) ]
    Draft.simplifyCollinear pts
    |> should equal [ (0L, 50L); (200L, 50L); (200L, 0L) ]

[<Fact>]
let ``simplifyCollinear collapses long stair-step on same axis to one segment`` () =
    // 4 collinear V points get merged to just the endpoints.
    let pts = [ (0L, 0L); (0L, 100L); (0L, 250L); (0L, 500L); (100L, 500L) ]
    Draft.simplifyCollinear pts
    |> should equal [ (0L, 0L); (0L, 500L); (100L, 500L) ]

[<Fact>]
let ``simplifyCollinear preserves real corners`` () =
    let pts = [ (0L, 0L); (0L, 100L); (50L, 100L); (50L, 200L) ]
    Draft.simplifyCollinear pts
    |> should equal pts

[<Fact>]
let ``fix glues Auto corners into Points and clears Auto`` () =
    // The auto-router's `Auto` corner list is the visible preview
    // between the last fixed point and the cursor. The user expects
    // clicking to commit what they see — so the fix locks in every
    // Auto corner plus the cursor.
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (1000L, 1000L)
        |> Draft.setAuto [ (500L, 0L); (500L, 1000L) ]
        |> Draft.fix
    r.Points |> should equal [ (0L, 0L); (500L, 0L); (500L, 1000L); (1000L, 1000L) ]
    r.Cursor |> should equal (None : (int64 * int64) option)
    r.Auto |> should be Empty

[<Fact>]
let ``finishSegments commits the auto-routed polyline (Auto corners included)`` () =
    // RouteFinish writes what the user sees — the live preview's
    // polyline includes Auto corners between last-fixed and cursor.
    let r =
        Draft.start met1 width (0L, 0L)
        |> Draft.setCursor (1000L, 1000L)
        |> Draft.setAuto [ (500L, 0L); (500L, 1000L) ]
    let segs = Draft.finishSegments r
    // (0,0)→(500,0), (500,0)→(500,1000), (500,1000)→(1000,1000) — 3 pairs.
    segs.Length |> should equal 3
