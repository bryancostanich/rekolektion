module Rekolektion.Viz.Core.Tests.RoutingWireDedupTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout
open Rekolektion.Viz.Core.Routing

// ─────────────────────────────────────────────────────────────────
// Routing.Wire.dedupCoincidentRects — collapse identical-bbox
// same-layer rects in each cell.  Used to clean up routing-emit
// duplicate via stacks at shared endpoints.
// ─────────────────────────────────────────────────────────────────

let private mkRect (x1, y1, x2, y2) (layerName: string) : Rectangle = {
    Layer = Named ("sky130", layerName)
    X1 = x1; Y1 = y1; X2 = x2; Y2 = y2
    Net = None
    Props = []
    Comments = []
    SubFormComments = Map.empty
}

let private mkRectWid (x1, y1, x2, y2) layerName wid : Rectangle =
    mkRect (x1, y1, x2, y2) layerName
    |> Wire.setWireId wid

let private mkCell (name: string) (elements: Element list) : Cell =
    { Name = name; Meta = None; Elements = elements
      Comments = []; SubFormComments = Map.empty }

let private mkDoc cells : Document =
    { emptyDocument with
        Cells = cells
        TopCell = cells |> List.tryHead |> Option.map (fun c -> c.Name) }

let private rectCount (doc: Document) : int =
    doc.Cells
    |> List.sumBy (fun c ->
        c.Elements
        |> List.sumBy (fun el -> match el with RectEl _ -> 1 | _ -> 0))

[<Fact>]
let ``dedup: empty doc → empty doc`` () =
    let d = mkDoc []
    let d' = Wire.dedupCoincidentRects d
    d'.Cells |> should equal d.Cells

[<Fact>]
let ``dedup: no duplicates → unchanged`` () =
    let doc =
        mkDoc [
            mkCell "top" [
                RectEl (mkRect (0L, 0L, 100L, 100L) "met1")
                RectEl (mkRect (200L, 0L, 300L, 100L) "met1")
            ]
        ]
    let doc' = Wire.dedupCoincidentRects doc
    rectCount doc' |> should equal 2

[<Fact>]
let ``dedup: two identical-bbox same-layer rects → 1`` () =
    let doc =
        mkDoc [
            mkCell "top" [
                RectEl (mkRectWid (10L, 10L, 50L, 50L) "met1" 1)
                RectEl (mkRectWid (10L, 10L, 50L, 50L) "met1" 2)
            ]
        ]
    let doc' = Wire.dedupCoincidentRects doc
    rectCount doc' |> should equal 1
    // First occurrence wins → wid=1 survives.
    let survivor =
        doc'.Cells.Head.Elements
        |> List.choose (function RectEl r -> Some r | _ -> None)
        |> List.head
    Wire.getWireId survivor |> should equal (Some 1)

[<Fact>]
let ``dedup: identical bbox on DIFFERENT layers → both kept`` () =
    // mcon (67/44) and met1 (68/20) at the same bbox are physically
    // different layers — keep both.
    let doc =
        mkDoc [
            mkCell "top" [
                RectEl (mkRect (10L, 10L, 50L, 50L) "met1")
                RectEl (mkRect (10L, 10L, 50L, 50L) "mcon")
            ]
        ]
    let doc' = Wire.dedupCoincidentRects doc
    rectCount doc' |> should equal 2

[<Fact>]
let ``dedup: normalises swapped coordinates`` () =
    // RectEl with X1 > X2 is the same bbox after normalisation.
    let doc =
        mkDoc [
            mkCell "top" [
                RectEl (mkRect (10L, 10L, 50L, 50L) "met1")
                RectEl (mkRect (50L, 50L, 10L, 10L) "met1")
            ]
        ]
    let doc' = Wire.dedupCoincidentRects doc
    rectCount doc' |> should equal 1

[<Fact>]
let ``dedup: order preserved — non-duplicates retain document order`` () =
    let doc =
        mkDoc [
            mkCell "top" [
                RectEl (mkRectWid (0L, 0L, 10L, 10L) "met1" 1)
                RectEl (mkRect (100L, 100L, 200L, 200L) "met1")
                RectEl (mkRectWid (0L, 0L, 10L, 10L) "met1" 2)  // dup of [0]
                RectEl (mkRect (300L, 300L, 400L, 400L) "met1")
            ]
        ]
    let doc' = Wire.dedupCoincidentRects doc
    rectCount doc' |> should equal 3
    let rects =
        doc'.Cells.Head.Elements
        |> List.choose (function RectEl r -> Some r | _ -> None)
    // Order: first dup (wid=1), the two non-dups in order.
    rects |> List.map (fun r -> r.X1) |> should equal [0L; 100L; 300L]
    rects |> List.head |> Wire.getWireId |> should equal (Some 1)

