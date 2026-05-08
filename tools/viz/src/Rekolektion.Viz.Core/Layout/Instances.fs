module Rekolektion.Viz.Core.Layout.Instances

open Rekolektion.Viz.Core.Gds.Types

/// One movable instance at the top level. Hit-testing, selection,
/// and drag/rotate/mirror operate on these — they're the unit of
/// edit per the locked decision "SRef instances only (no top-level
/// paint editing)".
///
/// `Index` is the position of the SRef within the top structure's
/// `Elements` list, used as a stable identity for selection across
/// re-flattens. `BBox` is the axis-aligned world-DBU bounding box
/// of the instance (after the SRef's transform is applied to its
/// child polygons), suitable for pointer hit-testing.
type Instance = {
    /// Stable identity — index into top structure's Elements.
    Index : int
    /// SRef metadata at point of enumeration. After an edit the
    /// caller should re-enumerate to get the updated origin /
    /// matrix — this record is a snapshot, not a live view.
    Sref : SRef
    /// World-DBU axis-aligned bbox: (xmin, ymin, xmax, ymax).
    /// Empty cells get a zero-area bbox at the instance origin.
    BBox : int64 * int64 * int64 * int64
    /// Display name: "<cell>[index]" for now. Stable enough for
    /// inspector / MCP listings.
    Name : string
}

/// 2D affine identical to the one in `Layout.Flatten` — duplicated
/// here intentionally so this module can compute world bboxes
/// without forcing Flatten to expose its internals or running a
/// full re-flatten on every selection update.
type private Affine = {
    A: float; B: float; Tx: float
    C: float; D: float; Ty: float
}

let private apply (m: Affine) (x: float) (y: float) : float * float =
    m.A * x + m.B * y + m.Tx,
    m.C * x + m.D * y + m.Ty

let private fromSref (s: SRef) : Affine =
    let rad = s.Angle * System.Math.PI / 180.0
    let cosA = System.Math.Cos rad
    let sinA = System.Math.Sin rad
    let mag = s.Mag
    if s.Reflected then
        { A = mag * cosA;  B = mag * sinA
          C = mag * sinA;  D = -mag * cosA
          Tx = float s.Origin.X; Ty = float s.Origin.Y }
    else
        { A = mag * cosA;  B = -mag * sinA
          C = mag * sinA;  D = mag * cosA
          Tx = float s.Origin.X; Ty = float s.Origin.Y }

