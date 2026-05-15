module Rekolektion.Viz.Core.Layout.Instances

open Rekolektion.Viz.Core.Rkt.Types

/// One movable instance at the top level. Hit-testing, selection,
/// and drag/rotate/mirror operate on these — they're the unit of
/// edit per the locked decision "SRef instances only (no top-level
/// paint editing)".
///
/// `Index` is the position of the SRef within the top cell's
/// `Elements` list, used as a stable identity for selection across
/// re-flattens. `BBox` is the axis-aligned world-DBU bounding box
/// of the instance (after the SRef's transform is applied to its
/// child polygons), suitable for pointer hit-testing.
type Instance = {
    /// Stable identity — index into top cell's Elements.
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
    let rad = s.Rot * System.Math.PI / 180.0
    let cosA = System.Math.Cos rad
    let sinA = System.Math.Sin rad
    let mag = s.Mag
    if s.Reflect then
        { A = mag * cosA;  B = mag * sinA
          C = mag * sinA;  D = -mag * cosA
          Tx = float s.Origin.X; Ty = float s.Origin.Y }
    else
        { A = mag * cosA;  B = -mag * sinA
          C = mag * sinA;  D = mag * cosA
          Tx = float s.Origin.X; Ty = float s.Origin.Y }

/// Find a cell's untransformed bbox (over its own poly + rect + path
/// elements + every SRef/ARef child it contains). Recurses through
/// children. Memoised by name to keep hierarchical macros cheap — a
/// 256x64 SRAM with one bitcell type is enumerated once, not once
/// per row*col.
let private buildLocalBboxes (doc: Document)
        : System.Collections.Generic.IDictionary<string, (int64 * int64 * int64 * int64) option> =
    let byName =
        doc.Cells
        |> List.map (fun c -> c.Name, c)
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
                | Some c ->
                    let mutable acc : (int64 * int64 * int64 * int64) option = None
                    for el in c.Elements do
                        match el with
                        | PolyEl p ->
                            for pt in p.Points do
                                let cur = (pt.X, pt.Y, pt.X, pt.Y)
                                acc <- merge acc (Some cur)
                        | RectEl r ->
                            let cur =
                                (min r.X1 r.X2, min r.Y1 r.Y2,
                                 max r.X1 r.X2, max r.Y1 r.Y2)
                            acc <- merge acc (Some cur)
                        | PathEl p ->
                            // Path widens by Width/2 each side. We
                            // approximate with the centerline plus
                            // half-width in both axes; good enough
                            // for hit-testing.
                            let half = p.Width / 2L
                            for pt in p.Points do
                                let cur = (pt.X - half, pt.Y - half, pt.X + half, pt.Y + half)
                                acc <- merge acc (Some cur)
                        | SRefEl sr ->
                            match bboxOf sr.Cell with
                            | None -> ()
                            | Some childBb ->
                                acc <- merge acc (Some (transformBbox (fromSref sr) childBb))
                        | ARefEl ar ->
                            match bboxOf ar.Cell with
                            | None -> ()
                            | Some childBb ->
                                if ar.Cols > 0 && ar.Rows > 0 then
                                    let rad = ar.Rot * System.Math.PI / 180.0
                                    let cosA = System.Math.Cos rad
                                    let sinA = System.Math.Sin rad
                                    let baseAff =
                                        if ar.Reflect then
                                            { A = ar.Mag * cosA
                                              B = ar.Mag * sinA
                                              C = ar.Mag * sinA
                                              D = -ar.Mag * cosA
                                              Tx = float ar.Origin.X
                                              Ty = float ar.Origin.Y }
                                        else
                                            { A = ar.Mag * cosA
                                              B = -ar.Mag * sinA
                                              C = ar.Mag * sinA
                                              D = ar.Mag * cosA
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
    for c in doc.Cells do
        bboxOf c.Name |> ignore
    cache :> System.Collections.Generic.IDictionary<_,_>

/// Same top-cell heuristic as `Flatten.findTop` — pick the cell no
/// other cell references, falling back to the first cell. Returns
/// None for an empty document.
let private findTop (doc: Document) : Cell option =
    if List.isEmpty doc.Cells then None
    else
        let referenced = System.Collections.Generic.HashSet<string>()
        for c in doc.Cells do
            for el in c.Elements do
                match el with
                | SRefEl s -> referenced.Add s.Cell |> ignore
                | ARefEl a -> referenced.Add a.Cell |> ignore
                | _ -> ()
        doc.Cells
        |> List.tryFind (fun c -> not (referenced.Contains c.Name))
        |> Option.defaultWith (fun () -> List.head doc.Cells)
        |> Some

/// Enumerate every SRef directly under the top cell with its world
/// bbox. ARefs at the top level are skipped — multi-instance arrays
/// are not yet movable as a unit (P0 scope).
let enumerate (doc: Document) : Instance array =
    match findTop doc with
    | None -> [||]
    | Some top ->
        let local = buildLocalBboxes doc
        let result = System.Collections.Generic.List<Instance>()
        top.Elements
        |> List.iteri (fun idx el ->
            match el with
            | SRefEl sr ->
                let bb =
                    match local.TryGetValue sr.Cell with
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
                    Name = sprintf "%s[%d]" sr.Cell idx
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

/// Per-(layer, datatype) world-DBU bboxes for *each individual
/// polygon* in a flat polygon set. Dimension overlay + DRC need
/// per-polygon bboxes (not the per-layer union) — two instances
/// placed side-by-side will always have overlapping per-layer
/// unions because both have polys spanning the full row, even
/// when their actual silicon footprints are cleanly separated.
/// Skips polygons with empty point arrays.
let layerPolyBboxesOf (polys: System.Collections.Generic.IEnumerable<Rekolektion.Viz.Core.Layout.Flatten.FlatPolygon>)
                      : Map<int * int, (int64 * int64 * int64 * int64) array> =
    let acc =
        System.Collections.Generic.Dictionary<int * int,
            System.Collections.Generic.List<int64 * int64 * int64 * int64>>()
    for p in polys do
        if p.Points.Length > 0 then
            let key = (p.Layer, p.DataType)
            let mutable xMin = System.Int64.MaxValue
            let mutable yMin = System.Int64.MaxValue
            let mutable xMax = System.Int64.MinValue
            let mutable yMax = System.Int64.MinValue
            for pt in p.Points do
                if pt.X < xMin then xMin <- pt.X
                if pt.X > xMax then xMax <- pt.X
                if pt.Y < yMin then yMin <- pt.Y
                if pt.Y > yMax then yMax <- pt.Y
            let bb = (xMin, yMin, xMax, yMax)
            match acc.TryGetValue key with
            | true, lst -> lst.Add bb
            | _ ->
                let lst = System.Collections.Generic.List<_>()
                lst.Add bb
                acc.[key] <- lst
    acc
    |> Seq.map (fun kv -> kv.Key, kv.Value.ToArray())
    |> Map.ofSeq

/// Per-top-instance, per-layer per-polygon bbox map. Built from
/// `flattenInstance` so bboxes are already in world coordinates.
let layerPolyBboxesByInstance (doc: Document)
                              : Map<int, Map<int * int, (int64 * int64 * int64 * int64) array>> =
    match findTop doc with
    | None -> Map.empty
    | Some top ->
        top.Elements
        |> List.indexed
        |> List.choose (fun (idx, el) ->
            match el with
            | SRefEl _ ->
                let polys = Rekolektion.Viz.Core.Layout.Flatten.flattenInstance doc idx
                Some (idx, layerPolyBboxesOf polys)
            | _ -> None)
        |> Map.ofList

/// Physical bbox of one top-instance: union of every polygon's
/// bbox EXCLUDING non-physical Magic-marker layers (checkpaint,
/// error, feedback). The dimension overlay uses this as the cell's
/// "outside edge" so arrows measure visible silicon extent, not
/// bookkeeping rectangles whose footprint extends past the actual
/// content. Returns None for instances with no physical geometry.
let physicalBboxOfInstance
        (perLayer: Map<int * int, (int64 * int64 * int64 * int64) array>)
        : (int64 * int64 * int64 * int64) option =
    let mutable xMin = System.Int64.MaxValue
    let mutable yMin = System.Int64.MaxValue
    let mutable xMax = System.Int64.MinValue
    let mutable yMax = System.Int64.MinValue
    let mutable any = false
    for kv in perLayer do
        let (l, dt) = kv.Key
        if not (Rekolektion.Viz.Core.Layout.Layer.isNonPhysical l dt) then
            for (a, b, c, d) in kv.Value do
                if a < xMin then xMin <- a
                if b < yMin then yMin <- b
                if c > xMax then xMax <- c
                if d > yMax then yMax <- d
                any <- true
    if any then Some (xMin, yMin, xMax, yMax) else None

/// Convenience wrapper: per-top-instance physical bbox, derived
/// from `layerPolyBboxesByInstance`.
let physicalBboxesByInstance (doc: Document)
                             : Map<int, int64 * int64 * int64 * int64> =
    layerPolyBboxesByInstance doc
    |> Map.toSeq
    |> Seq.choose (fun (idx, perLayer) ->
        physicalBboxOfInstance perLayer
        |> Option.map (fun bb -> idx, bb))
    |> Map.ofSeq

/// Convenience: union per-layer bbox over all polygons. Useful for
/// the "is the neighbor close enough at all?" coarse filter — much
/// cheaper than scanning per-polygon. Not appropriate for the
/// dimension overlay's actual gap math; see `layerPolyBboxesOf`.
let layerBboxesOf (polys: System.Collections.Generic.IEnumerable<Rekolektion.Viz.Core.Layout.Flatten.FlatPolygon>)
                  : Map<int * int, int64 * int64 * int64 * int64> =
    let perPoly = layerPolyBboxesOf polys
    perPoly
    |> Map.map (fun _ arr ->
        let mutable xMin = System.Int64.MaxValue
        let mutable yMin = System.Int64.MaxValue
        let mutable xMax = System.Int64.MinValue
        let mutable yMax = System.Int64.MinValue
        for (a, b, c, d) in arr do
            if a < xMin then xMin <- a
            if b < yMin then yMin <- b
            if c > xMax then xMax <- c
            if d > yMax then yMax <- d
        (xMin, yMin, xMax, yMax))

/// Replace the top cell's Elements with a new list. Helper for the
/// mutating ops below.
let private withTopElements (doc: Document) (top: Cell) (elements: Element list) : Document =
    { doc with
        Cells =
            doc.Cells
            |> List.map (fun c ->
                if c.Name = top.Name then { c with Elements = elements } else c) }

/// Duplicate every SRef whose Index is in `selectionByIndex` by
/// appending a clone of each one to the top cell's `Elements`,
/// with each clone shifted by `(dxDbu, dyDbu)` from its original
/// origin. Returns the new Document plus the new top-element
/// indices of the clones in the order they appeared in the
/// selection (caller swaps the selection over to those indices
/// so the duplicates become the active editing target).
let duplicateSelection
        (doc: Document)
        (selectionByIndex: Set<int>)
        (dxDbu: int64) (dyDbu: int64)
        : Document * Set<int> =
    if selectionByIndex.IsEmpty then doc, Set.empty
    else
        match findTop doc with
        | None -> doc, Set.empty
        | Some top ->
            let originals =
                top.Elements
                |> List.indexed
                |> List.choose (fun (idx, el) ->
                    if not (selectionByIndex.Contains idx) then None
                    else
                        match el with
                        | SRefEl sr ->
                            let o = sr.Origin
                            let cloned : SRef =
                                { sr with
                                    Origin =
                                        { X = o.X + dxDbu; Y = o.Y + dyDbu } }
                            Some (SRefEl cloned)
                        | _ -> None)
            let elems' = top.Elements @ originals
            let baseIdx = top.Elements.Length
            let cloneIndices =
                Set.ofList [ for i in 0 .. originals.Length - 1 -> baseIdx + i ]
            withTopElements doc top elems', cloneIndices

/// Duplicate every PolyEl / PathEl / RectEl in `polySelection` by
/// cloning each element with its points / coords shifted by Δ DBU.
/// Clones are appended to their source cell's element list (one
/// new element per original); the returned set names the clones'
/// (cell, new-index) pairs so callers can flip selection to the
/// clones (matching `duplicateSelection`'s SRef contract).
let duplicatePolygons
        (doc: Document)
        (polySelection: Set<string * int>)
        (dxDbu: int64) (dyDbu: int64)
        : Document * Set<string * int> =
    if polySelection.IsEmpty then doc, Set.empty
    else
        let perCell =
            polySelection
            |> Set.toList
            |> List.groupBy fst
            |> List.map (fun (s, items) -> s, items |> List.map snd |> Set.ofList)
            |> Map.ofList
        let translatePts (pts: Rekolektion.Viz.Core.Rkt.Types.Point list) =
            pts
            |> List.map (fun (p: Rekolektion.Viz.Core.Rkt.Types.Point) ->
                ({ X = p.X + dxDbu; Y = p.Y + dyDbu }
                 : Rekolektion.Viz.Core.Rkt.Types.Point))
        let cloneIndices = ResizeArray<string * int>()
        let updatedCells =
            doc.Cells
            |> List.map (fun c ->
                match Map.tryFind c.Name perCell with
                | None -> c
                | Some indices ->
                    let clones =
                        c.Elements
                        |> List.indexed
                        |> List.choose (fun (i, el) ->
                            if not (indices.Contains i) then None
                            else
                                match el with
                                | Rekolektion.Viz.Core.Rkt.Types.PolyEl p ->
                                    Some (Rekolektion.Viz.Core.Rkt.Types.PolyEl
                                        { p with Points = translatePts p.Points })
                                | Rekolektion.Viz.Core.Rkt.Types.PathEl p ->
                                    Some (Rekolektion.Viz.Core.Rkt.Types.PathEl
                                        { p with Points = translatePts p.Points })
                                | Rekolektion.Viz.Core.Rkt.Types.RectEl r ->
                                    Some (Rekolektion.Viz.Core.Rkt.Types.RectEl
                                        { r with
                                            X1 = r.X1 + dxDbu; Y1 = r.Y1 + dyDbu
                                            X2 = r.X2 + dxDbu; Y2 = r.Y2 + dyDbu })
                                | _ -> None)
                    let baseIdx = c.Elements.Length
                    for i in 0 .. clones.Length - 1 do
                        cloneIndices.Add (c.Name, baseIdx + i)
                    { c with Elements = c.Elements @ clones })
        { doc with Cells = updatedCells }, Set.ofSeq cloneIndices

/// Combined duplicate: SRefs (`instSelection`) + polys
/// (`polySelection`), each cloned with the same Δ. Returns the new
/// doc and the two new selection sets so the caller can flip
/// selection to the clones (so a follow-up drag moves the clones,
/// not the originals).
let duplicateSelections
        (doc: Document)
        (instSelection: Set<int>)
        (polySelection: Set<string * int>)
        (dxDbu: int64) (dyDbu: int64)
        : Document * Set<int> * Set<string * int> =
    let doc1, instClones = duplicateSelection doc instSelection dxDbu dyDbu
    let doc2, polyClones = duplicatePolygons doc1 polySelection dxDbu dyDbu
    doc2, instClones, polyClones

// `selectionsBbox` lives below the rotate/mirror block so it can
// reuse the file-level `elementBbox` helper.

// 2x2 rotation matrices for the supported rigid transforms. Each
// has integer entries so applying R to integer origins, with a
// snapped pivot, keeps every result on the manufacturing grid by
// construction.
let private R_rot90  : float * float * float * float = 0.0, -1.0, 1.0, 0.0
let private R_mirrorX: float * float * float * float = 1.0, 0.0, 0.0, -1.0
let private R_mirrorY: float * float * float * float = -1.0, 0.0, 0.0, 1.0

/// Multiply 2x2 R · M.
let private mul2x2
        ((ra, rb, rc, rd): float * float * float * float)
        ((ma, mb, mc, md): float * float * float * float)
        : float * float * float * float =
    (ra * ma + rb * mc,
     ra * mb + rb * md,
     rc * ma + rd * mc,
     rc * mb + rd * md)

/// Linear part of an SRef (mag * R * Refl^k as a 2×2). Mirrors
/// `Layout.Flatten.fromSref` minus the translation.
let private linearOfSref (sr: SRef) : float * float * float * float =
    let rad = sr.Rot * System.Math.PI / 180.0
    let cosA = System.Math.Cos rad
    let sinA = System.Math.Sin rad
    let mag = sr.Mag
    if sr.Reflect then
        (mag * cosA,  mag * sinA,
         mag * sinA, -mag * cosA)
    else
        (mag * cosA, -mag * sinA,
         mag * sinA,  mag * cosA)

/// Decompose a 2×2 linear part back into the Rkt SRef's
/// (Mag, Rot, Reflect) trio plus a new origin. Re-uses the legacy
/// Mag.Transform.toSref decomposition and copies its result into a
/// fresh Rkt.SRef record so Cell / Props / Comments survive.
let private srefWith
        (sr: SRef)
        ((a, b, c, d): float * float * float * float)
        (originX: int64) (originY: int64)
        : SRef =
    let decomposed =
        Rekolektion.Viz.Core.Mag.Transform.toSref
            sr.Cell a b c d (float originX) (float originY)
    { sr with
        Origin = { X = decomposed.Origin.X; Y = decomposed.Origin.Y }
        Rot = decomposed.Angle
        Mag = decomposed.Mag
        Reflect = decomposed.Reflected }

/// Apply rigid transform `R` to every SRef in `selectionByIndex`,
/// pivoting around `pivotDbu` (snapped centroid). Each instance's
/// linear part becomes `R · old_linear` and its origin becomes
/// `R · (origin - pivot) + pivot`. With integer R, integer origin,
/// and a grid-snapped pivot, results stay on the mfg grid.
let private transformSelection
        (doc: Document)
        (selectionByIndex: Set<int>)
        (R: float * float * float * float)
        ((px, py): int64 * int64)
        : Document =
    if selectionByIndex.IsEmpty then doc
    else
        match findTop doc with
        | None -> doc
        | Some top ->
            let (ra, rb, rc, rd) = R
            let elems' =
                top.Elements
                |> List.mapi (fun idx el ->
                    if not (selectionByIndex.Contains idx) then el
                    else
                        match el with
                        | SRefEl sr ->
                            let oldLin = linearOfSref sr
                            let newLin = mul2x2 R oldLin
                            let ox = sr.Origin.X
                            let oy = sr.Origin.Y
                            let dx = float (ox - px)
                            let dy = float (oy - py)
                            let nx = ra * dx + rb * dy + float px
                            let ny = rc * dx + rd * dy + float py
                            let newOX = int64 (System.Math.Round nx)
                            let newOY = int64 (System.Math.Round ny)
                            SRefEl (srefWith sr newLin newOX newOY)
                        | other -> other)
            withTopElements doc top elems'

/// Centroid of the bbox-of-bboxes for a selection, snapped to the
/// manufacturing grid. The snapped centroid is mandatory: with
/// integer R and integer origins, only a snapped pivot keeps the
/// transform results on-grid.
let selectionPivotSnapped
        (doc: Document)
        (selected: Instance array)
        : (int64 * int64) option =
    selectionBbox selected
    |> Option.map (fun (x1, y1, x2, y2) ->
        let cx = (x1 + x2) / 2L
        let cy = (y1 + y2) / 2L
        let p =
            Rekolektion.Viz.Core.Layout.Snap.snapPointDbu doc.Units
                Rekolektion.Viz.Core.Layout.Snap.sky130MfgGridNm
                { X = cx; Y = cy }
        p.X, p.Y)

/// Rotate every SRef in `selectionByIndex` 90° CCW around `pivot`.
let rotate90Selection
        (doc: Document)
        (selectionByIndex: Set<int>)
        (pivot: int64 * int64)
        : Document =
    transformSelection doc selectionByIndex R_rot90 pivot

/// Mirror every SRef in `selectionByIndex` about the X axis through
/// `pivot` (flips Y).
let mirrorXSelection
        (doc: Document)
        (selectionByIndex: Set<int>)
        (pivot: int64 * int64)
        : Document =
    transformSelection doc selectionByIndex R_mirrorX pivot

/// Mirror every SRef in `selectionByIndex` about the Y axis through
/// `pivot` (flips X).
let mirrorYSelection
        (doc: Document)
        (selectionByIndex: Set<int>)
        (pivot: int64 * int64)
        : Document =
    transformSelection doc selectionByIndex R_mirrorY pivot

/// Apply a translation Δ (DBU) to every SRef whose Index is in
/// `selectionByIndex`. Returns a new Document with the top cell's
/// SRef Origins updated; non-selected elements and other cells are
/// reused as-is. Δ is expected to already be grid-snapped — see
/// `Layout.Snap.snapDeltaDbu`.
let translateSelection
        (doc: Document)
        (selectionByIndex: Set<int>)
        (dxDbu: int64) (dyDbu: int64)
        : Document =
    if selectionByIndex.IsEmpty || (dxDbu = 0L && dyDbu = 0L) then doc
    else
        match findTop doc with
        | None -> doc
        | Some top ->
            let elems' =
                top.Elements
                |> List.mapi (fun idx el ->
                    if not (selectionByIndex.Contains idx) then el
                    else
                        match el with
                        | SRefEl sr ->
                            let o = sr.Origin
                            SRefEl
                                { sr with
                                    Origin = { X = o.X + dxDbu; Y = o.Y + dyDbu } }
                        | other -> other)
            withTopElements doc top elems'

// -- Label-anchor inference shared by edit ops --------------------
// Same rule the renderer / Net.Ratlines / LabelFlood all use:
// "the smallest same-layer-number polygon whose bbox contains the
// label's origin." Lifted to Core so the canvas live preview AND
// the Update commit can share the implementation.

let layerNumberOf (layer: Rekolektion.Viz.Core.Rkt.Types.Layer) : int =
    fst (Rekolektion.Viz.Core.Rkt.ToGds.layerToGds layer)

let elementBbox
        (el: Rekolektion.Viz.Core.Rkt.Types.Element)
        : (int64 * int64 * int64 * int64) option =
    match el with
    | Rekolektion.Viz.Core.Rkt.Types.PolyEl p when not p.Points.IsEmpty ->
        let mutable xMin = System.Int64.MaxValue
        let mutable yMin = System.Int64.MaxValue
        let mutable xMax = System.Int64.MinValue
        let mutable yMax = System.Int64.MinValue
        for pt in p.Points do
            if pt.X < xMin then xMin <- pt.X
            if pt.X > xMax then xMax <- pt.X
            if pt.Y < yMin then yMin <- pt.Y
            if pt.Y > yMax then yMax <- pt.Y
        Some (xMin, yMin, xMax, yMax)
    | Rekolektion.Viz.Core.Rkt.Types.RectEl r ->
        let xMin, xMax = if r.X1 <= r.X2 then r.X1, r.X2 else r.X2, r.X1
        let yMin, yMax = if r.Y1 <= r.Y2 then r.Y1, r.Y2 else r.Y2, r.Y1
        Some (xMin, yMin, xMax, yMax)
    | _ -> None

/// Per-cell label → anchoring-element index map. A label whose
/// origin doesn't land inside any same-layer-number bbox is
/// absent (no anchor → label doesn't travel with any edit).
/// "Smallest" so a label on a thin met1 stripe inside a wider
/// areaid bbox anchors to the stripe.
let anchorMapForCell
        (cell: Rekolektion.Viz.Core.Rkt.Types.Cell)
        : Map<int, int> =
    let indexed = cell.Elements |> List.indexed
    let labels =
        indexed
        |> List.choose (fun (i, el) ->
            match el with
            | Rekolektion.Viz.Core.Rkt.Types.LabelEl l -> Some (i, l)
            | _ -> None)
    labels
    |> List.choose (fun (labelIdx, label) ->
        let labelLayerNum = layerNumberOf label.Layer
        let mutable best : int voption = ValueNone
        let mutable bestArea = System.Int64.MaxValue
        for (elIdx, el) in indexed do
            let layerNum =
                match el with
                | Rekolektion.Viz.Core.Rkt.Types.PolyEl p -> Some (layerNumberOf p.Layer)
                | Rekolektion.Viz.Core.Rkt.Types.RectEl r -> Some (layerNumberOf r.Layer)
                | _ -> None
            match layerNum with
            | Some n when n = labelLayerNum ->
                match elementBbox el with
                | Some (xMin, yMin, xMax, yMax) ->
                    if label.Origin.X >= xMin && label.Origin.X <= xMax
                       && label.Origin.Y >= yMin && label.Origin.Y <= yMax then
                        let area = (xMax - xMin) * (yMax - yMin)
                        if area < bestArea then
                            bestArea <- area
                            best <- ValueSome elIdx
                | None -> ()
            | _ -> ()
        match best with
        | ValueSome anchorIdx -> Some (labelIdx, anchorIdx)
        | ValueNone -> None)
    |> Map.ofList

/// Translate every polygon (PolyEl / PathEl / RectEl) in
/// `polySelection` by Δ DBU, plus any same-cell label anchored
/// (per `anchorMapForCell`) to one of the moved polygons. Mirrors
/// `translateSelectionWithLabels` for SRefs.
let translatePolygonsWithLabels
        (doc: Document)
        (polySelection: Set<string * int>)
        (dxDbu: int64) (dyDbu: int64)
        : Document =
    if polySelection.IsEmpty || (dxDbu = 0L && dyDbu = 0L) then doc
    else
        let perCell =
            polySelection
            |> Set.toList
            |> List.groupBy fst
            |> List.map (fun (s, items) -> s, items |> List.map snd |> Set.ofList)
            |> Map.ofList
        let translatePts (pts: Rekolektion.Viz.Core.Rkt.Types.Point list) =
            pts
            |> List.map (fun (p: Rekolektion.Viz.Core.Rkt.Types.Point) ->
                ({ X = p.X + dxDbu; Y = p.Y + dyDbu }
                 : Rekolektion.Viz.Core.Rkt.Types.Point))
        let updatedCells =
            doc.Cells
            |> List.map (fun c ->
                match Map.tryFind c.Name perCell with
                | None -> c
                | Some indices ->
                    let anchorMap = anchorMapForCell c
                    let elems' =
                        c.Elements
                        |> List.mapi (fun i el ->
                            if indices.Contains i then
                                match el with
                                | Rekolektion.Viz.Core.Rkt.Types.PolyEl p ->
                                    Rekolektion.Viz.Core.Rkt.Types.PolyEl
                                        { p with Points = translatePts p.Points }
                                | Rekolektion.Viz.Core.Rkt.Types.PathEl p ->
                                    Rekolektion.Viz.Core.Rkt.Types.PathEl
                                        { p with Points = translatePts p.Points }
                                | Rekolektion.Viz.Core.Rkt.Types.RectEl r ->
                                    Rekolektion.Viz.Core.Rkt.Types.RectEl
                                        { r with
                                            X1 = r.X1 + dxDbu; Y1 = r.Y1 + dyDbu
                                            X2 = r.X2 + dxDbu; Y2 = r.Y2 + dyDbu }
                                | other -> other
                            else
                                match el, Map.tryFind i anchorMap with
                                | Rekolektion.Viz.Core.Rkt.Types.LabelEl l, Some anchorIdx
                                        when indices.Contains anchorIdx ->
                                    let o = l.Origin
                                    Rekolektion.Viz.Core.Rkt.Types.LabelEl
                                        { l with
                                            Origin =
                                                ({ X = o.X + dxDbu; Y = o.Y + dyDbu }
                                                 : Rekolektion.Viz.Core.Rkt.Types.Point) }
                                | _ -> el)
                    { c with Elements = elems' })
        { doc with Cells = updatedCells }

/// Like `translateSelection`, but also moves any top-cell label
/// anchored (per `Net.Ratlines.anchorForLabel` rule: smallest same-
/// layer-number containing-bbox poly) to a polygon inside one of
/// the moved SRefs. Without this, parent-cell labels stay at their
/// original positions while the SRef they describe slides away —
/// the renderer's ratline re-anchor mid-drag then computes a
/// different component graph than the post-commit state, and the
/// user sees the ratline set "snap" on release. Used by both the
/// canvas live preview and the Update commit so the two stay
/// consistent.
let translateSelectionWithLabels
        (doc: Document)
        (selectionByIndex: Set<int>)
        (dxDbu: int64) (dyDbu: int64)
        : Document =
    if selectionByIndex.IsEmpty || (dxDbu = 0L && dyDbu = 0L) then doc
    else
        match findTop doc with
        | None -> doc
        | Some top ->
            // Build the anchor-candidate list in flat-world coords:
            // top-cell direct paint (tag = None) + every selected
            // instance's flat polys (tag = Some k). We only look at
            // SELECTED instances because labels anchored to non-
            // selected geometry shouldn't move.
            let layerNum (layer: Rekolektion.Viz.Core.Rkt.Types.Layer) : int =
                fst (Rekolektion.Viz.Core.Rkt.ToGds.layerToGds layer)
            let polyBboxFor (poly: Rekolektion.Viz.Core.Layout.Flatten.FlatPolygon)
                    : (int * int64 * int64 * int64 * int64) option =
                if poly.Points.Length = 0 then None
                else
                    let mutable xMin = System.Int64.MaxValue
                    let mutable yMin = System.Int64.MaxValue
                    let mutable xMax = System.Int64.MinValue
                    let mutable yMax = System.Int64.MinValue
                    for pt in poly.Points do
                        if pt.X < xMin then xMin <- pt.X
                        if pt.X > xMax then xMax <- pt.X
                        if pt.Y < yMin then yMin <- pt.Y
                        if pt.Y > yMax then yMax <- pt.Y
                    Some (poly.Layer, xMin, yMin, xMax, yMax)
            // Both the moved-SRef polys AND every other anchor
            // candidate go in. We need ALL candidates (not just the
            // moved ones) so the smallest-area tiebreak picks the
            // RIGHT anchor — a label sitting on a tiny moved poly
            // inside a wider non-moved bbox should anchor to the
            // tiny one. Tag carries the originating instance index
            // (or None for top-cell direct paint).
            let candidates =
                ResizeArray<int option * int * int64 * int64 * int64 * int64>()
            for el in Rekolektion.Viz.Core.Layout.Flatten.flattenTopCellDirect doc do
                match polyBboxFor el with
                | Some (ln, xMin, yMin, xMax, yMax) ->
                    candidates.Add (None, ln, xMin, yMin, xMax, yMax)
                | None -> ()
            for inst in enumerate doc do
                let polys =
                    Rekolektion.Viz.Core.Layout.Flatten.flattenInstance doc inst.Index
                for poly in polys do
                    match polyBboxFor poly with
                    | Some (ln, xMin, yMin, xMax, yMax) ->
                        candidates.Add (Some inst.Index, ln, xMin, yMin, xMax, yMax)
                    | None -> ()
            // Identify top-cell labels whose best anchor (smallest
            // same-layer-number containing) lives inside a SELECTED
            // instance.
            let labelsToShift = System.Collections.Generic.HashSet<int>()
            top.Elements
            |> List.iteri (fun i el ->
                match el with
                | Rekolektion.Viz.Core.Rkt.Types.LabelEl l ->
                    let labelLn = layerNum l.Layer
                    let mutable best : int option voption = ValueNone
                    let mutable bestArea = System.Int64.MaxValue
                    for (tag, ln, xMin, yMin, xMax, yMax) in candidates do
                        if ln = labelLn
                           && l.Origin.X >= xMin && l.Origin.X <= xMax
                           && l.Origin.Y >= yMin && l.Origin.Y <= yMax then
                            let area = (xMax - xMin) * (yMax - yMin)
                            if area < bestArea then
                                bestArea <- area
                                best <- ValueSome tag
                    match best with
                    | ValueSome (Some k) when selectionByIndex.Contains k ->
                        labelsToShift.Add i |> ignore
                    | _ -> ()
                | _ -> ())
            // Single pass: translate SRefs + shift identified
            // labels. Same delta for both.
            let elems' =
                top.Elements
                |> List.mapi (fun idx el ->
                    if selectionByIndex.Contains idx then
                        match el with
                        | Rekolektion.Viz.Core.Rkt.Types.SRefEl sr ->
                            let o = sr.Origin
                            Rekolektion.Viz.Core.Rkt.Types.SRefEl
                                { sr with
                                    Origin =
                                        ({ X = o.X + dxDbu; Y = o.Y + dyDbu }
                                         : Rekolektion.Viz.Core.Rkt.Types.Point) }
                        | other -> other
                    elif labelsToShift.Contains idx then
                        match el with
                        | Rekolektion.Viz.Core.Rkt.Types.LabelEl l ->
                            let o = l.Origin
                            Rekolektion.Viz.Core.Rkt.Types.LabelEl
                                { l with
                                    Origin =
                                        ({ X = o.X + dxDbu; Y = o.Y + dyDbu }
                                         : Rekolektion.Viz.Core.Rkt.Types.Point) }
                        | other -> other
                    else el)
            withTopElements doc top elems'

/// Combined translate: SRefs (`instSelection`) + polys
/// (`polySelection`), each with their anchored labels, by the
/// same Δ. Order is "instances first, then polys"; the two
/// operations don't overlap in the elements they touch (SRefs vs.
/// polys/rects/paths/labels are disjoint kinds), so the result is
/// commutative. Used by drag commits + canvas live preview when
/// either or both selections are non-empty.
let translateSelectionsWithLabels
        (doc: Document)
        (instSelection: Set<int>)
        (polySelection: Set<string * int>)
        (dxDbu: int64) (dyDbu: int64)
        : Document =
    if (dxDbu = 0L && dyDbu = 0L)
       || (instSelection.IsEmpty && polySelection.IsEmpty) then doc
    else
        doc
        |> fun d -> translateSelectionWithLabels d instSelection dxDbu dyDbu
        |> fun d -> translatePolygonsWithLabels d polySelection dxDbu dyDbu

// -- Polygon rotate / mirror --------------------------------------
//
// Mirror of `rotate90Selection` / `mirrorXSelection` /
// `mirrorYSelection` for the polygon side of a mixed selection.
// Applies a per-point transform (rotation OR mirror around `pivot`)
// to every selected `PolyEl` / `PathEl` / `RectEl`. Rect corners
// are re-normalized after rotation so the bbox stays axis-aligned.
// SRefs are NOT touched here — call the corresponding *Selection
// helper for those.

let private transformPolygons
        (doc: Document)
        (polySelection: Set<string * int>)
        (pointXform: int64 -> int64 -> int64 * int64)
        : Document =
    if polySelection.IsEmpty then doc
    else
        let perCell =
            polySelection
            |> Set.toList
            |> List.groupBy fst
            |> List.map (fun (s, items) -> s, items |> List.map snd |> Set.ofList)
            |> Map.ofList
        let xformPts (pts: Rekolektion.Viz.Core.Rkt.Types.Point list) =
            pts
            |> List.map (fun (p: Rekolektion.Viz.Core.Rkt.Types.Point) ->
                let nx, ny = pointXform p.X p.Y
                ({ X = nx; Y = ny } : Rekolektion.Viz.Core.Rkt.Types.Point))
        let updatedCells =
            doc.Cells
            |> List.map (fun c ->
                match Map.tryFind c.Name perCell with
                | None -> c
                | Some indices ->
                    let elems' =
                        c.Elements
                        |> List.mapi (fun i el ->
                            if not (indices.Contains i) then el
                            else
                                match el with
                                | Rekolektion.Viz.Core.Rkt.Types.PolyEl p ->
                                    Rekolektion.Viz.Core.Rkt.Types.PolyEl
                                        { p with Points = xformPts p.Points }
                                | Rekolektion.Viz.Core.Rkt.Types.PathEl p ->
                                    Rekolektion.Viz.Core.Rkt.Types.PathEl
                                        { p with Points = xformPts p.Points }
                                | Rekolektion.Viz.Core.Rkt.Types.RectEl r ->
                                    let nx1, ny1 = pointXform r.X1 r.Y1
                                    let nx2, ny2 = pointXform r.X2 r.Y2
                                    let xMin, xMax =
                                        if nx1 <= nx2 then nx1, nx2 else nx2, nx1
                                    let yMin, yMax =
                                        if ny1 <= ny2 then ny1, ny2 else ny2, ny1
                                    Rekolektion.Viz.Core.Rkt.Types.RectEl
                                        { r with
                                            X1 = xMin; Y1 = yMin
                                            X2 = xMax; Y2 = yMax }
                                | other -> other)
                    { c with Elements = elems' })
        { doc with Cells = updatedCells }

/// Rotate every polygon in `polySelection` 90° CCW around `pivot`.
let rotate90Polygons
        (doc: Document)
        (polySelection: Set<string * int>)
        (pivot: int64 * int64)
        : Document =
    let px, py = pivot
    transformPolygons doc polySelection (fun x y -> px + py - y, py - px + x)

/// Mirror every polygon in `polySelection` about the X axis
/// through `pivot` (flips Y).
let mirrorXPolygons
        (doc: Document)
        (polySelection: Set<string * int>)
        (pivot: int64 * int64)
        : Document =
    let _, py = pivot
    transformPolygons doc polySelection (fun x y -> x, 2L * py - y)

/// Mirror every polygon in `polySelection` about the Y axis
/// through `pivot` (flips X).
let mirrorYPolygons
        (doc: Document)
        (polySelection: Set<string * int>)
        (pivot: int64 * int64)
        : Document =
    let px, _ = pivot
    transformPolygons doc polySelection (fun x y -> 2L * px - x, y)

/// Snapped centroid of the bbox-of-bboxes covering BOTH the
/// selected SRefs (`instances`) AND the selected polys
/// (`polySelection`). Returns None when no bboxes contribute.
/// Used as the pivot for unified rotate / mirror over a mixed
/// selection.
let selectionsPivotSnapped
        (doc: Document)
        (instances: Instance array)
        (polySelection: Set<string * int>)
        : (int64 * int64) option =
    let bboxes = ResizeArray<int64 * int64 * int64 * int64>()
    for inst in instances do
        bboxes.Add inst.BBox
    if not polySelection.IsEmpty then
        let perCell =
            polySelection
            |> Set.toList
            |> List.groupBy fst
            |> List.map (fun (s, items) -> s, items |> List.map snd |> Set.ofList)
            |> Map.ofList
        for c in doc.Cells do
            match Map.tryFind c.Name perCell with
            | None -> ()
            | Some indices ->
                c.Elements
                |> List.iteri (fun i el ->
                    if indices.Contains i then
                        match elementBbox el with
                        | Some bb -> bboxes.Add bb
                        | None -> ())
    if bboxes.Count = 0 then None
    else
        let mutable xMin = System.Int64.MaxValue
        let mutable yMin = System.Int64.MaxValue
        let mutable xMax = System.Int64.MinValue
        let mutable yMax = System.Int64.MinValue
        for (a, b, c, d) in bboxes do
            if a < xMin then xMin <- a
            if b < yMin then yMin <- b
            if c > xMax then xMax <- c
            if d > yMax then yMax <- d
        let cx = (xMin + xMax) / 2L
        let cy = (yMin + yMax) / 2L
        let p =
            Rekolektion.Viz.Core.Layout.Snap.snapPointDbu doc.Units
                Rekolektion.Viz.Core.Layout.Snap.sky130MfgGridNm
                ({ X = cx; Y = cy }
                 : Rekolektion.Viz.Core.Rkt.Types.Point)
        Some (p.X, p.Y)

/// Bbox-of-bboxes spanning the SELECTED SRefs + the SELECTED polys.
/// Returns None when no bboxes contribute. Used by duplicate to
/// derive the per-clone offset (one selection-bbox width to the
/// right + a small gap).
let selectionsBbox
        (doc: Document)
        (instances: Instance array)
        (polySelection: Set<string * int>)
        : (int64 * int64 * int64 * int64) option =
    let bboxes = ResizeArray<int64 * int64 * int64 * int64>()
    for inst in instances do
        bboxes.Add inst.BBox
    if not polySelection.IsEmpty then
        let perCell =
            polySelection
            |> Set.toList
            |> List.groupBy fst
            |> List.map (fun (s, items) -> s, items |> List.map snd |> Set.ofList)
            |> Map.ofList
        for c in doc.Cells do
            match Map.tryFind c.Name perCell with
            | None -> ()
            | Some indices ->
                c.Elements
                |> List.iteri (fun i el ->
                    if indices.Contains i then
                        match elementBbox el with
                        | Some bb -> bboxes.Add bb
                        | None -> ())
    if bboxes.Count = 0 then None
    else
        let mutable xMin = System.Int64.MaxValue
        let mutable yMin = System.Int64.MaxValue
        let mutable xMax = System.Int64.MinValue
        let mutable yMax = System.Int64.MinValue
        for (a, b, c, d) in bboxes do
            if a < xMin then xMin <- a
            if b < yMin then yMin <- b
            if c > xMax then xMax <- c
            if d > yMax then yMax <- d
        Some (xMin, yMin, xMax, yMax)

/// Transitional wrappers for callers that still hold a
/// `Gds.Types.Library`. Each one converts to `Rkt.Document` on the
/// way in and back to `Library` on the way out via `Rkt.OfGds` /
/// `Rkt.ToGds`. Geometry round-trips losslessly; net metadata and
/// comments are not synthesised from a Library that doesn't carry
/// them. When the App's model migrates to `Rkt.Document`, this
/// submodule retires.
module Library =
    type LibT = Rekolektion.Viz.Core.Gds.Types.Library

    let private toRkt (lib: LibT) : Document =
        Rekolektion.Viz.Core.Rkt.OfGds.fromLibrary lib

    let private toLib (doc: Document) : LibT =
        Rekolektion.Viz.Core.Rkt.ToGds.toLibrary doc

    let enumerate (lib: LibT) : Instance array =
        enumerate (toRkt lib)

    let layerPolyBboxesByInstance (lib: LibT) =
        layerPolyBboxesByInstance (toRkt lib)

    let physicalBboxesByInstance (lib: LibT) =
        physicalBboxesByInstance (toRkt lib)

    let selectionPivotSnapped
            (lib: LibT) (selected: Instance array) =
        selectionPivotSnapped (toRkt lib) selected

    let translateSelection
            (lib: LibT) (selectionByIndex: Set<int>)
            (dxDbu: int64) (dyDbu: int64) : LibT =
        translateSelection (toRkt lib) selectionByIndex dxDbu dyDbu
        |> toLib

    let duplicateSelection
            (lib: LibT) (selectionByIndex: Set<int>)
            (dxDbu: int64) (dyDbu: int64) : LibT * Set<int> =
        let doc', clones =
            duplicateSelection (toRkt lib) selectionByIndex dxDbu dyDbu
        toLib doc', clones

    let rotate90Selection
            (lib: LibT) (selectionByIndex: Set<int>)
            (pivot: int64 * int64) : LibT =
        rotate90Selection (toRkt lib) selectionByIndex pivot
        |> toLib

    let mirrorXSelection
            (lib: LibT) (selectionByIndex: Set<int>)
            (pivot: int64 * int64) : LibT =
        mirrorXSelection (toRkt lib) selectionByIndex pivot
        |> toLib

    let mirrorYSelection
            (lib: LibT) (selectionByIndex: Set<int>)
            (pivot: int64 * int64) : LibT =
        mirrorYSelection (toRkt lib) selectionByIndex pivot
        |> toLib
