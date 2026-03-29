/// GDS to 3D mesh (STL + GLB) generator.
/// Replicates the Python gds_to_stl.py behavior for validation.
module Viz.Mesh.MeshGenerator

open System
open System.IO
open Viz.Gds.Types

// -----------------------------------------------------------------------
// SKY130 layer stackup: (layer, datatype) → (z_bot, z_top, name, color)
// -----------------------------------------------------------------------

type StackupEntry = {
    ZBot: float    // μm
    ZTop: float    // μm
    Name: string
    ColorHex: string
}

let private sky130Stackup : Map<(int * int), StackupEntry> =
    [
        (64, 20), { ZBot = -0.20; ZTop = 0.00; Name = "nwell"; ColorHex = "#A0C8FF" }
        (65, 20), { ZBot = -0.10; ZTop = 0.05; Name = "diff";  ColorHex = "#FFD080" }
        (65, 44), { ZBot = -0.10; ZTop = 0.05; Name = "tap";   ColorHex = "#FFD080" }
        (66, 20), { ZBot =  0.00; ZTop = 0.18; Name = "poly";  ColorHex = "#FF4040" }
        (66, 44), { ZBot =  0.05; ZTop = 0.43; Name = "licon"; ColorHex = "#808080" }
        (67, 20), { ZBot =  0.43; ZTop = 0.53; Name = "li1";   ColorHex = "#C080FF" }
        (67, 44), { ZBot =  0.53; ZTop = 0.89; Name = "mcon";  ColorHex = "#606060" }
        (68, 20), { ZBot =  0.89; ZTop = 1.25; Name = "met1";  ColorHex = "#4090FF" }
        (68, 44), { ZBot =  1.25; ZTop = 1.61; Name = "via";   ColorHex = "#505050" }
        (69, 20), { ZBot =  1.61; ZTop = 1.97; Name = "met2";   ColorHex = "#40FF90" }
        (69, 44), { ZBot =  1.97; ZTop = 2.33; Name = "via2";   ColorHex = "#646464" }
        (70, 20), { ZBot =  2.33; ZTop = 2.69; Name = "met3";   ColorHex = "#FFA040" }
        (70, 44), { ZBot =  2.69; ZTop = 3.05; Name = "via3";   ColorHex = "#464646" }
        (71, 20), { ZBot =  3.05; ZTop = 3.41; Name = "met4";   ColorHex = "#FFFF40" }
        (89, 44), { ZBot =  2.50; ZTop = 2.55; Name = "mimcap"; ColorHex = "#FFC800" }
    ]
    |> Map.ofList

let private skipLayers = set [ (93, 44); (94, 20); (235, 4) ]

// -----------------------------------------------------------------------
// Triangulation
// -----------------------------------------------------------------------

type Vec3 = { X: float32; Y: float32; Z: float32 }

/// Fan triangulation for simple polygons. For quads and most VLSI rectangles
/// this is perfect. Complex concave polygons may need ear-clipping.
let private fanTriangulate (n: int) : (int * int * int) list =
    if n < 3 then []
    elif n = 3 then [(0, 1, 2)]
    elif n = 4 then [(0, 1, 2); (0, 2, 3)]
    else [ for i in 1 .. n - 2 do (0, i, i + 1) ]