[<Fact>]
let ``dedup: per-cell scope — same bbox in different cells survives`` () =
    // dedup is per-cell.  Two cells each with the same rect should
    // both keep their rect — they're separate cells, not duplicates.
    let doc =
        mkDoc [
            mkCell "a" [ RectEl (mkRect (0L, 0L, 100L, 100L) "met1") ]
            mkCell "b" [ RectEl (mkRect (0L, 0L, 100L, 100L) "met1") ]
        ]
    let doc' = Wire.dedupCoincidentRects doc
    rectCount doc' |> should equal 2

[<Fact>]
let ``dedup: idempotent`` () =
    let doc =
        mkDoc [
            mkCell "top" [
                RectEl (mkRect (0L, 0L, 10L, 10L) "met1")
                RectEl (mkRect (0L, 0L, 10L, 10L) "met1")
                RectEl (mkRect (0L, 0L, 10L, 10L) "met1")
            ]
        ]
    let pass1 = Wire.dedupCoincidentRects doc
    let pass2 = Wire.dedupCoincidentRects pass1
    rectCount pass1 |> should equal 1
    rectCount pass2 |> should equal 1
    pass1.Cells |> should equal pass2.Cells

[<Fact>]
let ``dedup: non-rect elements pass through unchanged`` () =
    let label : Element = LabelEl {
        Layer = Named ("sky130", "met1_label")
        Text = "NET"; Origin = { X = 50L; Y = 50L }
        Class = None; Props = []; Comments = []
        SubFormComments = Map.empty
        IsInternal = false; Kind = NetName
    }
    let doc =
        mkDoc [
            mkCell "top" [
                RectEl (mkRect (0L, 0L, 100L, 100L) "met1")
                RectEl (mkRect (0L, 0L, 100L, 100L) "met1")
                label
            ]
        ]
    let doc' = Wire.dedupCoincidentRects doc
    let labelCount =
        doc'.Cells.Head.Elements
        |> List.sumBy (function LabelEl _ -> 1 | _ -> 0)
    labelCount |> should equal 1
    rectCount doc' |> should equal 1

// ─── Integration: real d11_ota_v4.rkt before / after dedup ───────

let private targetCell =
    "/Users/bryancostanich/git_repos/bryan_costanich/khalkulo/source/cell_designs/column_readout_chain/d11_ota_v4.rkt"

let private dupGroupCount (doc: Document) : int =
    doc.Cells
    |> List.sumBy (fun c ->
        c.Elements
        |> List.choose (fun el ->
            match el with
            | RectEl r ->
                let (l, d) = Rkt.ToGds.layerToGds r.Layer
                let xLo = min r.X1 r.X2
                let xHi = max r.X1 r.X2
                let yLo = min r.Y1 r.Y2
                let yHi = max r.Y1 r.Y2
                Some (l, d, xLo, yLo, xHi, yHi)
            | _ -> None)
        |> List.groupBy id
        |> List.filter (fun (_, xs) -> List.length xs > 1)
        |> List.length)

[<Fact>]
let ``dedup: d11_ota_v4.rkt round-trip leaves zero duplicate groups`` () =
    // Sourced from the routing audit on 2026-05-30 — the file
    // originally had 10 duplicate-bbox groups (matching the user's
    // bug report).  This test no longer locks in that count: the
    // file is a live workspace asset and may be deduped (via the
    // Tidy command, commit-time dedup, or hand-edits) between
    // runs.  Instead it asserts the invariant: after dedup, no
    // duplicate groups remain.  Dedup is idempotent, so re-running
    // on an already-clean file is a no-op.
    if not (System.IO.File.Exists targetCell) then () else
    let doc, _ = LayoutLoader.load targetCell
    let doc' = Wire.dedupCoincidentRects doc
    dupGroupCount doc' |> should equal 0
    // Rect count drops by exactly the # of duplicate members
    // collapsed (zero if the file was already clean).
    let before = rectCount doc
    let after  = rectCount doc'
    let dupMembers =
        // Each group of size N collapses to 1 — N-1 dropped.
        let mutable n = 0
        doc.Cells
        |> List.iter (fun c ->
            c.Elements
            |> List.choose (fun el ->
                match el with
                | RectEl r ->
                    let (l, d) = Rkt.ToGds.layerToGds r.Layer
                    let xLo = min r.X1 r.X2
                    let xHi = max r.X1 r.X2
                    let yLo = min r.Y1 r.Y2
                    let yHi = max r.Y1 r.Y2
                    Some (l, d, xLo, yLo, xHi, yHi)
                | _ -> None)
            |> List.groupBy id
            |> List.iter (fun (_, members) ->
                if List.length members > 1 then
                    n <- n + (List.length members - 1)))
        n
    (before - after) |> should equal dupMembers
