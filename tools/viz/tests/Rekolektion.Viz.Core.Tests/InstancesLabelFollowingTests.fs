module Rekolektion.Viz.Core.Tests.InstancesLabelFollowingTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout

// ─────────────────────────────────────────────────────────────────
// Builders for inline Document fixtures.
//
// Each test constructs a minimal Document with:
//   - a sub cell painting one rect (an SRef "pin")
//   - a top cell with an SRef of the sub + a Label at the SRef's
//     painted pin position
// then exercises `rotate90SelectionWithLabels` /
// `mirrorXSelectionWithLabels` / `mirrorYSelectionWithLabels` and
// asserts the label travels with the SRef per the
// `Net.Ratlines.anchorForLabel` rule (smallest same-layer-number
// bbox-containing flat polygon).
//
// met1       = (68, 20)
// met1_label = (68, 5)  (same layer NUMBER 68 — anchor rule matches)
// ─────────────────────────────────────────────────────────────────

let private rect (x1, y1, x2, y2) : Element =
    RectEl {
        Layer = Named ("sky130", "met1")
        X1 = x1; Y1 = y1; X2 = x2; Y2 = y2
        Net = None
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
    { emptyDocument with
        Cells = cells
        TopCell = Some "top" }

// Find a label by text in the (post-transform) top cell.
let private labelOriginByText (doc: Document) (text: string) : (int64 * int64) option =
    doc.Cells
    |> List.tryFind (fun c -> c.Name = "top")
    |> Option.bind (fun c ->
        c.Elements
        |> List.tryPick (fun el ->
            match el with
            | LabelEl l when l.Text = text -> Some (l.Origin.X, l.Origin.Y)
            | _ -> None))

let private srefOrigin (doc: Document) : (int64 * int64) option =
    doc.Cells
    |> List.tryFind (fun c -> c.Name = "top")
    |> Option.bind (fun c ->
        c.Elements
        |> List.tryPick (fun el ->
            match el with
            | SRefEl s -> Some (s.Origin.X, s.Origin.Y)
            | _ -> None))

// ─────────────────────────────────────────────────────────────────
// rotate90SelectionWithLabels
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``rotate90 SRef carries anchored parent label`` () =
    // Sub paints met1 rect at local (0,0)-(100,100).
    // Top SRef at origin (50,50) → world rect (50,50)-(150,150).
    // Label "NET" at (100,100) — center of the world rect, anchored.
    let sub = mkCell "sub" [ rect (0L, 0L, 100L, 100L) ]
    let top = mkCell "top" [
        sref "sub" 50L 50L         // index 0
        label "NET" 100L 100L      // index 1
    ]
    let doc = mkDoc [ top; sub ]
    // Rotate around pivot (0,0). 90° CCW: (x,y) → (-y, x).
    let doc' = Instances.rotate90SelectionWithLabels doc (Set.ofList [0]) (0L, 0L)
    // SRef origin: (50,50) → (-50, 50).
    srefOrigin doc' |> should equal (Some (-50L, 50L))
    // Label moved with the SRef: (100,100) → (-100, 100).
    labelOriginByText doc' "NET" |> should equal (Some (-100L, 100L))

[<Fact>]
let ``rotate90 SRef does NOT move label outside its bbox`` () =
    let sub = mkCell "sub" [ rect (0L, 0L, 100L, 100L) ]
    let top = mkCell "top" [
        sref "sub" 50L 50L          // SRef rect world: (50,50)-(150,150)
        label "NET" 100L 100L       // inside — anchored
        label "OTHER" 500L 500L     // outside — NOT anchored
    ]
    let doc = mkDoc [ top; sub ]
    let doc' = Instances.rotate90SelectionWithLabels doc (Set.ofList [0]) (0L, 0L)
    // NET travels.
    labelOriginByText doc' "NET" |> should equal (Some (-100L, 100L))
    // OTHER is anchored to nothing in the selection → stays put.
    labelOriginByText doc' "OTHER" |> should equal (Some (500L, 500L))

[<Fact>]
let ``rotate90 with empty selection is a no-op`` () =
    let sub = mkCell "sub" [ rect (0L, 0L, 100L, 100L) ]
    let top = mkCell "top" [
        sref "sub" 50L 50L
        label "NET" 100L 100L
    ]
    let doc = mkDoc [ top; sub ]
    let doc' = Instances.rotate90SelectionWithLabels doc Set.empty (0L, 0L)
    srefOrigin doc' |> should equal (Some (50L, 50L))
    labelOriginByText doc' "NET" |> should equal (Some (100L, 100L))

// ─────────────────────────────────────────────────────────────────
// mirrorXSelectionWithLabels / mirrorYSelectionWithLabels
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``mirrorX SRef carries anchored parent label (flips Y)`` () =
    let sub = mkCell "sub" [ rect (0L, 0L, 100L, 100L) ]
    let top = mkCell "top" [
        sref "sub" 50L 50L
        label "NET" 100L 100L
    ]
    let doc = mkDoc [ top; sub ]
    // Mirror about X through (0,0): (x, y) → (x, -y).
    let doc' = Instances.mirrorXSelectionWithLabels doc (Set.ofList [0]) (0L, 0L)
    // Label: (100, 100) → (100, -100).
    labelOriginByText doc' "NET" |> should equal (Some (100L, -100L))

[<Fact>]
let ``mirrorY SRef carries anchored parent label (flips X)`` () =
    let sub = mkCell "sub" [ rect (0L, 0L, 100L, 100L) ]
    let top = mkCell "top" [
        sref "sub" 50L 50L
        label "NET" 100L 100L
    ]
    let doc = mkDoc [ top; sub ]
    // Mirror about Y through (0,0): (x, y) → (-x, y).
    let doc' = Instances.mirrorYSelectionWithLabels doc (Set.ofList [0]) (0L, 0L)
    labelOriginByText doc' "NET" |> should equal (Some (-100L, 100L))

// ─────────────────────────────────────────────────────────────────
// Gap 2 — transformPolygons handles LabelEl directly
//
// When a label is EXPLICITLY in `polySelection`, it now transforms.
// (Previously the `| other -> other` fallthrough left it alone.)
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``rotate90Polygons explicit label selection transforms label`` () =
    let top = mkCell "top" [
        rect (0L, 0L, 100L, 100L)   // index 0
        label "NET" 50L 50L         // index 1
    ]
    let doc = mkDoc [ top ]
    // Select BOTH the rect and the label.
    let polySel = Set.ofList [ ("top", 0); ("top", 1) ]
    let doc' = Instances.rotate90Polygons doc polySel (0L, 0L)
    // Label rotates 90 CCW around (0,0): (50, 50) → (-50, 50).
    labelOriginByText doc' "NET" |> should equal (Some (-50L, 50L))

// ─────────────────────────────────────────────────────────────────
// rotate/mirror PolygonsWithLabels (poly-anchored label following)
//
// A label NOT in the selection but anchored to a SELECTED polygon
// should travel with the polygon.  Mirrors translatePolygonsWithLabels.
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``rotate90PolygonsWithLabels label anchored to selected rect rotates`` () =
    let top = mkCell "top" [
        rect (0L, 0L, 100L, 100L)   // index 0 — selected
        label "NET" 50L 50L         // index 1 — anchored to rect 0, NOT in sel
    ]
    let doc = mkDoc [ top ]
    let polySel = Set.ofList [ ("top", 0) ]
    let doc' =
        Instances.rotate90PolygonsWithLabels doc polySel (0L, 0L)
    // Rect at indices 0 rotates; label at index 1 rides along.
    labelOriginByText doc' "NET" |> should equal (Some (-50L, 50L))

[<Fact>]
let ``mirrorXPolygonsWithLabels label rides along`` () =
    let top = mkCell "top" [
        rect (0L, 0L, 100L, 100L)
        label "NET" 50L 50L
    ]
    let doc = mkDoc [ top ]
    let polySel = Set.ofList [ ("top", 0) ]
    let doc' = Instances.mirrorXPolygonsWithLabels doc polySel (0L, 0L)
    // (50,50) → (50, -50) about X axis through origin.
    labelOriginByText doc' "NET" |> should equal (Some (50L, -50L))

[<Fact>]
let ``mirrorYPolygonsWithLabels label rides along`` () =
    let top = mkCell "top" [
        rect (0L, 0L, 100L, 100L)
        label "NET" 50L 50L
    ]
    let doc = mkDoc [ top ]
    let polySel = Set.ofList [ ("top", 0) ]
    let doc' = Instances.mirrorYPolygonsWithLabels doc polySel (0L, 0L)
    // (50,50) → (-50, 50) about Y axis through origin.
    labelOriginByText doc' "NET" |> should equal (Some (-50L, 50L))

// ─────────────────────────────────────────────────────────────────
// anchorMapForCell — wire RectEls are NOT label anchor candidates.
//
// Bug report (project_viz_known_bugs.md 2026-05-30; resurfaced
// 2026-05-31): deleting a wire that overlaps a pre-existing pin
// label silently deletes the label too. Root cause: anchorMapForCell
// picks the SMALLEST same-layer bbox containing the label origin,
// and a wire's end-segment / pad is usually smaller than the pin
// patch beneath it. So a label anchored to the pin gets re-pointed
// to the wire — and the delete-cascade in Msg.DeleteSelection then
// takes the label out with the wire.
//
// Fix: rects tagged with the `wire-id` property are routing artifacts
// owned by the wire, not by the user's pins. They must NEVER be
// anchor candidates. Pin patches / primitive polys (untagged) are
// the only legitimate anchors.
// ─────────────────────────────────────────────────────────────────

let private wireRect (x1, y1, x2, y2) (wireId: int) : Element =
    RectEl {
        Layer = Named ("sky130", "met1")
        X1 = x1; Y1 = y1; X2 = x2; Y2 = y2
        Net = None
        Props = [ { Key = "wire-id"; Value = PvInt (int64 wireId) } ]
        Comments = []
        SubFormComments = Map.empty
    }

[<Fact>]
let ``anchorMapForCell prefers pin patch over a smaller wire rect at the same coord`` () =
    // index 0: pin patch (large, no wire-id)
    // index 1: wire end (smaller, wire-id=1) — overlaps the label
    // index 2: label at (50, 50) inside both
    // Without the fix, anchorMapForCell picks index 1 (smaller bbox);
    // with the fix, picks index 0 (pin patch is the only legitimate
    // anchor — wires are filtered out).
    let cell =
        mkCell "top" [
            rect (0L, 0L, 200L, 200L)        // pin patch
            wireRect (40L, 40L, 60L, 60L) 1  // small wire end on top
            label "v_in" 50L 50L
        ]
    let map = Instances.anchorMapForCell cell
    Map.tryFind 2 map |> should equal (Some 0)

[<Fact>]
let ``anchorMapForCell returns None for a label that ONLY sits inside a wire rect`` () =
    // No pin patch under the label — only a wire. The label MUST NOT
    // anchor to the wire (otherwise wire-delete cascades the label).
    // Returning None means the label survives any rect deletion.
    let cell =
        mkCell "top" [
            wireRect (0L, 0L, 100L, 100L) 1
            label "stray" 50L 50L
        ]
    let map = Instances.anchorMapForCell cell
    Map.containsKey 1 map |> should equal false

[<Fact>]
let ``anchorMapForCell anchors to a pin patch even when label sits dead-center on the wire`` () =
    // Stress: wire is sub-pixel inside the pin patch, label coords
    // happen to be the wire's exact centre. Still pin patch.
    let cell =
        mkCell "top" [
            rect (-500L, -500L, 500L, 500L)
            wireRect (95L, 95L, 105L, 105L) 7
            label "VSS" 100L 100L
        ]
    let map = Instances.anchorMapForCell cell
    Map.tryFind 2 map |> should equal (Some 0)
