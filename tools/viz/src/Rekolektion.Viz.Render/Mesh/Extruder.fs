module Rekolektion.Viz.Render.Mesh.Extruder

open Rekolektion.Viz.Core.Gds.Types
open Rekolektion.Viz.Core.Layout

type Vertex = { X: float32; Y: float32; Z: float32; LayerKey: int * int }
type ExtrudedMesh = {
    Vertices: Vertex array
    Indices : int array
}

/// DBU -> um via the library's UserUnitsPerDbUnit (e.g., 0.001 for SKY130).
let private dbuToUm (lib: Library) (v: int64) : float32 =
    float32 (float v * lib.UserUnitsPerDbUnit)

/// Extrude a single rectilinear polygon at z0..z1. Returns 8 vertices
/// (top quad then bottom quad) and 36 triangle indices for a rect.
/// Non-rectangular polygons fall back to fan triangulation of the
/// top/bottom caps; sides still come from edge pairs.
let private extrudePolygon (lib: Library) (layer: Layer.Layer) (pts: Point list) (vertOffset: int) : Vertex array * int array =
    let stripped =
        match pts with
        | [] -> []
        | _ when List.last pts = List.head pts -> pts |> List.take (List.length pts - 1)
        | _ -> pts
    if stripped.Length < 3 then [||], [||]
    else
        let zBot = float32 layer.StackZ
        let zTop = float32 (layer.StackZ + layer.Thickness)
        let n = stripped.Length
        // Top vertices [0..n-1], then bottom [n..2n-1]
        let verts =
            [|
                for p in stripped do
                    yield { X = dbuToUm lib p.X; Y = dbuToUm lib p.Y; Z = zTop; LayerKey = layer.Number, layer.DataType }
                for p in stripped do
                    yield { X = dbuToUm lib p.X; Y = dbuToUm lib p.Y; Z = zBot; LayerKey = layer.Number, layer.DataType }
            |]
        // Top cap: fan triangulation
        let topIndices =
            [| for i in 1 .. n - 2 -> [| 0; i; i + 1 |] |] |> Array.concat
        // Bottom cap: fan, reversed winding
        let bottomIndices =
            [| for i in 1 .. n - 2 -> [| n; n + i + 1; n + i |] |] |> Array.concat
        // Sides: 2 triangles per edge
        let sideIndices =
            [|
                for i in 0 .. n - 1 do
                    let i2 = (i + 1) % n
                    yield i; yield i2; yield n + i2
                    yield i; yield n + i2; yield n + i
            |]
        let indices = Array.concat [ topIndices; bottomIndices; sideIndices ]
        let offsetIndices = indices |> Array.map ((+) vertOffset)
        verts, offsetIndices

/// Extrude every visible boundary in the library to a mesh.
let extrude (lib: Library) : ExtrudedMesh =
    let allVerts = System.Collections.Generic.List<Vertex>()
    let allIdx   = System.Collections.Generic.List<int>()
    for s in lib.Structures do
        for el in s.Elements do
            match el with
            | Boundary b ->
                match Layer.bySky130Number b.Layer b.DataType with
                | None -> ()
                | Some layer ->
                    let v, i = extrudePolygon lib layer b.Points allVerts.Count
                    allVerts.AddRange v
                    allIdx.AddRange i
            | _ -> ()
    { Vertices = allVerts.ToArray(); Indices = allIdx.ToArray() }
