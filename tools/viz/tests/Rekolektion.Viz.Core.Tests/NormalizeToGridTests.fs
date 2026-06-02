module Rekolektion.Viz.Core.Tests.NormalizeToGridTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout

// Element builders reuse the shape from InstancesLabelFollowingTests
// / ZeroOriginTests — same minimal sky130 Layer-tagged
// constructors so the tests focus on the round-to-grid behaviour
// rather than fixture noise.

let private rect (x1, y1, x2, y2) : Element =
    RectEl {
        Layer = Named ("sky130", "met1")
        X1 = x1; Y1 = y1; X2 = x2; Y2 = y2
        Net = None
        Props = []
        Comments = []
        SubFormComments = Map.empty
    }

let private poly (pts: (int64 * int64) list) : Element =
    PolyEl {
        Layer = Named ("sky130", "met1")
        Points = pts |> List.map (fun (x, y) -> { X = x; Y = y })
        Net = None
        Props = []
        Comments = []
        SubFormComments = Map.empty
    }

let private path (w: int64) (pts: (int64 * int64) list) : Element =
    PathEl {
        Layer = Named ("sky130", "met1")
        Width = w
        Points = pts |> List.map (fun (x, y) -> { X = x; Y = y })
        Net = None
        Cap = None
        Props = []
        Comments = []
        SubFormComments = Map.empty
    }

let private sref (cell: string) (ox: int64) (oy: int64) : Element =
    SRefEl {
        Cell = cell
        Origin = { X = ox; Y = oy }
        Rot = 0.0; Mag = 1.0; Reflect = false
        Props = []; Comments = []
        SubFormComments = Map.empty
    }

let private aref (cell: string) (ox: int64) (oy: int64)
                 (cols: int) (rows: int)
                 (cpX: int64, cpY: int64)
                 (rpX: int64, rpY: int64) : Element =
    ARefEl {
        Cell = cell
        Origin = { X = ox; Y = oy }
        Cols = cols; Rows = rows
        ColPitch = { X = cpX; Y = cpY }
        RowPitch = { X = rpX; Y = rpY }
        Rot = 0.0; Mag = 1.0; Reflect = false
        Props = []; Comments = []
        SubFormComments = Map.empty
    }

let private label (text: string) (ox: int64) (oy: int64) : Element =
    LabelEl {
        Layer = Named ("sky130", "met1_label")
        Text = text
        Origin = { X = ox; Y = oy }
        Class = None
        Props = []
        Comments = []
        SubFormComments = Map.empty
        IsInternal = false
        Kind = NetName
    }

let private mkCell name elements : Cell =
    { Name = name; Meta = None; Elements = elements
      Comments = []; SubFormComments = Map.empty }

let private mkDoc cells : Document =
    { emptyDocument with Cells = cells; TopCell = Some "top"; Pdk = "sky130" }

let private topElements (doc: Document) : Element list =
    doc.Cells |> List.find (fun c -> c.Name = "top") |> _.Elements

// ─────────────────────────────────────────────────────────────────
// Per-element round behaviour
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``SRef Origin snaps to nearest grid point`` () =
    // sky130 grid = 5L. 12 -> 10 (half-away-from-zero on +2 below half).
    let doc = mkDoc [ mkCell "top" [ sref "sub" 12L 18L ] ]
    let doc' = Instances.normalizeToGrid doc
    match topElements doc' |> List.head with
    | SRefEl s ->
        s.Origin.X |> should equal 10L
        // 18 + 2(half) = 20, /5 = 4, *5 = 20.
        s.Origin.Y |> should equal 20L
    | other -> failwithf "expected SRef, got %A" other

[<Fact>]
let ``Rect corners snap independently`` () =
    let doc = mkDoc [ mkCell "top" [ rect (3L, 7L, 27L, 33L) ] ]
    let doc' = Instances.normalizeToGrid doc
    match topElements doc' |> List.head with
    | RectEl r ->
        r.X1 |> should equal 5L
        r.Y1 |> should equal 5L
        r.X2 |> should equal 25L
        r.Y2 |> should equal 35L
    | other -> failwithf "expected Rect, got %A" other

[<Fact>]
let ``Poly vertices snap independently`` () =
    let doc = mkDoc [ mkCell "top" [ poly [ 1L,4L; 11L,12L; 23L,9L ] ] ]
    let doc' = Instances.normalizeToGrid doc
    match topElements doc' |> List.head with
    | PolyEl p ->
        p.Points
        |> List.map (fun pt -> pt.X, pt.Y)
        |> should equal [ 0L,5L; 10L,10L; 25L,10L ]
    | other -> failwithf "expected Poly, got %A" other

[<Fact>]
let ``Path vertices snap; width unchanged`` () =
    let doc = mkDoc [ mkCell "top" [ path 137L [ 2L,3L; 12L,14L ] ] ]
    let doc' = Instances.normalizeToGrid doc
    match topElements doc' |> List.head with
    | PathEl p ->
        p.Width |> should equal 137L
        p.Points
        |> List.map (fun pt -> pt.X, pt.Y)
        |> should equal [ 0L,5L; 10L,15L ]
    | other -> failwithf "expected Path, got %A" other

[<Fact>]
let ``Label Origin snaps`` () =
    let doc = mkDoc [ mkCell "top" [ label "NET" 13L 21L ] ]
    let doc' = Instances.normalizeToGrid doc
    match topElements doc' |> List.head with
    | LabelEl l ->
        l.Origin.X |> should equal 15L
        l.Origin.Y |> should equal 20L
    | other -> failwithf "expected Label, got %A" other

