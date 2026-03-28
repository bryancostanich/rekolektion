/// CLI entry point for the rekolektion SRAM visualization toolkit.
/// GDS reading, per-layer PNG rendering, and 3D mesh generation.
module Viz.Program

open System

let printUsage () =
    printfn "rekolektion Visualization Toolkit"
    printfn ""
    printfn "Usage: viz <command> [options]"
    printfn ""
    printfn "Commands:"
    printfn "  read <file.gds>                    Read GDS and print summary"
    printfn "  render <file.gds> <output_dir/>     Render per-layer PNGs"
    printfn "  mesh <file.gds> <output_dir/>       Generate STL + GLB 3D models"

let cmdRead (args: string list) =
    match args with
    | [gdsPath] ->
        let lib = Viz.Gds.Reader.readGds gdsPath
        printfn "Library: %s" lib.Name
        printfn "DB units/user unit: %g" lib.DbUnitsPerUserUnit
        printfn "DB units in meters: %g" lib.DbUnitsInMeters
        printfn "Structures: %d" lib.Structures.Length
        for s in lib.Structures do
            let boundaries = s.Elements |> List.filter (fun e -> match e with Viz.Gds.Types.Boundary _ -> true | _ -> false) |> List.length
            let paths = s.Elements |> List.filter (fun e -> match e with Viz.Gds.Types.Path _ -> true | _ -> false) |> List.length
            let srefs = s.Elements |> List.filter (fun e -> match e with Viz.Gds.Types.SRef _ -> true | _ -> false) |> List.length
            let arefs = s.Elements |> List.filter (fun e -> match e with Viz.Gds.Types.ARef _ -> true | _ -> false) |> List.length
            printfn "  %s: %d boundaries, %d paths, %d srefs, %d arefs"
                s.Name boundaries paths srefs arefs

            let allPoints =
                s.Elements |> List.collect (fun e ->
                    match e with
                    | Viz.Gds.Types.Boundary b -> b.Points
                    | Viz.Gds.Types.Path p -> p.Points
                    | _ -> [])
            if allPoints.Length > 0 then
                let minX = allPoints |> List.map (fun p -> int p.X) |> List.min
                let maxX = allPoints |> List.map (fun p -> int p.X) |> List.max
                let minY = allPoints |> List.map (fun p -> int p.Y) |> List.min
                let maxY = allPoints |> List.map (fun p -> int p.Y) |> List.max
                printfn "    BBox: (%d, %d) to (%d, %d) nm — %.3f x %.3f um"
                    minX minY maxX maxY
                    (float (maxX - minX) / 1000.0)
                    (float (maxY - minY) / 1000.0)
    | _ ->
        printfn "Usage: viz read <file.gds>"

let cmdRender (args: string list) =
    match args with
    | [gdsPath; outputDir] ->
        Viz.Render.LayerRenderer.render gdsPath outputDir 600.0
    | _ ->
        printfn "Usage: viz render <file.gds> <output_dir/>"

let cmdMesh (args: string list) =
    match args with
    | [gdsPath; outputDir] ->
        Viz.Mesh.MeshGenerator.generate gdsPath outputDir
    | _ ->
        printfn "Usage: viz mesh <file.gds> <output_dir/>"

[<EntryPoint>]
let main argv =
    let args = argv |> Array.toList
    match args with
    | "read" :: rest -> cmdRead rest; 0
    | "render" :: rest -> cmdRender rest; 0
    | "mesh" :: rest -> cmdMesh rest; 0
    | "--help" :: _ | "-h" :: _ | [] -> printUsage (); 0
    | cmd :: _ -> printfn "Unknown command: %s" cmd; printUsage (); 1
