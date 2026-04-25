/// CLI entry point for the rekolektion-viz toolkit. Dispatches on
/// the first argv token to one of: read | render | mesh | app |
/// viz-render. `read` is fully ported from the legacy
/// `tools/viz/Program.fs`; `render` and `mesh` remain stubs in
/// Phase 1 (LayerRenderer / MeshGenerator not yet ported); `app`
/// hands off to `Rekolektion.Viz.App.Program.runDesktop`;
/// `viz-render` is stubbed for Task 27.
module Rekolektion.Viz.Cli.Program

open Rekolektion.Viz.Core.Gds

let private printUsage () =
    printfn "rekolektion-viz <command> [options]"
    printfn ""
    printfn "Commands:"
    printfn "  read   <file.gds>                       GDS summary"
    printfn "  render <file.gds> <out_dir/>            Per-layer PNGs"
    printfn "  mesh   <file.gds> <out_dir/>            STL + GLB 3D models"
    printfn "  app    [<file.gds>]                     Launch GUI"
    printfn "  viz-render --gds <f> --output <p.png>"
    printfn "             [--toggle-layer <n>=on|off]"
    printfn "             [--highlight-net <n>] [--tab 2D|3D]"
    printfn "             [--width <px>] [--height <px>] [--hold-ms <ms>]"

/// `read <file.gds>` — print a Library / Structures summary
/// modelled on the legacy `Viz.Program.cmdRead` output: library
/// name, DB-unit scale factors, per-structure element counts, and
/// a per-structure bounding box in DBU and micrometers. DBU→nm
/// uses `DbUnitsInMeters`, mirroring the legacy report.
let cmdRead (args: string list) : int =
    match args with
    | [path] ->
        let lib = Reader.readGds path
        printfn "Library: %s" lib.Name
        printfn "User units/DB unit: %g" lib.UserUnitsPerDbUnit
        printfn "DB units in meters: %g" lib.DbUnitsInMeters
        printfn "Structures: %d" lib.Structures.Length
        for s in lib.Structures do
            let boundaries =
                s.Elements
                |> List.filter (function Types.Boundary _ -> true | _ -> false)
                |> List.length
            let paths =
                s.Elements
                |> List.filter (function Types.Path _ -> true | _ -> false)
                |> List.length
            let srefs =
                s.Elements
                |> List.filter (function Types.SRef _ -> true | _ -> false)
                |> List.length
            let arefs =
                s.Elements
                |> List.filter (function Types.ARef _ -> true | _ -> false)
                |> List.length
            printfn "  %s: %d boundaries, %d paths, %d srefs, %d arefs"
                s.Name boundaries paths srefs arefs

            let allPoints =
                s.Elements
                |> List.collect (fun e ->
                    match e with
                    | Types.Boundary b -> b.Points
                    | Types.Path p     -> p.Points
                    | _ -> [])
            if not allPoints.IsEmpty then
                let minX = allPoints |> List.map (fun p -> p.X) |> List.min
                let maxX = allPoints |> List.map (fun p -> p.X) |> List.max
                let minY = allPoints |> List.map (fun p -> p.Y) |> List.min
                let maxY = allPoints |> List.map (fun p -> p.Y) |> List.max
                // DBU → nm scale: DbUnitsInMeters * 1e9 nm/m. For
                // SKY130 GDS this is 1.0 nm/DBU, matching the legacy
                // report's "(maxX-minX) nm" assumption.
                let nmPerDbu = lib.DbUnitsInMeters * 1.0e9
                let widthNm  = float (maxX - minX) * nmPerDbu
                let heightNm = float (maxY - minY) * nmPerDbu
                printfn "    BBox: (%d, %d) to (%d, %d) DBU — %.3f x %.3f um"
                    minX minY maxX maxY
                    (widthNm / 1000.0)
                    (heightNm / 1000.0)
        0
    | _ -> printUsage(); 1

/// `render <file.gds> <out_dir/>` — STUB. The legacy
/// `Viz.Render.LayerRenderer` has not been ported into
/// Rekolektion.Viz.Render yet; until Task N ports it, redirect
/// callers to the legacy Viz.fsproj.
let cmdRender (_args: string list) : int =
    printfn "render: not yet implemented in Phase 1 (port LayerRenderer pending)"
    printfn "  use the legacy CLI for now: dotnet run --project tools/viz/Viz.fsproj -- render ..."
    1

/// `mesh <file.gds> <out_dir/>` — STUB. The legacy
/// `Viz.Mesh.MeshGenerator` has not been ported into
/// Rekolektion.Viz.Render yet; until Task N ports it, redirect
/// callers to the legacy Viz.fsproj.
let cmdMesh (_args: string list) : int =
    printfn "mesh: not yet implemented in Phase 1 (port MeshGenerator pending)"
    printfn "  use the legacy CLI for now: dotnet run --project tools/viz/Viz.fsproj -- mesh ..."
    1

/// `app [args...]` — boot the Avalonia desktop GUI. Phase 1
/// doesn't auto-open a GDS from argv; that wiring will land when
/// the App grows a `--gds` startup arg. For now we just forward
/// argv unchanged so future flags don't need a CLI change.
let cmdApp (args: string list) : int =
    let argv = args |> List.toArray
    Rekolektion.Viz.App.Program.runDesktop argv

/// `viz-render --gds ... --output ...` — STUB. Task 26 stops at
/// dispatch wiring; Task 27 implements full flag parsing and
/// invokes `App.HeadlessRender.renderToPng` with toggle Msgs
/// pre-applied to the headless model.
let cmdVizRender (_args: string list) : int =
    printfn "viz-render: implemented in Task 27 (flag parsing + headless toggles)"
    0

[<EntryPoint>]
let main argv =
    match argv |> Array.toList with
    | "read" :: rest        -> cmdRead rest
    | "render" :: rest      -> cmdRender rest
    | "mesh" :: rest        -> cmdMesh rest
    | "app" :: rest         -> cmdApp rest
    | "viz-render" :: rest  -> cmdVizRender rest
    | "--help" :: _ | "-h" :: _ | [] -> printUsage(); 0
    | cmd :: _ -> printfn "Unknown command: %s" cmd; printUsage(); 1
