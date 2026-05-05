module Rekolektion.Viz.Core.Layout.MagToLayout

open System.IO
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Gds.Types
open Rekolektion.Viz.Core.Mag.Types

/// Build a `Library` from a parsed `MagCell` list. Each `MagCell`
/// becomes a GDS-style `Structure`; rects → Boundary, instances →
/// SRef, labels → TextLabel. Layer-name lookup goes through
/// `Mag.LayerMap`; unknown names are appended to `warnings` and
/// emitted with a marker (Layer = 0, DataType = 0) so the polygon
/// still shows up (the renderer falls back to a default theme
/// color for unknown keys).
///
/// `magscaleNum` / `magscaleDenom` come from the top cell's
/// magscale directive — Magic guarantees all cells in one design
/// share the same scale, so the top is authoritative.
let buildLibrary
        (top: MagCell)
        (allCells: MagCell list)
        : Library * string list =
    let warnings = ResizeArray<string>()
    let lambdaUm = Mag.Types.sky130LambdaNm / 1000.0
    let scaleUmPerInternal =
        lambdaUm * float top.MagscaleNum / float top.MagscaleDenom
    let scaleMetersPerInternal = scaleUmPerInternal * 1.0e-6

    let rectToBoundary (r: MagRect) : Boundary option =
        if Mag.LayerMap.isSkipped r.Layer then None
        else
        match Mag.LayerMap.tryFind r.Layer with
        | None ->
            warnings.Add(sprintf "unknown layer '%s' (rect %d,%d %d,%d)"
                            r.Layer r.X1 r.Y1 r.X2 r.Y2)
            None
        | Some (gdsLayer, gdsType) ->
            let pts = [
                { X = r.X1; Y = r.Y1 }
                { X = r.X2; Y = r.Y1 }
                { X = r.X2; Y = r.Y2 }
                { X = r.X1; Y = r.Y2 }
                { X = r.X1; Y = r.Y1 }
            ]
            Some {
                Layer = gdsLayer
                DataType = gdsType
                Points = pts
            }

    let instToSref (i: MagInstance) : SRef =
        Rekolektion.Viz.Core.Mag.Transform.toSref
            i.CellName i.A i.B i.C i.D i.Tx i.Ty

    let labelToText (l: MagLabel) : TextLabel option =
        match Mag.LayerMap.tryFind l.Layer with
        | None ->
            warnings.Add(sprintf "unknown label layer '%s' (text \"%s\")" l.Layer l.Text)
            None
        | Some (gdsLayer, _) ->
            // Use TextType 5 by default (SKY130 .label datatype) so
            // labels show up under the standard label-layer toggles.
            // Origin is the rect center.
            let cx = (l.X1 + l.X2) / 2L
            let cy = (l.Y1 + l.Y2) / 2L
            Some {
                Layer = gdsLayer
                TextType = 5
                Origin = { X = cx; Y = cy }
                Text = l.Text
            }

    let cellToStructure (c: MagCell) : Structure =
        let elems = ResizeArray<Element>()
        for r in c.Rects do
            match rectToBoundary r with
            | Some b -> elems.Add(Element.Boundary b)
            | None -> ()
        for inst in c.Instances do
            elems.Add(Element.SRef (instToSref inst))
        for l in c.Labels do
            match labelToText l with
            | Some t -> elems.Add(Element.Text t)
            | None -> ()
        { Name = c.Name; Elements = List.ofSeq elems }

    // Top cell goes first so the existing `Layout.Flatten.findTop`
    // heuristic — pick the structure no SRef references — still
    // resolves to it when the file has no subcells (or when every
    // child is referenced by the top).
    let ordered =
        let names = allCells |> List.map (fun c -> c.Name)
        let unique = System.Collections.Generic.HashSet<string>()
        let result = ResizeArray<MagCell>()
        // Top first
        if unique.Add(top.Name) then result.Add(top)
        for c in allCells do
            if unique.Add(c.Name) then result.Add(c)
        let _ = names
        List.ofSeq result

    let lib : Library = {
        Name = top.Name
        UserUnitsPerDbUnit = scaleUmPerInternal
        DbUnitsInMeters = scaleMetersPerInternal
        Structures = ordered |> List.map cellToStructure
    }
    lib, List.ofSeq warnings

/// Recursively load a `.mag` file and any subcells reachable
/// through `use` directives. `extraSearchDirs` extends the
/// implicit "loaded file's directory" path. Missing subcells log
/// a warning rather than failing — same defensive posture the
/// brief asks for. Returns the assembled Library plus the
/// flattened warning list.
let loadFile
        (path: string)
        (extraSearchDirs: string list)
        : Library * string list =
    let topPath =
        try Path.GetFullPath path
        with _ -> path
    let searchPath = Mag.SearchPath.buildPath topPath extraSearchDirs
    let top = Mag.Reader.read topPath
    let visited = System.Collections.Generic.HashSet<string>([| top.Name |])
    let queue = System.Collections.Generic.Queue<MagCell>()
    let allCells = ResizeArray<MagCell>([| top |])
    let extraWarn = ResizeArray<string>()

    for inst in top.Instances do
        if visited.Add inst.CellName then
            match Mag.SearchPath.resolve inst.CellName searchPath with
            | Some p -> queue.Enqueue(Mag.Reader.read p)
            | None ->
                extraWarn.Add(sprintf "subcell '%s' not found in search path" inst.CellName)

    while queue.Count > 0 do
        let cell = queue.Dequeue()
        allCells.Add cell
        for inst in cell.Instances do
            if visited.Add inst.CellName then
                match Mag.SearchPath.resolve inst.CellName searchPath with
                | Some p -> queue.Enqueue(Mag.Reader.read p)
                | None ->
                    extraWarn.Add(sprintf "subcell '%s' not found in search path" inst.CellName)

    let lib, ws = buildLibrary top (List.ofSeq allCells)
    lib, (List.ofSeq extraWarn) @ ws