/// Extrude a 2D polygon into a 3D solid. Returns list of triangles (3 vertices each).
let private extrudePolygon (points: (float * float) list) (zBot: float) (zTop: float) : Vec3 list =
    let n = points.Length
    if n < 3 then []
    else
        let result = System.Collections.Generic.List<Vec3>()

        let triIndices = fanTriangulate n

        // Bottom face (reversed winding for outward normal)
        for (a, b, c) in triIndices do
            let pa = points.[a]
            let pb = points.[b]
            let pc = points.[c]
            result.Add({ X = float32 (fst pa); Y = float32 (snd pa); Z = float32 zBot })
            result.Add({ X = float32 (fst pc); Y = float32 (snd pc); Z = float32 zBot })
            result.Add({ X = float32 (fst pb); Y = float32 (snd pb); Z = float32 zBot })

        // Top face
        for (a, b, c) in triIndices do
            let pa = points.[a]
            let pb = points.[b]
            let pc = points.[c]
            result.Add({ X = float32 (fst pa); Y = float32 (snd pa); Z = float32 zTop })
            result.Add({ X = float32 (fst pb); Y = float32 (snd pb); Z = float32 zTop })
            result.Add({ X = float32 (fst pc); Y = float32 (snd pc); Z = float32 zTop })

        // Side faces
        for i in 0 .. n - 1 do
            let j = (i + 1) % n
            let v0 = points.[i]
            let v1 = points.[j]
            // Triangle 1
            result.Add({ X = float32 (fst v0); Y = float32 (snd v0); Z = float32 zBot })
            result.Add({ X = float32 (fst v1); Y = float32 (snd v1); Z = float32 zBot })
            result.Add({ X = float32 (fst v1); Y = float32 (snd v1); Z = float32 zTop })
            // Triangle 2
            result.Add({ X = float32 (fst v0); Y = float32 (snd v0); Z = float32 zBot })
            result.Add({ X = float32 (fst v1); Y = float32 (snd v1); Z = float32 zTop })
            result.Add({ X = float32 (fst v0); Y = float32 (snd v0); Z = float32 zTop })

        result |> Seq.toList

// -----------------------------------------------------------------------
// STL output (binary format)
// -----------------------------------------------------------------------

let private writeStl (triangles: Vec3 list) (path: string) =
    let triCount = triangles.Length / 3
    use stream = File.Create(path)
    use writer = new BinaryWriter(stream)

    // 80-byte header
    let header = Array.zeroCreate<byte> 80
    let headerText = System.Text.Encoding.ASCII.GetBytes("rekolektion viz STL")
    Array.Copy(headerText, header, min headerText.Length 80)
    writer.Write(header)

    // Triangle count
    writer.Write(uint32 triCount)

    // Each triangle: normal (3 floats) + 3 vertices (9 floats) + attribute (uint16)
    for i in 0 .. triCount - 1 do
        let v0 = triangles.[i * 3]
        let v1 = triangles.[i * 3 + 1]
        let v2 = triangles.[i * 3 + 2]

        // Compute normal via cross product
        let e1x = v1.X - v0.X
        let e1y = v1.Y - v0.Y
        let e1z = v1.Z - v0.Z
        let e2x = v2.X - v0.X
        let e2y = v2.Y - v0.Y
        let e2z = v2.Z - v0.Z
        let nx = e1y * e2z - e1z * e2y
        let ny = e1z * e2x - e1x * e2z
        let nz = e1x * e2y - e1y * e2x
        let len = sqrt (float (nx * nx + ny * ny + nz * nz))
        let len32 = if len > 0.0 then float32 len else 1.0f

        writer.Write(nx / len32)
        writer.Write(ny / len32)
        writer.Write(nz / len32)
        writer.Write(v0.X); writer.Write(v0.Y); writer.Write(v0.Z)
        writer.Write(v1.X); writer.Write(v1.Y); writer.Write(v1.Z)
        writer.Write(v2.X); writer.Write(v2.Y); writer.Write(v2.Z)
        writer.Write(0us)  // attribute byte count

// -----------------------------------------------------------------------
// GLB output via SharpGLTF
// -----------------------------------------------------------------------

let private hexToRgba (hex: string) : float32 * float32 * float32 * float32 =
    let h = hex.TrimStart('#')
    let r = float32 (Convert.ToInt32(h.[0..1], 16)) / 255.0f
    let g = float32 (Convert.ToInt32(h.[2..3], 16)) / 255.0f
    let b = float32 (Convert.ToInt32(h.[4..5], 16)) / 255.0f
    (r, g, b, 1.0f)