/// Find a structure's untransformed bbox (over its own Boundary +
/// Path elements + every SRef/ARef child it contains). Recurses
/// through children. Memoised by name to keep hierarchical macros
/// cheap — a 256x64 SRAM with one bitcell type is enumerated once,
/// not once per row*col.
let private buildLocalBboxes (lib: Library)
        : System.Collections.Generic.IDictionary<string, (int64 * int64 * int64 * int64) option> =
    let byName =
        lib.Structures
        |> List.map (fun s -> s.Name, s)
        |> Map.ofList
    let cache =
        System.Collections.Generic.Dictionary<string, (int64 * int64 * int64 * int64) option>()
    let merge (a: (int64 * int64 * int64 * int64) option)
              (b: (int64 * int64 * int64 * int64) option)
              : (int64 * int64 * int64 * int64) option =
        match a, b with
        | None, x | x, None -> x
        | Some (ax1, ay1, ax2, ay2), Some (bx1, by1, bx2, by2) ->
            Some (min ax1 bx1, min ay1 by1, max ax2 bx2, max ay2 by2)
    let rec bboxOf (name: string) =
        match cache.TryGetValue name with
        | true, v -> v
        | _ ->
            // Stash a sentinel before recursing so a cyclic chain
            // (illegal but possible in malformed input) terminates.
            cache.[name] <- None
            let result =
                match Map.tryFind name byName with
                | None -> None
                | Some s ->
                    let mutable acc : (int64 * int64 * int64 * int64) option = None
                    for el in s.Elements do
                        match el with
                        | Boundary b ->
                            for p in b.Points do
                                let cur = (p.X, p.Y, p.X, p.Y)
                                acc <- merge acc (Some cur)
                        | Path p ->
                            // Path widens by Width/2 each side. We
                            // approximate with the centerline plus
                            // half-width in both axes; good enough
                            // for hit-testing.
                            let half = int64 p.Width / 2L
                            for pt in p.Points do
                                let cur = (pt.X - half, pt.Y - half, pt.X + half, pt.Y + half)
                                acc <- merge acc (Some cur)
                        | SRef sr ->
                            match bboxOf sr.StructureName with
                            | None -> ()
                            | Some childBb ->
                                acc <- merge acc (Some (transformBbox (fromSref sr) childBb))
                        | ARef ar ->
                            match bboxOf ar.StructureName with
                            | None -> ()
                            | Some childBb ->
                                if ar.Cols > 0 && ar.Rows > 0 then
                                    let baseAff =
                                        { A = (if ar.Reflected then ar.Mag * System.Math.Cos(ar.Angle * System.Math.PI / 180.0) else ar.Mag * System.Math.Cos(ar.Angle * System.Math.PI / 180.0))
                                          B = (if ar.Reflected then ar.Mag * System.Math.Sin(ar.Angle * System.Math.PI / 180.0) else -ar.Mag * System.Math.Sin(ar.Angle * System.Math.PI / 180.0))
                                          C = ar.Mag * System.Math.Sin(ar.Angle * System.Math.PI / 180.0)
                                          D = (if ar.Reflected then -ar.Mag * System.Math.Cos(ar.Angle * System.Math.PI / 180.0) else ar.Mag * System.Math.Cos(ar.Angle * System.Math.PI / 180.0))
                                          Tx = float ar.Origin.X
                                          Ty = float ar.Origin.Y }
                                    let colStepX = (float ar.ColPitch.X - float ar.Origin.X) / float ar.Cols
                                    let colStepY = (float ar.ColPitch.Y - float ar.Origin.Y) / float ar.Cols
                                    let rowStepX = (float ar.RowPitch.X - float ar.Origin.X) / float ar.Rows
                                    let rowStepY = (float ar.RowPitch.Y - float ar.Origin.Y) / float ar.Rows
                                    for r in 0 .. ar.Rows - 1 do
                                        for c in 0 .. ar.Cols - 1 do
                                            let aff =
                                                { baseAff with
                                                    Tx = baseAff.Tx + float c * colStepX + float r * rowStepX
                                                    Ty = baseAff.Ty + float c * colStepY + float r * rowStepY }
                                            acc <- merge acc (Some (transformBbox aff childBb))
                        | _ -> ()
                    acc
            cache.[name] <- result
            result
    and transformBbox (m: Affine) (bb: int64 * int64 * int64 * int64) : int64 * int64 * int64 * int64 =
        let (x1, y1, x2, y2) = bb
        let corners = [|
            apply m (float x1) (float y1)
            apply m (float x2) (float y1)
            apply m (float x2) (float y2)
            apply m (float x1) (float y2)
        |]
        let xs = corners |> Array.map fst
        let ys = corners |> Array.map snd
        int64 (System.Math.Floor (Array.min xs)),
        int64 (System.Math.Floor (Array.min ys)),
        int64 (System.Math.Ceiling (Array.max xs)),
        int64 (System.Math.Ceiling (Array.max ys))
    for s in lib.Structures do
        bboxOf s.Name |> ignore
    cache :> System.Collections.Generic.IDictionary<_,_>

/// Same top-cell heuristic as Flatten.findTop — pick the structure
/// no other structure references, falling back to the first cell.
let private findTop (lib: Library) : Structure =
    let referenced = System.Collections.Generic.HashSet<string>()
    for s in lib.Structures do
        for el in s.Elements do
            match el with
            | SRef sr -> referenced.Add sr.StructureName |> ignore
            | ARef ar -> referenced.Add ar.StructureName |> ignore
            | _ -> ()
    lib.Structures
    |> List.tryFind (fun s -> not (referenced.Contains s.Name))
    |> Option.defaultWith (fun () -> List.head lib.Structures)

