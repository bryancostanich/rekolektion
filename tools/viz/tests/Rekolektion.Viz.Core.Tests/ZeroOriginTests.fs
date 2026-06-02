module Rekolektion.Viz.Core.Tests.ZeroOriginTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout

// ─────────────────────────────────────────────────────────────────
// Builders — same shape as InstancesLabelFollowingTests but
// trimmed to what the Zero-Origin path actually exercises:
// rects (Poly/Path/Rect), SRefs, ARefs, Labels.
// ─────────────────────────────────────────────────────────────────

let private rect (layerName: string) (x1, y1, x2, y2) : Element =
    RectEl {
        Layer = Named ("sky130", layerName)
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
    { emptyDocument with
        Cells = cells
        TopCell = Some "top" }

let private topElements (doc: Document) : Element list =
    doc.Cells
    |> List.find (fun c -> c.Name = "top")
    |> _.Elements

// ─────────────────────────────────────────────────────────────────
// translateTopCell — every position-bearing element shifts by
// the same delta; non-position elements pass through untouched.
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``zero delta returns the document unchanged`` () =
    let doc =
        mkDoc [
            mkCell "sub" [ rect "met1" (0L, 0L, 100L, 100L) ]
            mkCell "top" [ sref "sub" 50L 50L ]
        ]
    let doc' = Instances.translateTopCell doc 0L 0L
    doc' |> should equal doc

[<Fact>]
let ``SRef Origin translates by the delta`` () =
    let doc =
        mkDoc [
            mkCell "sub" [ rect "met1" (0L, 0L, 100L, 100L) ]
            mkCell "top" [ sref "sub" 200L 300L ]
        ]
    let doc' = Instances.translateTopCell doc -200L -300L
    match topElements doc' |> List.head with
    | SRefEl s -> (s.Origin.X, s.Origin.Y) |> should equal (0L, 0L)
    | other -> failwithf "expected SRef at head, got %A" other

[<Fact>]
let ``Rect both corners translate together`` () =
    let doc =
        mkDoc [
            mkCell "top" [ rect "met1" (100L, 200L, 500L, 700L) ]
        ]
    let doc' = Instances.translateTopCell doc -100L -200L
    match topElements doc' |> List.head with
    | RectEl r ->
        r.X1 |> should equal 0L
        r.Y1 |> should equal 0L
        r.X2 |> should equal 400L
        r.Y2 |> should equal 500L
    | other -> failwithf "expected Rect, got %A" other

[<Fact>]
let ``Poly translates every point`` () =
    let pts0 = [ 50L,50L; 150L,50L; 100L,150L ]
    let doc = mkDoc [ mkCell "top" [ poly pts0 ] ]
    let doc' = Instances.translateTopCell doc -50L -50L
    match topElements doc' |> List.head with
    | PolyEl p ->
        let xs = p.Points |> List.map (fun pt -> pt.X)
        let ys = p.Points |> List.map (fun pt -> pt.Y)
        xs |> should equal [ 0L; 100L; 50L ]
        ys |> should equal [ 0L; 0L; 100L ]
    | other -> failwithf "expected Poly, got %A" other

[<Fact>]
let ``Path translates every vertex`` () =
    let pts0 = [ 10L, 20L; 110L, 20L; 110L, 120L ]
    let doc = mkDoc [ mkCell "top" [ path 200L pts0 ] ]
    let doc' = Instances.translateTopCell doc -10L -20L
    match topElements doc' |> List.head with
    | PathEl p ->
        p.Points
        |> List.map (fun pt -> pt.X, pt.Y)
        |> should equal [ 0L,0L; 100L,0L; 100L,100L ]
    | other -> failwithf "expected Path, got %A" other

[<Fact>]
let ``Label Origin translates by the delta`` () =
    let doc = mkDoc [ mkCell "top" [ label "NET" 100L 200L ] ]
    let doc' = Instances.translateTopCell doc -100L -200L
    match topElements doc' |> List.head with
    | LabelEl l -> (l.Origin.X, l.Origin.Y) |> should equal (0L, 0L)
    | other -> failwithf "expected Label, got %A" other

[<Fact>]
let ``ARef Origin AND ColPitch / RowPitch translate together`` () =
    // ColPitch / RowPitch are absolute coords in GDS terms — the
    // vector from Origin to one-beyond-the-last-col-or-row. If
    // we only translated Origin the array's column step would
    // shift and the per-cell positions would drift.
    let doc =
        mkDoc [
            mkCell "sub" [ rect "met1" (0L, 0L, 50L, 50L) ]
            mkCell "top" [
                aref "sub" 100L 200L 4 3 (400L, 200L) (100L, 500L)
            ]
        ]
    let doc' = Instances.translateTopCell doc -100L -200L
    match topElements doc' |> List.head with
    | ARefEl a ->
        a.Origin.X    |> should equal 0L
        a.Origin.Y    |> should equal 0L
        // ColPitch moved from (400, 200) to (300, 0) — same
        // delta as Origin so colStep = (cp - origin)/cols holds.
        a.ColPitch.X  |> should equal 300L
        a.ColPitch.Y  |> should equal 0L
        a.RowPitch.X  |> should equal 0L
        a.RowPitch.Y  |> should equal 300L
    | other -> failwithf "expected ARef, got %A" other

[<Fact>]
let ``mixed element set translates as one cohesive unit`` () =
    // The "real" Zero Origin case: top cell has rects + a poly +
    // an SRef + a label. Every coord shifts by the same delta so
    // relative geometry stays identical, only the world frame moves.
    let doc =
        mkDoc [
            mkCell "sub" [ rect "met1" (0L, 0L, 50L, 50L) ]
            mkCell "top" [
                rect "met1" (50L, 50L, 150L, 150L)
                poly [ 200L,200L; 300L,200L; 250L,300L ]
                sref "sub" 400L 400L
                label "VDD" 500L 500L
            ]
        ]
    let doc' = Instances.translateTopCell doc -50L -50L
    let els = topElements doc'
    let r =
        match els.[0] with
        | RectEl r -> r
        | other -> failwithf "expected Rect at [0], got %A" other
    r.X1 |> should equal 0L
    r.Y1 |> should equal 0L
    match els.[1] with
    | PolyEl p ->
        p.Points |> List.head |> (fun pt -> pt.X, pt.Y) |> should equal (150L, 150L)
    | other -> failwithf "expected Poly at [1], got %A" other
    match els.[2] with
    | SRefEl s -> (s.Origin.X, s.Origin.Y) |> should equal (350L, 350L)
    | other -> failwithf "expected SRef at [2], got %A" other
    match els.[3] with
    | LabelEl l -> (l.Origin.X, l.Origin.Y) |> should equal (450L, 450L)
    | other -> failwithf "expected Label at [3], got %A" other

// ─────────────────────────────────────────────────────────────────
// bboxOfFlat — the bbox helper drives the Zero Origin command,
// so its behaviour on the empty case + multi-poly case matters
// independently.
// ─────────────────────────────────────────────────────────────────

let private flatPoly (pts: (int64 * int64) list) : Flatten.FlatPolygon =
    { Layer = 68; DataType = 20
      Points = pts |> List.map (fun (x, y) -> { X = x; Y = y }) |> List.toArray
      SourceStructure = "top"
      SourceIndex = 0
      TopInstanceIndex = None }

[<Fact>]
let ``bboxOfFlat returns None on an empty sequence`` () =
    Instances.bboxOfFlat Seq.empty |> should equal (None : (int64*int64*int64*int64) option)

[<Fact>]
let ``bboxOfFlat returns the tight extents across multiple polys`` () =
    let polys = [
        flatPoly [ 100L,200L; 300L,200L; 200L,400L ]
        flatPoly [ -50L,-50L; 0L,-50L; 0L,0L; -50L,0L ]
        flatPoly [ 500L,500L; 600L,500L; 600L,800L; 500L,800L ]
    ]
    match Instances.bboxOfFlat polys with
    | Some (xMin, yMin, xMax, yMax) ->
        xMin |> should equal -50L
        yMin |> should equal -50L
        xMax |> should equal 600L
        yMax |> should equal 800L
    | None -> failwith "expected Some bbox"