let private addMeshToScene
    (scene: SharpGLTF.Scenes.SceneBuilder)
    (name: string) (colorHex: string)
    (vertices: Vec3 list)
    (alphaMode: SharpGLTF.Materials.AlphaMode)
    (alpha: float32)
    (transform: System.Numerics.Matrix4x4) =

    if vertices.IsEmpty then ()
    else

    let r, g, b, _ = hexToRgba colorHex
    let metallic, roughness =
        if alphaMode = SharpGLTF.Materials.AlphaMode.BLEND then 0.0f, 0.8f
        else 0.1f, 0.6f
    let material =
        SharpGLTF.Materials.MaterialBuilder(name)
            .WithDoubleSide(true)
            .WithMetallicRoughnessShader()
            .WithChannelParam(
                SharpGLTF.Materials.KnownChannel.BaseColor,
                SharpGLTF.Materials.KnownProperty.RGBA,
                System.Numerics.Vector4(r, g, b, alpha))
            .WithChannelParam(
                SharpGLTF.Materials.KnownChannel.MetallicRoughness,
                SharpGLTF.Materials.KnownProperty.MetallicFactor,
                metallic)
            .WithChannelParam(
                SharpGLTF.Materials.KnownChannel.MetallicRoughness,
                SharpGLTF.Materials.KnownProperty.RoughnessFactor,
                roughness)
    if alphaMode = SharpGLTF.Materials.AlphaMode.BLEND then
        material.WithAlpha(SharpGLTF.Materials.AlphaMode.BLEND) |> ignore

    let meshBuilder =
        new SharpGLTF.Geometry.MeshBuilder<
            SharpGLTF.Geometry.VertexTypes.VertexPosition,
            SharpGLTF.Geometry.VertexTypes.VertexEmpty,
            SharpGLTF.Geometry.VertexTypes.VertexEmpty>(name)
    let primitive = meshBuilder.UsePrimitive(material)

    let triCount = vertices.Length / 3
    for i in 0 .. triCount - 1 do
        let v0 = vertices.[i * 3]
        let v1 = vertices.[i * 3 + 1]
        let v2 = vertices.[i * 3 + 2]
        // Convert Z-up (GDS) → Y-up (glTF): swap Y and Z, then fix winding
        let mutable p0 = System.Numerics.Vector3(v0.X, v0.Z, v0.Y)
        let mutable p1 = System.Numerics.Vector3(v1.X, v1.Z, v1.Y)
        let mutable p2 = System.Numerics.Vector3(v2.X, v2.Z, v2.Y)
        let mutable gv0 = SharpGLTF.Geometry.VertexTypes.VertexPosition(&p0)
        let mutable gv1 = SharpGLTF.Geometry.VertexTypes.VertexPosition(&p1)
        let mutable gv2 = SharpGLTF.Geometry.VertexTypes.VertexPosition(&p2)
        let vb0 = SharpGLTF.Geometry.VertexBuilder<_,_,_>(&gv0)
        let vb1 = SharpGLTF.Geometry.VertexBuilder<_,_,_>(&gv1)
        let vb2 = SharpGLTF.Geometry.VertexBuilder<_,_,_>(&gv2)
        // Swap v1/v2 to fix winding after Y↔Z swap
        primitive.AddTriangle(vb0, vb2, vb1) |> ignore

    scene.AddRigidMesh(meshBuilder, transform) |> ignore

let private identityMatrix = System.Numerics.Matrix4x4.Identity
let private rotateY180 = System.Numerics.Matrix4x4.CreateRotationY(float32 System.Math.PI)

// -----------------------------------------------------------------------
// Box mesh + pixel font for in-situ strata labels
// -----------------------------------------------------------------------