/// Enumerate every SRef directly under the top cell with its world
/// bbox. ARefs at the top level are skipped — multi-instance arrays
/// are not yet movable as a unit (P0 scope).
let enumerate (lib: Library) : Instance array =
    let top = findTop lib
    let local = buildLocalBboxes lib
    let result = System.Collections.Generic.List<Instance>()
    top.Elements
    |> List.iteri (fun idx el ->
        match el with
        | SRef sr ->
            let bb =
                match local.TryGetValue sr.StructureName with
                | true, Some bb ->
                    let aff = fromSref sr
                    let (x1, y1, x2, y2) = bb
                    let corners = [|
                        apply aff (float x1) (float y1)
                        apply aff (float x2) (float y1)
                        apply aff (float x2) (float y2)
                        apply aff (float x1) (float y2)
                    |]
                    let xs = corners |> Array.map fst
                    let ys = corners |> Array.map snd
                    int64 (System.Math.Floor (Array.min xs)),
                    int64 (System.Math.Floor (Array.min ys)),
                    int64 (System.Math.Ceiling (Array.max xs)),
                    int64 (System.Math.Ceiling (Array.max ys))
                | _ ->
                    // Empty / unresolved child: use a degenerate
                    // bbox at the instance origin so click tests
                    // can still select via the SRef's hot point.
                    let x = sr.Origin.X
                    let y = sr.Origin.Y
                    (x, y, x, y)
            result.Add {
                Index = idx
                Sref = sr
                BBox = bb
                Name = sprintf "%s[%d]" sr.StructureName idx
            }
        | _ -> ())
    result.ToArray()

/// Hit-test: return all instances whose bbox contains `(x, y)` in
/// world DBU. Multiple overlapping hits are returned in declaration
/// order (top of the file first); callers typically want the last
/// (front-most) for click-through.
let hitTest (instances: Instance array) (x: int64) (y: int64) : Instance array =
    instances
    |> Array.filter (fun inst ->
        let (x1, y1, x2, y2) = inst.BBox
        x >= x1 && x <= x2 && y >= y1 && y <= y2)

/// Bbox-of-bboxes for a non-empty selection. Callers passing an
/// empty array get None — there's no centroid to compute.
let selectionBbox (selected: Instance array)
                  : (int64 * int64 * int64 * int64) option =
    if selected.Length = 0 then None
    else
        let mutable x1 = System.Int64.MaxValue
        let mutable y1 = System.Int64.MaxValue
        let mutable x2 = System.Int64.MinValue
        let mutable y2 = System.Int64.MinValue
        for inst in selected do
            let (a, b, c, d) = inst.BBox
            if a < x1 then x1 <- a
            if b < y1 then y1 <- b
            if c > x2 then x2 <- c
            if d > y2 then y2 <- d
        Some (x1, y1, x2, y2)

/// Apply a translation Δ (DBU) to every SRef whose Index is in
/// `selectionByIndex`. Returns a new Library with the top cell's
/// SRef Origins updated; non-selected elements and other structures
/// are reused as-is. Δ is expected to already be grid-snapped — see
/// `Layout.Snap.snapDeltaDbu`.
let translateSelection (lib: Library)
                       (selectionByIndex: Set<int>)
                       (dxDbu: int64) (dyDbu: int64)
                       : Library =
    if selectionByIndex.IsEmpty || (dxDbu = 0L && dyDbu = 0L) then lib
    else
        let topName = (findTop lib).Name
        let updateStruct (s: Structure) : Structure =
            if s.Name <> topName then s
            else
                let elems' =
                    s.Elements
                    |> List.mapi (fun idx el ->
                        if not (selectionByIndex.Contains idx) then el
                        else
                            match el with
                            | SRef sr ->
                                let o = sr.Origin
                                Element.SRef
                                    { sr with
                                        Origin = { X = o.X + dxDbu; Y = o.Y + dyDbu } }
                            | other -> other)
                { s with Elements = elems' }
        { lib with Structures = lib.Structures |> List.map updateStruct }