[<Fact>]
let ``ARef Origin + ColPitch + RowPitch all snap independently`` () =
    let doc = mkDoc [ mkCell "top" [ aref "sub" 1L 4L 4 3 (403L, 6L) (2L, 502L) ] ]
    let doc' = Instances.normalizeToGrid doc
    match topElements doc' |> List.head with
    | ARefEl a ->
        a.Origin.X    |> should equal 0L
        a.Origin.Y    |> should equal 5L
        a.ColPitch.X  |> should equal 405L
        a.ColPitch.Y  |> should equal 5L
        a.RowPitch.X  |> should equal 0L
        a.RowPitch.Y  |> should equal 500L
    | other -> failwithf "expected ARef, got %A" other

// ─────────────────────────────────────────────────────────────────
// Cross-cutting contracts
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``half-away-from-zero rounding is sign-symmetric`` () =
    // sky130 grid = 5 → integer-half = 2.  The crossover is the
    // exact midpoint 2.5; integers above it round away from
    // zero, below it round toward zero, and the symmetry
    // snap(-v) = -snap(v) holds either way.  Matters for
    // rotated SRefs whose negative coords mirror positive ones —
    // asymmetric rounding would leave one quadrant off by 5 nm.
    let doc =
        mkDoc [
            mkCell "top" [
                sref "sub" 3L -3L     // 3 > 2.5 → +5; -3 < -2.5 → -5
                sref "sub" -3L 3L     // mirror
                sref "sub" 2L -2L     // 2 < 2.5 → 0; -2 > -2.5 → 0
            ]
        ]
    let doc' = Instances.normalizeToGrid doc
    let origins =
        topElements doc'
        |> List.choose (fun el ->
            match el with
            | SRefEl s -> Some (s.Origin.X, s.Origin.Y)
            | _ -> None)
    origins.[0] |> should equal (5L, -5L)
    origins.[1] |> should equal (-5L, 5L)
    origins.[2] |> should equal (0L, 0L)

[<Fact>]
let ``labels do not inherit SRef snap deltas - each snaps independently`` () =
    // Spec contract: a Label originally aligned with an SRef's
    // anchor (off-grid) does NOT travel with the SRef's snap
    // delta. Each rounds to its own nearest grid point — which is
    // exactly the bug Norm exists to surface, not preserve.
    let doc =
        mkDoc [
            mkCell "sub" [ rect (0L, 0L, 10L, 10L) ]
            mkCell "top" [
                sref "sub" 13L 13L      // -> (15, 15) snap delta +2
                label "PIN" 17L 17L     // -> (15, 15) snap delta -2  (NOT +2)
            ]
        ]
    let doc' = Instances.normalizeToGrid doc
    let els = topElements doc'
    match els.[0] with
    | SRefEl s -> (s.Origin.X, s.Origin.Y) |> should equal (15L, 15L)
    | other -> failwithf "expected SRef at [0], got %A" other
    match els.[1] with
    // Label snaps to (15, 15) on its own. If it had inherited the
    // SRef's +2 delta it'd land at (19, 19) — definitely off-grid.
    | LabelEl l -> (l.Origin.X, l.Origin.Y) |> should equal (15L, 15L)
    | other -> failwithf "expected Label at [1], got %A" other

[<Fact>]
let ``every cell is snapped, not just the top`` () =
    // Off-grid drift in subcells (build-script arithmetic) is the
    // common case Norm exists to address. Top-cell-only would
    // leave verify-grid still flagging the children.
    let doc =
        mkDoc [
            mkCell "sub" [ rect (1L, 2L, 13L, 24L) ]
            mkCell "mid" [ sref "sub" 7L 11L ]
            mkCell "top" [ sref "mid" 33L 9L ]
        ]
    let doc' = Instances.normalizeToGrid doc
    let allElements =
        doc'.Cells
        |> List.collect (fun c -> c.Elements |> List.map (fun el -> c.Name, el))
    // sub.rect snapped
    match allElements |> List.find (fun (n, _) -> n = "sub") |> snd with
    | RectEl r ->
        (r.X1, r.Y1, r.X2, r.Y2) |> should equal (0L, 0L, 15L, 25L)
    | _ -> failwith "expected sub rect"
    // mid.sref snapped
    match allElements |> List.find (fun (n, _) -> n = "mid") |> snd with
    | SRefEl s -> (s.Origin.X, s.Origin.Y) |> should equal (5L, 10L)
    | _ -> failwith "expected mid sref"
    // top.sref snapped
    match allElements |> List.find (fun (n, _) -> n = "top") |> snd with
    | SRefEl s -> (s.Origin.X, s.Origin.Y) |> should equal (35L, 10L)
    | _ -> failwith "expected top sref"

[<Fact>]
let ``already-clean doc is returned unchanged`` () =
    // Idempotence: a doc already on grid round-trips byte-for-byte.
    // The Update handler relies on this for its no-op short-
    // circuit (skip the undo snapshot + flatten churn when no
    // coord actually moved).
    let doc =
        mkDoc [
            mkCell "sub" [ rect (0L, 0L, 100L, 100L) ]
            mkCell "top" [
                sref "sub" 50L 50L
                rect (200L, 200L, 300L, 300L)
                label "VDD" 100L 100L
            ]
        ]
    let doc' = Instances.normalizeToGrid doc
    doc' |> should equal doc

[<Fact>]
let ``unknown PDK raises so silent fall-back can't mask drift`` () =
    // Tech.gridFor explicitly errors on unknown PDKs (Track 01).
    // The handler should propagate that — falling back to grid=1L
    // would be a no-op that hid the misconfig.
    let doc =
        { emptyDocument with
            Cells = [ mkCell "top" [ sref "sub" 13L 7L ] ]
            TopCell = Some "top"
            Pdk = "umc28" }  // not registered
    (fun () -> Instances.normalizeToGrid doc |> ignore)
    |> shouldFail