/// Create a box mesh as triangles in Z-up space (X=layoutX, Y=layoutY, Z=height).
/// addMeshToScene will swap Y↔Z to produce glTF Y-up output.
let private makeBox (xMin: float) (yMin: float) (zMin: float)
                    (xMax: float) (yMax: float) (zMax: float) : Vec3 list =
    let v000 = { X = float32 xMin; Y = float32 yMin; Z = float32 zMin }
    let v001 = { X = float32 xMin; Y = float32 yMin; Z = float32 zMax }
    let v010 = { X = float32 xMin; Y = float32 yMax; Z = float32 zMin }
    let v011 = { X = float32 xMin; Y = float32 yMax; Z = float32 zMax }
    let v100 = { X = float32 xMax; Y = float32 yMin; Z = float32 zMin }
    let v101 = { X = float32 xMax; Y = float32 yMin; Z = float32 zMax }
    let v110 = { X = float32 xMax; Y = float32 yMax; Z = float32 zMin }
    let v111 = { X = float32 xMax; Y = float32 yMax; Z = float32 zMax }
    [
        // -X face
        v000; v010; v011; v000; v011; v001
        // +X face
        v100; v111; v110; v100; v101; v111
        // -Y face
        v000; v101; v100; v000; v001; v101
        // +Y face
        v010; v110; v111; v010; v111; v011
        // -Z face
        v000; v100; v110; v000; v110; v010
        // +Z face
        v001; v111; v101; v001; v011; v111
    ]

/// Pixel font for 3D text labels (4x6 grid per character).
let private pixelFont : Map<char, (int * int) list> =
    [
        'A', [(0,1);(0,2);(1,0);(1,3);(2,0);(2,1);(2,2);(2,3);(3,0);(3,3);(4,0);(4,3);(5,0);(5,3)]
        'B', [(0,0);(0,1);(0,2);(1,0);(1,3);(2,0);(2,1);(2,2);(3,0);(3,3);(4,0);(4,3);(5,0);(5,1);(5,2)]
        'C', [(0,1);(0,2);(0,3);(1,0);(2,0);(3,0);(4,0);(5,1);(5,2);(5,3)]
        'D', [(0,0);(0,1);(0,2);(1,0);(1,3);(2,0);(2,3);(3,0);(3,3);(4,0);(4,3);(5,0);(5,1);(5,2)]
        'E', [(0,0);(0,1);(0,2);(0,3);(1,0);(2,0);(2,1);(2,2);(3,0);(4,0);(5,0);(5,1);(5,2);(5,3)]
        'F', [(0,0);(0,1);(0,2);(0,3);(1,0);(2,0);(2,1);(2,2);(3,0);(4,0);(5,0)]
        'G', [(0,1);(0,2);(0,3);(1,0);(2,0);(3,0);(3,2);(3,3);(4,0);(4,3);(5,1);(5,2);(5,3)]
        'H', [(0,0);(0,3);(1,0);(1,3);(2,0);(2,1);(2,2);(2,3);(3,0);(3,3);(4,0);(4,3);(5,0);(5,3)]
        'I', [(0,0);(0,1);(0,2);(1,1);(2,1);(3,1);(4,1);(5,0);(5,1);(5,2)]
        'J', [(0,3);(1,3);(2,3);(3,3);(4,0);(4,3);(5,1);(5,2)]
        'K', [(0,0);(0,3);(1,0);(1,2);(2,0);(2,1);(3,0);(3,1);(4,0);(4,2);(5,0);(5,3)]
        'L', [(0,0);(1,0);(2,0);(3,0);(4,0);(5,0);(5,1);(5,2);(5,3)]
        'M', [(0,0);(0,3);(1,0);(1,1);(1,2);(1,3);(2,0);(2,2);(2,3);(3,0);(3,3);(4,0);(4,3);(5,0);(5,3)]
        'N', [(0,0);(0,3);(1,0);(1,1);(1,3);(2,0);(2,2);(2,3);(3,0);(3,3);(4,0);(4,3);(5,0);(5,3)]
        'O', [(0,1);(0,2);(1,0);(1,3);(2,0);(2,3);(3,0);(3,3);(4,0);(4,3);(5,1);(5,2)]
        'P', [(0,0);(0,1);(0,2);(1,0);(1,3);(2,0);(2,1);(2,2);(3,0);(4,0);(5,0)]
        'Q', [(0,1);(0,2);(1,0);(1,3);(2,0);(2,3);(3,0);(3,3);(4,0);(4,2);(5,1);(5,2);(5,3)]
        'R', [(0,0);(0,1);(0,2);(1,0);(1,3);(2,0);(2,1);(2,2);(3,0);(3,3);(4,0);(4,2);(5,0);(5,3)]
        'S', [(0,1);(0,2);(0,3);(1,0);(2,1);(2,2);(3,3);(4,3);(5,0);(5,1);(5,2)]
        'T', [(0,0);(0,1);(0,2);(0,3);(1,1);(1,2);(2,1);(2,2);(3,1);(3,2);(4,1);(4,2);(5,1);(5,2)]
        'U', [(0,0);(0,3);(1,0);(1,3);(2,0);(2,3);(3,0);(3,3);(4,0);(4,3);(5,1);(5,2)]
        'V', [(0,0);(0,3);(1,0);(1,3);(2,0);(2,3);(3,0);(3,3);(4,1);(4,2);(5,1);(5,2)]
        'W', [(0,0);(0,3);(1,0);(1,3);(2,0);(2,3);(3,0);(3,2);(3,3);(4,0);(4,1);(4,2);(4,3);(5,0);(5,3)]
        'X', [(0,0);(0,3);(1,0);(1,3);(2,1);(2,2);(3,1);(3,2);(4,0);(4,3);(5,0);(5,3)]
        'Y', [(0,0);(0,3);(1,0);(1,3);(2,1);(2,2);(3,1);(3,2);(4,1);(4,2);(5,1);(5,2)]
        'Z', [(0,0);(0,1);(0,2);(0,3);(1,3);(2,2);(3,1);(4,0);(5,0);(5,1);(5,2);(5,3)]
        '0', [(0,1);(0,2);(1,0);(1,3);(2,0);(2,3);(3,0);(3,3);(4,0);(4,3);(5,1);(5,2)]
        '1', [(0,1);(1,0);(1,1);(2,1);(3,1);(4,1);(5,0);(5,1);(5,2)]
        ' ', []
        '-', [(3,0);(3,1);(3,2);(3,3)]
        '/', [(5,0);(4,1);(3,2);(2,2);(1,3);(0,3)]
        '(', [(0,2);(1,1);(2,1);(3,1);(4,1);(5,2)]
        ')', [(0,1);(1,2);(2,2);(3,2);(4,2);(5,1)]
        '.', [(5,1)]
    ] |> Map.ofList

