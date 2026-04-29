module Rekolektion.Viz.Render.Mesh.Extruder

open Rekolektion.Viz.Core.Gds.Types
open Rekolektion.Viz.Core.Layout
open Rekolektion.Viz.Core.Layout.Flatten

type Vertex = { X: float32; Y: float32; Z: float32; LayerKey: int * int }
type ExtrudedMesh = {
    Vertices: Vertex array
    Indices : int array
}

/// Extrude a single rectilinear polygon at z0..z1. Top + bottom fan-
/// triangulated; sides triangulated as quad pairs. Returns the
/// vertices/indices for one polygon, with indices offset by
/// `vertOffset` so they slot into a global mesh buffer.
let private extrudePolygon
        (umPerDbu: float)
        (layer: Layer.Layer)
        (pts: Point array)
        (vertOffset: int)
        : Vertex array * int array =
    // Drop trailing duplicate of first point if present.
    let n =
        if pts.Length >= 2 && pts.[0] = pts.[pts.Length - 1] then pts.Length - 1
        else pts.Length
    if n < 3 then [||], [||]
    else
        let zBot = float32 layer.StackZ
        let zTop = float32 (layer.StackZ + layer.Thickness)
        let toUm (v: int64) = float32 (float v * umPerDbu)
        // Top vertices [0..n-1], then bottom [n..2n-1]
        let verts = Array.zeroCreate<Vertex> (2 * n)
        for i in 0 .. n - 1 do
            verts.[i] <-
                { X = toUm pts.[i].X; Y = toUm pts.[i].Y; Z = zTop
                  LayerKey = layer.Number, layer.DataType }
            verts.[n + i] <-
                { X = toUm pts.[i].X; Y = toUm pts.[i].Y; Z = zBot
                  LayerKey = layer.Number, layer.DataType }
        // Top cap: fan triangulation from vert 0
        let topCount = (n - 2) * 3
        let bottomCount = (n - 2) * 3
        let sideCount = n * 6
        let indices = Array.zeroCreate<int> (topCount + bottomCount + sideCount)
        let mutable k = 0
        for i in 1 .. n - 2 do
            indices.[k] <- vertOffset + 0
            indices.[k + 1] <- vertOffset + i
            indices.[k + 2] <- vertOffset + i + 1
            k <- k + 3
        // Bottom cap: fan, reversed winding so normal points -Z
        for i in 1 .. n - 2 do
            indices.[k] <- vertOffset + n
            indices.[k + 1] <- vertOffset + n + i + 1
            indices.[k + 2] <- vertOffset + n + i
            k <- k + 3
        // Sides: 2 tris per edge
        for i in 0 .. n - 1 do
            let i2 = (i + 1) % n
            indices.[k] <- vertOffset + i
            indices.[k + 1] <- vertOffset + i2
            indices.[k + 2] <- vertOffset + n + i2
            indices.[k + 3] <- vertOffset + i
            indices.[k + 4] <- vertOffset + n + i2
            indices.[k + 5] <- vertOffset + n + i
            k <- k + 6
        verts, indices

/// Extrude every flat polygon to a 3D mesh. `flat` should be the
/// output of `Rekolektion.Viz.Core.Layout.Flatten.flatten` so
/// hierarchical macros emit their full bitcell array (or whatever
/// SRef/ARef content) rather than only the top cell.
let extrude (umPerDbu: float) (flat: FlatPolygon array) : ExtrudedMesh =
    // Pre-size with rough estimates. Most polygons are 4-corner
    // rectangles → 8 verts + 36 indices each.
    let estimatedVerts = flat.Length * 8
    let estimatedIdx   = flat.Length * 36
    let allVerts = System.Collections.Generic.List<Vertex>(estimatedVerts)
    let allIdx   = System.Collections.Generic.List<int>(estimatedIdx)
    for poly in flat do
        match Layer.bySky130Number poly.Layer poly.DataType with
        | None -> ()
        | Some layer ->
            let v, i = extrudePolygon umPerDbu layer poly.Points allVerts.Count
            allVerts.AddRange v
            allIdx.AddRange i
    { Vertices = allVerts.ToArray(); Indices = allIdx.ToArray() }
