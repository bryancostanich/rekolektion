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
let layerPolyBboxesByInstance (lib: Rekolektion.Viz.Core.Gds.Types.Library)
                              : Map<int, Map<int * int, (int64 * int64 * int64 * int64) array>> =
    let top = findTop lib
    // Flatten now consumes Rkt.Document; convert at the call site
    // until Instances itself migrates.
    let doc = Rekolektion.Viz.Core.Rkt.OfGds.fromLibrary lib
    top.Elements
    |> List.indexed
    |> List.choose (fun (idx, el) ->
        match el with
        | Rekolektion.Viz.Core.Gds.Types.SRef _ ->
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
let physicalBboxesByInstance (lib: Rekolektion.Viz.Core.Gds.Types.Library)
                             : Map<int, int64 * int64 * int64 * int64> =
    layerPolyBboxesByInstance lib
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

/// Duplicate every SRef whose Index is in `selectionByIndex` by
/// appending a clone of each one to the top cell's `Elements`,
/// with each clone shifted by `(dxDbu, dyDbu)` from its original
/// origin. Returns the new Library plus the new top-element
/// indices of the clones in the order they appeared in the
/// selection (caller swaps the selection over to those indices
/// so the duplicates become the active editing target).
let duplicateSelection
        (lib: Rekolektion.Viz.Core.Gds.Types.Library)
        (selectionByIndex: Set<int>)
        (dxDbu: int64) (dyDbu: int64)
        : Rekolektion.Viz.Core.Gds.Types.Library * Set<int> =
    if selectionByIndex.IsEmpty then lib, Set.empty
    else
        let topName = (findTop lib).Name
        // Build the list of clones to append, preserving the
        // SRefs' relative order so groups stay together.
        let mutable cloneIndices = []
        let updateStruct (s: Rekolektion.Viz.Core.Gds.Types.Structure)
                         : Rekolektion.Viz.Core.Gds.Types.Structure =
            if s.Name <> topName then s
            else
                let originals =
                    s.Elements
                    |> List.indexed
                    |> List.choose (fun (idx, el) ->
                        if not (selectionByIndex.Contains idx) then None
                        else
                            match el with
                            | Rekolektion.Viz.Core.Gds.Types.SRef sr ->
                                let o = sr.Origin
                                let cloned : Rekolektion.Viz.Core.Gds.Types.SRef =
                                    { sr with
                                        Origin =
                                            { X = o.X + dxDbu; Y = o.Y + dyDbu } }
                                Some (Rekolektion.Viz.Core.Gds.Types.Element.SRef cloned)
                            | _ -> None)
                let elems' = s.Elements @ originals
                // Record the new indices (= original length .. +N-1)
                let baseIdx = s.Elements.Length
                cloneIndices <-
                    [ for i in 0 .. originals.Length - 1 -> baseIdx + i ]
                { s with Elements = elems' }
        let lib' =
            { lib with
                Structures = lib.Structures |> List.map updateStruct }
        lib', Set.ofList cloneIndices

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
let private linearOfSref (sr: Rekolektion.Viz.Core.Gds.Types.SRef)
                         : float * float * float * float =
    let rad = sr.Angle * System.Math.PI / 180.0
    let cosA = System.Math.Cos rad
    let sinA = System.Math.Sin rad
    let mag = sr.Mag
    if sr.Reflected then
        (mag * cosA,  mag * sinA,
         mag * sinA, -mag * cosA)
    else
        (mag * cosA, -mag * sinA,
         mag * sinA,  mag * cosA)

/// Re-emit an SRef given a new linear part and origin. Decomposes
/// the linear matrix back into (Mag, Angle, Reflected) via
/// `Mag.Transform.toSref`. StructureName is preserved.
let private srefWith
        (sr: Rekolektion.Viz.Core.Gds.Types.SRef)
        ((a, b, c, d): float * float * float * float)
        (originX: int64) (originY: int64)
        : Rekolektion.Viz.Core.Gds.Types.SRef =
    let decomposed =
        Rekolektion.Viz.Core.Mag.Transform.toSref
            sr.StructureName a b c d (float originX) (float originY)
    { decomposed with StructureName = sr.StructureName }

/// Apply rigid transform `R` to every SRef in `selectionByIndex`,
/// pivoting around `pivotDbu` (snapped centroid). Each instance's
/// linear part becomes `R · old_linear` and its origin becomes
/// `R · (origin - pivot) + pivot`. With integer R, integer origin,
/// and a grid-snapped pivot, results stay on the mfg grid.
let private transformSelection
        (lib: Rekolektion.Viz.Core.Gds.Types.Library)
        (selectionByIndex: Set<int>)
        (R: float * float * float * float)
        ((px, py): int64 * int64)
        : Rekolektion.Viz.Core.Gds.Types.Library =
    if selectionByIndex.IsEmpty then lib
    else
        let topName = (findTop lib).Name
        let (ra, rb, rc, rd) = R
        let updateStruct (s: Rekolektion.Viz.Core.Gds.Types.Structure)
                         : Rekolektion.Viz.Core.Gds.Types.Structure =
            if s.Name <> topName then s
            else
                let elems' =
                    s.Elements
                    |> List.mapi (fun idx el ->
                        if not (selectionByIndex.Contains idx) then el
                        else
                            match el with
                            | Rekolektion.Viz.Core.Gds.Types.SRef sr ->
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
                                Rekolektion.Viz.Core.Gds.Types.Element.SRef
                                    (srefWith sr newLin newOX newOY)
                            | other -> other)
                { s with Elements = elems' }
        { lib with Structures = lib.Structures |> List.map updateStruct }

/// Centroid of the bbox-of-bboxes for a selection, snapped to the
/// manufacturing grid. The snapped centroid is mandatory: with
/// integer R and integer origins, only a snapped pivot keeps the
/// transform results on-grid.
let selectionPivotSnapped
        (lib: Rekolektion.Viz.Core.Gds.Types.Library)
        (selected: Instance array)
        : (int64 * int64) option =
    selectionBbox selected
    |> Option.map (fun (x1, y1, x2, y2) ->
        let cx = (x1 + x2) / 2L
        let cy = (y1 + y2) / 2L
        // Snap now takes the unit record; derive it from the legacy
        // Library at the call site. Instances itself still operates
        // on Gds.Library and migrates in a later stage.
        let units = Rekolektion.Viz.Core.Layout.Snap.unitsOfLibrary lib
        let p =
            Rekolektion.Viz.Core.Layout.Snap.snapPointDbu units
                Rekolektion.Viz.Core.Layout.Snap.sky130MfgGridNm
                { X = cx; Y = cy }
        p.X, p.Y)

/// Rotate every SRef in `selectionByIndex` 90° CCW around `pivot`.
let rotate90Selection
        (lib: Rekolektion.Viz.Core.Gds.Types.Library)
        (selectionByIndex: Set<int>)
        (pivot: int64 * int64)
        : Rekolektion.Viz.Core.Gds.Types.Library =
    transformSelection lib selectionByIndex R_rot90 pivot

/// Mirror every SRef in `selectionByIndex` about the X axis through
/// `pivot` (flips Y).
let mirrorXSelection
        (lib: Rekolektion.Viz.Core.Gds.Types.Library)
        (selectionByIndex: Set<int>)
        (pivot: int64 * int64)
        : Rekolektion.Viz.Core.Gds.Types.Library =
    transformSelection lib selectionByIndex R_mirrorX pivot

/// Mirror every SRef in `selectionByIndex` about the Y axis through
/// `pivot` (flips X).
let mirrorYSelection
        (lib: Rekolektion.Viz.Core.Gds.Types.Library)
        (selectionByIndex: Set<int>)
        (pivot: int64 * int64)
        : Rekolektion.Viz.Core.Gds.Types.Library =
    transformSelection lib selectionByIndex R_mirrorY pivot

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