/// Generate text mesh as pixel-font block extrusions in Z-up space.
/// X = layout X, Y = front face (layout Y), Z = stack height.
/// addMeshToScene will swap Y↔Z to produce glTF Y-up output.
let private makeTextMesh (text: string) (x: float) (y: float)
                         (zCenter: float) (pixelSize: float) (depth: float) : Vec3 list =
    let text = text.ToUpper()
    let charW = 4
    let charH = 6
    let charSpacing = 1

    let totalH = float charH * pixelSize
    let zStart = zCenter + totalH / 2.0

    let totalTextW = float (text.Length) * float (charW + charSpacing) * pixelSize
    let mutable cursorX = x + totalTextW

    let result = System.Collections.Generic.List<Vec3>()
    for ch in text do
        cursorX <- cursorX - float (charW + charSpacing) * pixelSize
        match pixelFont |> Map.tryFind ch with
        | None -> ()
        | Some pixels ->
            for (row, col) in pixels do
                let mirroredCol = (charW - 1) - col
                let px = cursorX + float mirroredCol * pixelSize
                let pz = zStart - float row * pixelSize - pixelSize
                // Box: X range, Y = front face (thin depth), Z = height
                let box = makeBox px (y - depth) pz (px + pixelSize) y (pz + pixelSize)
                result.AddRange(box)
    result |> Seq.toList

// -----------------------------------------------------------------------
// In-situ GLB: cell embedded in process cross-section strata
// -----------------------------------------------------------------------

let private generateInSitu (gdsPath: string) (outputDir: string) : unit =
    let lib = Viz.Gds.Reader.readGds gdsPath
    let cell =
        match lib.Structures with
        | [] -> failwith "No structures in GDS file"
        | structs -> structs |> List.last

    let allPoints =
        cell.Elements
        |> List.collect (fun e ->
            match e with
            | Boundary b -> b.Points
            | Path p -> p.Points
            | _ -> [])

    if allPoints.IsEmpty then
        printfn "No geometry for in-situ GLB"
    else

    let scale = 10.0

    let minX = allPoints |> List.map (fun p -> float p.X / 1000.0 * scale) |> List.min
    let maxX = allPoints |> List.map (fun p -> float p.X / 1000.0 * scale) |> List.max
    let minY = allPoints |> List.map (fun p -> float p.Y / 1000.0 * scale) |> List.min
    let maxY = allPoints |> List.map (fun p -> float p.Y / 1000.0 * scale) |> List.max
    let margin = 0.5 * scale

    // SKY130 process cross-section strata
    let strata = [
        ("Si substrate",     -0.50, -0.20, "#8B7355", 50)
        ("P-well / N-well",  -0.20,  0.00, "#A09080", 40)
        ("STI oxide",        -0.10,  0.00, "#D0D0E0", 35)
        ("Gate oxide",        0.00,  0.01, "#E8E8F8", 30)
        ("ILD0 (pre-metal)",  0.18,  0.43, "#C8E8C8", 35)
        ("ILD1 (via0)",       0.53,  0.89, "#E8E0C8", 35)
        ("IMD1 (above met1)", 1.25,  1.61, "#D0D8E8", 30)
        ("IMD2 (above met2)", 1.97,  2.33, "#D8D0E8", 30)
        ("IMD3 (above met3)", 2.69,  3.05, "#E8D0D0", 30)
        ("Passivation",       3.41,  3.60, "#E0E0E0", 25)
    ]

    let scene = new SharpGLTF.Scenes.SceneBuilder("bitcell_in_situ")

    let pixelSize = 0.010 * scale
    let textDepth = 0.007 * scale
    let textX = minX - margin + 0.1 * scale
    let textY = minY - margin  // front face in layout Y (becomes Z in glTF)

    // Add strata boxes + text labels
    // Z-up space: X = layout X, Y = layout Y, Z = stack height
    for (sname, zBotUm, zTopUm, colorHex, alphaByte) in strata do
        let zBot = zBotUm * scale
        let zTop = zTopUm * scale
        let alpha = float32 alphaByte / 255.0f

        // Box: X=[xMin-margin, xMax+margin], Y=[yMin-margin, yMax+margin], Z=[zBot, zTop]
        let boxVerts =
            makeBox (minX - margin) (minY - margin) zBot
                    (maxX + margin) (maxY + margin) zTop

        addMeshToScene scene sname colorHex boxVerts SharpGLTF.Materials.AlphaMode.BLEND alpha rotateY180

        // Text label on front face (-Y side in Z-up, becomes -Z in glTF)
        let zCenter = (zBot + zTop) / 2.0
        let layerThickness = zTop - zBot
        let effectivePixel = min pixelSize (layerThickness / 7.0)
        if effectivePixel >= 0.008 * scale then
            let textVerts = makeTextMesh sname textX textY zCenter effectivePixel textDepth
            if not textVerts.IsEmpty then
                addMeshToScene scene $"label: {sname}" "#FFFFFF" textVerts SharpGLTF.Materials.AlphaMode.BLEND 0.9f rotateY180

    // Add actual cell features as opaque meshes
    let layerGroups =
        cell.Elements
        |> List.choose (fun e ->
            match e with
            | Boundary b -> Some b
            | _ -> None)
        |> List.groupBy (fun b -> (b.Layer, b.Datatype))
        |> Map.ofList

    for layerKey in layerGroups |> Map.keys |> Seq.sort do
        if skipLayers.Contains layerKey then ()
        else
        match sky130Stackup |> Map.tryFind layerKey with
        | None -> ()
        | Some entry ->
            let boundaries = layerGroups.[layerKey]
            let zBot = entry.ZBot * scale
            let zTop = entry.ZTop * scale

            let layerTris = System.Collections.Generic.List<Vec3>()
            for b in boundaries do
                let pts =
                    b.Points
                    |> List.map (fun p ->
                        (float p.X / 1000.0 * scale, float p.Y / 1000.0 * scale))
                let tris = extrudePolygon pts zBot zTop
                layerTris.AddRange(tris)

            if layerTris.Count > 0 then
                addMeshToScene scene entry.Name entry.ColorHex (layerTris |> Seq.toList) SharpGLTF.Materials.AlphaMode.OPAQUE 1.0f rotateY180

    let model = scene.ToGltf2()
    let glbPath = Path.Combine(outputDir, "bitcell_3d_in_situ.glb")
    model.SaveGLB(glbPath)
    printfn "GLB (in-situ): %s" glbPath

// -----------------------------------------------------------------------
// Public API
// -----------------------------------------------------------------------

/// Generate STL + GLB 3D models from a GDS file.
let generate (gdsPath: string) (outputDir: string) : unit =
    let lib = Viz.Gds.Reader.readGds gdsPath
    let cell =
        match lib.Structures with
        | [] -> failwith "No structures in GDS file"
        | structs -> structs |> List.last

    printfn "Processing cell: %s" cell.Name

    // Group boundaries by (layer, datatype)
    let layerGroups =
        cell.Elements
        |> List.choose (fun e ->
            match e with
            | Boundary b -> Some b
            | _ -> None)
        |> List.groupBy (fun b -> (b.Layer, b.Datatype))
        |> Map.ofList

    Directory.CreateDirectory(outputDir) |> ignore

    let scale = 10.0  // 10 units per um
    let allTriangles = System.Collections.Generic.List<Vec3>()
    let scene = new SharpGLTF.Scenes.SceneBuilder("bitcell")

    for layerKey in layerGroups |> Map.keys |> Seq.sort do
        if skipLayers.Contains layerKey then ()
        else
        match sky130Stackup |> Map.tryFind layerKey with
        | None -> ()
        | Some entry ->
            let boundaries = layerGroups.[layerKey]
            let zBot = entry.ZBot * scale
            let zTop = entry.ZTop * scale

            let layerTris = System.Collections.Generic.List<Vec3>()

            for b in boundaries do
                let pts =
                    b.Points
                    |> List.map (fun p ->
                        (float p.X / 1000.0 * scale, float p.Y / 1000.0 * scale))
                let tris = extrudePolygon pts zBot zTop
                layerTris.AddRange(tris)

            if layerTris.Count > 0 then
                printfn "  %s: %d polygons, %d triangles, z=[%.2f, %.2f]"
                    entry.Name boundaries.Length (layerTris.Count / 3) entry.ZBot entry.ZTop

                // Per-layer STL
                let layerPath = Path.Combine(outputDir, $"{entry.Name}_{fst layerKey}_{snd layerKey}.stl")
                writeStl (layerTris |> Seq.toList) layerPath

                // Add to combined
                allTriangles.AddRange(layerTris)

                // Add to GLB scene
                addMeshToScene scene entry.Name entry.ColorHex (layerTris |> Seq.toList) SharpGLTF.Materials.AlphaMode.OPAQUE 1.0f identityMatrix

    // Combined STL
    if allTriangles.Count > 0 then
        let combinedPath = Path.Combine(outputDir, "bitcell_3d_combined.stl")
        writeStl (allTriangles |> Seq.toList) combinedPath
        printfn "Combined STL: %s (%d triangles)" combinedPath (allTriangles.Count / 3)

    // GLB
    let model = scene.ToGltf2()
    let glbPath = Path.Combine(outputDir, "bitcell_3d.glb")
    model.SaveGLB(glbPath)
    printfn "GLB: %s" glbPath

    // In-situ GLB
    generateInSitu gdsPath outputDir
