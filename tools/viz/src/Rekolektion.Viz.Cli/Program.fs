/// CLI entry point for the rekolektion-viz toolkit. Dispatches on
/// the first argv token to one of: read | render | mesh | app |
/// viz-render. `read` is fully ported from the legacy
/// `tools/viz/Program.fs`; `render` and `mesh` remain stubs in
/// Phase 1 (LayerRenderer / MeshGenerator not yet ported); `app`
/// hands off to `Rekolektion.Viz.App.Program.runDesktop`;
/// `viz-render` is stubbed for Task 27.
module Rekolektion.Viz.Cli.Program

open Rekolektion.Viz.Core.Gds
open Avalonia.VisualTree

let private printUsage () =
    printfn "rekolektion-viz <command> [options]"
    printfn ""
    printfn "Commands:"
    printfn "  read   <file.gds>                       GDS summary"
    printfn "  to-gds <input.rkt|.mag> <out.gds>       Export to canonical GDS"
    printfn "  to-lef <input.rkt> <out.lef>            Emit LEF 5.7 abstract"
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
        // Dispatch on extension: .mag → Magic parser, .gds → GDS,
        // .rkt → Rkt reader. All produce a canonical `Rkt.Document`.
        let doc, warnings =
            Rekolektion.Viz.Core.Layout.LayoutLoader.load path
        for w in warnings do
            eprintfn "[viz] %s" w
        let nmPerDbu = float doc.Units.DbuNm
        printfn "Document: pdk %s, version %d" doc.Pdk doc.Version
        printfn "Units: %g nm/DBU (uu_um %d)" nmPerDbu doc.Units.UuUm
        printfn "Cells: %d" doc.Cells.Length
        for c in doc.Cells do
            let polys =
                c.Elements
                |> List.filter (function
                    | Rekolektion.Viz.Core.Rkt.Types.PolyEl _ -> true
                    | Rekolektion.Viz.Core.Rkt.Types.RectEl _ -> true
                    | _ -> false)
                |> List.length
            let paths =
                c.Elements
                |> List.filter (function
                    | Rekolektion.Viz.Core.Rkt.Types.PathEl _ -> true
                    | _ -> false)
                |> List.length
            let srefs =
                c.Elements
                |> List.filter (function
                    | Rekolektion.Viz.Core.Rkt.Types.SRefEl _ -> true
                    | _ -> false)
                |> List.length
            let arefs =
                c.Elements
                |> List.filter (function
                    | Rekolektion.Viz.Core.Rkt.Types.ARefEl _ -> true
                    | _ -> false)
                |> List.length
            printfn "  %s: %d polys, %d paths, %d srefs, %d arefs"
                c.Name polys paths srefs arefs

            let allPoints =
                c.Elements
                |> List.collect (fun e ->
                    match e with
                    | Rekolektion.Viz.Core.Rkt.Types.PolyEl p -> p.Points
                    | Rekolektion.Viz.Core.Rkt.Types.PathEl p -> p.Points
                    | Rekolektion.Viz.Core.Rkt.Types.RectEl r ->
                        [ { Rekolektion.Viz.Core.Rkt.Types.X = r.X1
                            Rekolektion.Viz.Core.Rkt.Types.Y = r.Y1 }
                          { Rekolektion.Viz.Core.Rkt.Types.X = r.X2
                            Rekolektion.Viz.Core.Rkt.Types.Y = r.Y2 } ]
                    | _ -> [])
            if not allPoints.IsEmpty then
                let minX = allPoints |> List.map (fun p -> p.X) |> List.min
                let maxX = allPoints |> List.map (fun p -> p.X) |> List.max
                let minY = allPoints |> List.map (fun p -> p.Y) |> List.min
                let maxY = allPoints |> List.map (fun p -> p.Y) |> List.max
                let widthNm  = float (maxX - minX) * nmPerDbu
                let heightNm = float (maxY - minY) * nmPerDbu
                printfn "    BBox: (%d, %d) to (%d, %d) DBU — %.3f x %.3f um"
                    minX minY maxX maxY
                    (widthNm / 1000.0)
                    (heightNm / 1000.0)
        0
    | _ -> printUsage(); 1

/// `to-gds <input.rkt|.mag|.gds> <output.gds>` — export any
/// LayoutLoader-supported file as canonical sky130 GDS. Used by
/// the Python DRC integration (`rekolektion.verify.run_drc`) to
/// hand a block to Magic for checking. Goes through the same
/// `Rkt.ToGds.toLibrary` + `Gds.Writer.writeGds` pipeline as the
/// rest of the toolchain — same layer-map fixes, same coordinate
/// scaling, no second source of truth.
let cmdToGds (args: string list) : int =
    match args with
    | [input; output] ->
        let doc, warnings =
            Rekolektion.Viz.Core.Layout.LayoutLoader.load input
        for w in warnings do
            eprintfn "[viz] %s" w
        let lib = Rekolektion.Viz.Core.Rkt.ToGds.toLibrary doc
        Rekolektion.Viz.Core.Gds.Writer.writeGds output lib
        printfn "wrote %s (%d cells)" output lib.Structures.Length
        0
    | _ ->
        printfn "usage: to-gds <input.rkt|.mag|.gds> <output.gds>"
        1

/// `to-lef <input.rkt> <output.lef>
///        [--cell <name>] [--uppercase-pins]
///        [--obs none|fullsize|derived] [--obs-layers met1,met2,…]` —
/// emit a LEF 5.7 abstract from a `.rkt` cell. `--cell` defaults to
/// the document's `(top …)`, or the first cell if `(top …)` is
/// absent. `--cell *` emits every cell in the document as one LEF
/// library.
let cmdToLef (args: string list) : int =
    let printToLefUsage () =
        printfn "usage: to-lef <input.rkt> <output.lef>"
        printfn "              [--cell <name>|*]"
        printfn "              [--uppercase-pins]"
        printfn "              [--obs none|fullsize|derived|band-excluding]"
        printfn "              [--obs-layers met1,met2,...]"
        printfn "              [--obs-band <layer>:<y0_um>:<y1_um>]   (repeatable)"
        printfn "              [--decimal-precision N]"
        printfn "              [--emit-abutment-shape]"
        printfn "              [--symmetry <text>]"
        printfn "              [--omit-foreign-offset]"
        printfn "              [--legacy-zero-short-form]"
    // Split positional and flag arguments. Flags are key/value
    // pairs; standalone flags (`--uppercase-pins`) are boolean.
    let mutable positional : string list = []
    let mutable cell : string option = None
    let mutable uppercase = false
    let mutable obsMode : string option = None
    let mutable obsLayers : string list option = None
    let mutable obsBands : (string * decimal * decimal) list = []
    let mutable decimalPrecision : int option = None
    let mutable emitAbutmentShape = false
    let mutable symmetry : string option = None
    let mutable omitForeignOffset = false
    let mutable legacyZeroShortForm = false
    let parseBand (s: string) : Result<string * decimal * decimal, string> =
        match s.Split(':') with
        | [| layer; y0; y1 |] ->
            match System.Decimal.TryParse(y0, System.Globalization.NumberStyles.Float,
                                          System.Globalization.CultureInfo.InvariantCulture),
                  System.Decimal.TryParse(y1, System.Globalization.NumberStyles.Float,
                                          System.Globalization.CultureInfo.InvariantCulture) with
            | (true, y0v), (true, y1v) -> Ok (layer, y0v, y1v)
            | _ -> Error (sprintf "--obs-band needs <layer>:<y0>:<y1>, got '%s'" s)
        | _ -> Error (sprintf "--obs-band needs <layer>:<y0>:<y1>, got '%s'" s)
    let rec parse = function
        | [] -> Ok ()
        | "--cell" :: v :: rest -> cell <- Some v; parse rest
        | "--uppercase-pins" :: rest -> uppercase <- true; parse rest
        | "--obs" :: v :: rest -> obsMode <- Some v; parse rest
        | "--obs-layers" :: v :: rest ->
            obsLayers <- Some (v.Split(',') |> Array.toList |> List.map (fun s -> s.Trim()))
            parse rest
        | "--obs-band" :: v :: rest ->
            match parseBand v with
            | Error e -> Error e
            | Ok band -> obsBands <- obsBands @ [ band ]; parse rest
        | "--decimal-precision" :: v :: rest ->
            match System.Int32.TryParse v with
            | true, n -> decimalPrecision <- Some n; parse rest
            | _ -> Error (sprintf "--decimal-precision needs integer, got '%s'" v)
        | "--emit-abutment-shape" :: rest ->
            emitAbutmentShape <- true; parse rest
        | "--symmetry" :: v :: rest ->
            symmetry <- Some v; parse rest
        | "--omit-foreign-offset" :: rest ->
            omitForeignOffset <- true; parse rest
        | "--legacy-zero-short-form" :: rest ->
            legacyZeroShortForm <- true; parse rest
        | s :: _ when s.StartsWith "--" ->
            Error (sprintf "unknown or incomplete flag: %s" s)
        | s :: rest ->
            positional <- positional @ [ s ]; parse rest
    match parse args with
    | Error e -> eprintfn "to-lef: %s" e; printToLefUsage (); 1
    | Ok () ->
    match positional with
    | [ input; output ] ->
        match Rekolektion.Viz.Core.Rkt.Reader.readFile input with
        | Error e ->
            eprintfn "to-lef: parse error: %A" e
            1
        | Ok (_, doc) ->
            let defaults = Rekolektion.Viz.Core.Rkt.ToLef.EmitOptions.defaults
            let obsPolicy =
                let layers = obsLayers |> Option.defaultValue [ "met1"; "met2" ]
                match obsMode with
                | None -> defaults.Obstructions
                | Some "none" -> Rekolektion.Viz.Core.Rkt.ToLef.NoObs
                | Some "fullsize" -> Rekolektion.Viz.Core.Rkt.ToLef.FullSize layers
                | Some "derived" -> Rekolektion.Viz.Core.Rkt.ToLef.DerivedFromGeometry layers
                | Some "band-excluding" ->
                    Rekolektion.Viz.Core.Rkt.ToLef.BandExcluding (layers, obsBands)
                | Some other ->
                    eprintfn "to-lef: unknown --obs value '%s' (expected none|fullsize|derived|band-excluding)" other
                    defaults.Obstructions
            let pinCase =
                if uppercase then Rekolektion.Viz.Core.Rkt.ToLef.Uppercase
                else Rekolektion.Viz.Core.Rkt.ToLef.Verbatim
            let options =
                { defaults with
                    PinCase = pinCase
                    Obstructions = obsPolicy
                    DecimalPrecision = decimalPrecision
                    EmitAbutmentShape = emitAbutmentShape
                    Symmetry = symmetry
                    OmitForeignOffset = omitForeignOffset
                    LegacyZeroShortForm = legacyZeroShortForm }
            let cellChoice =
                cell
                |> Option.orElse doc.TopCell
                |> Option.orElseWith (fun () ->
                    doc.Cells |> List.tryHead |> Option.map (fun c -> c.Name))
            let result =
                match cellChoice with
                | Some "*" ->
                    Rekolektion.Viz.Core.Rkt.ToLef.emitDocument options doc
                | Some name ->
                    Rekolektion.Viz.Core.Rkt.ToLef.emitCell options doc name
                | None ->
                    Error (Rekolektion.Viz.Core.Rkt.ToLef.NoSuchCell "(no cells in document)")
            match result with
            | Error err ->
                eprintfn "to-lef: %s" (Rekolektion.Viz.Core.Rkt.ToLef.formatError err)
                1
            | Ok lef ->
                System.IO.File.WriteAllText(output, lef)
                printfn "wrote %s" output
                0
    | _ -> printToLefUsage (); 1

/// `render <file.gds> <out_dir/>` — STUB. The legacy
/// `Viz.Render.LayerRenderer` has not been ported into
/// Rekolektion.Viz.Render yet; until Task N ports it, redirect
/// callers to the legacy Viz.fsproj.
/// `dump-layer <input> <layerNum> <dt>` — print the bbox of every
/// FlatPolygon on a specific (layer, datatype) so we can see what
/// viz's DRC pass is actually iterating over.
let cmdDumpLayer (args: string list) : int =
    match args with
    | [path; ln; dt] ->
        let layer = int ln
        let datatype = int dt
        let doc, _ = Rekolektion.Viz.Core.Layout.LayoutLoader.load path
        let flat = Rekolektion.Viz.Core.Layout.Flatten.flatten doc
        let mutable n = 0
        for p in flat do
            if p.Layer = layer && p.DataType = datatype then
                let mutable xMin = System.Int64.MaxValue
                let mutable yMin = System.Int64.MaxValue
                let mutable xMax = System.Int64.MinValue
                let mutable yMax = System.Int64.MinValue
                for pt in p.Points do
                    if pt.X < xMin then xMin <- pt.X
                    if pt.X > xMax then xMax <- pt.X
                    if pt.Y < yMin then yMin <- pt.Y
                    if pt.Y > yMax then yMax <- pt.Y
                printfn "%d %d %d %d  src=%s/%d"
                    xMin yMin xMax yMax p.SourceStructure p.SourceIndex
                n <- n + 1
        eprintfn "=== %d polys on %d/%d ===" n layer datatype
        0
    | _ ->
        eprintfn "usage: rekolektion-viz dump-layer <input> <num> <dt>"
        1

/// `drc [--compat klayout|magic] <input.rkt|.gds|.mag>` — load,
/// flatten, run the full DRC check against the chosen compat
/// target, and print every Violation as one CSV-ish line. Lets the
/// caller (and the Phase 4 equivalency harness) diff viz's reported
/// violations against either Magic or KLayout output.
///
/// Default is `--compat klayout` to match the rest of the project
/// (Track 02). Phase 3 state: KLayout's F# rule list is empty, so
/// `--compat klayout` returns 0 violations until Phase 4 populates
/// rules. Use `--compat magic` for the existing Magic-tuned ruleset.
let cmdDrc (args: string list) : int =
    let rec parse (acc: string option) (compat: Rekolektion.Viz.Core.Drc.Compat.Compat) (rest: string list) =
        match rest with
        | "--compat" :: v :: tail ->
            match Rekolektion.Viz.Core.Drc.Compat.parse v with
            | Some c -> parse acc c tail
            | None ->
                eprintfn "drc: unknown --compat value %s (expected klayout|magic)" v
                Error 2
        | x :: tail when acc.IsNone -> parse (Some x) compat tail
        | [] when acc.IsSome -> Ok (acc.Value, compat)
        | _ ->
            eprintfn "usage: rekolektion-viz drc [--compat klayout|magic] <input.rkt|.gds|.mag>"
            Error 1
    match parse None Rekolektion.Viz.Core.Drc.Compat.defaultCompat args with
    | Error rc -> rc
    | Ok (path, compat) ->
        let doc, warnings =
            Rekolektion.Viz.Core.Layout.LayoutLoader.load path
        for w in warnings do eprintfn "[viz] %s" w
        let flat = Rekolektion.Viz.Core.Layout.Flatten.flatten doc
        let view = Rekolektion.Viz.Core.Drc.Rules.viewFor compat
        let viols =
            Rekolektion.Viz.Core.Drc.Check.checkWithCompat
                compat view doc.Units flat
        let layerName n d =
            match Rekolektion.Viz.Core.Layout.Layer.bySky130Number n d with
            | Some l -> l.Name
            | None -> sprintf "%d/%d" n d
        printfn "rule\tlayer\tlimit_dbu\tmeasured_dbu\tbbox_a\tbbox_b"
        for v in viols do
            let (ax1, ay1, ax2, ay2) = v.BboxA
            let bStr =
                match v.BboxB with
                | Some (bx1, by1, bx2, by2) ->
                    sprintf "(%d,%d,%d,%d)" bx1 by1 bx2 by2
                | None -> ""
            printfn "%s\t%s\t%d\t%d\t(%d,%d,%d,%d)\t%s"
                v.Rule (layerName v.LayerNumber v.LayerType)
                v.LimitDbu v.MeasuredDbu
                ax1 ay1 ax2 ay2 bStr
        eprintfn "=== %d violations (compat=%s) ==="
            viols.Length (Rekolektion.Viz.Core.Drc.Compat.toString compat)
        0

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

/// `viz-render --gds ... --output ...` — boot the App headlessly,
/// dispatch a pre-render Msg sequence (OpenFile + per-layer
/// toggles + optional highlight + tab switch), then capture a
/// PNG of the resulting MainWindow. Used by the MCP
/// `rekolektion_viz_render` tool (Task 29) so agents can inspect
/// arbitrary GDS macros without a live Viz session.
///
/// Unknown layer names from `--toggle-layer` are silently
/// dropped via `List.choose` here. CommandListener returns a JSON
/// error in the same situation; for the one-shot CLI path we
/// match `List.choose`'s drop-and-continue semantics so a
/// typo in one layer doesn't fail the whole render.
let cmdVizRender (args: string list) : int =
    match Rekolektion.Viz.App.HeadlessRenderArgs.parseVizRenderArgs args with
    | Error msg ->
        eprintfn "viz-render: %s" msg
        1
    | Ok parsed ->
        let openMsg =
            Rekolektion.Viz.App.Model.Msg.Msg.OpenFile parsed.Gds
        let toggleMsgs =
            parsed.Toggles
            |> List.choose (fun (name, visible) ->
                Rekolektion.Viz.Core.Layout.Layer.allDrawing
                |> List.tryFind (fun l -> l.Name = name)
                |> Option.map (fun l ->
                    Rekolektion.Viz.App.Model.Msg.Msg.ToggleLayer
                        ((l.Number, l.DataType), visible)))
        let highlightMsgs =
            match parsed.Highlight with
            | Some n ->
                [ Rekolektion.Viz.App.Model.Msg.Msg.SetHighlightedNets
                    (Set.singleton n) ]
            | None   -> []
        let tabMsgs =
            match parsed.Tab with
            | "3D" ->
                [ Rekolektion.Viz.App.Model.Msg.Msg.SetTab
                    Rekolektion.Viz.App.Model.Model.Tab.View3D ]
            | _ -> []
        let preRenderMsgs =
            openMsg :: (toggleMsgs @ highlightMsgs @ tabMsgs)
        Rekolektion.Viz.App.HeadlessRender.renderToPng
            parsed.Output
            parsed.Width
            parsed.Height
            parsed.HoldMs
            preRenderMsgs

/// Headless test probe: boots the App, finds the "Run macro..."
/// button by walking the visual tree, simulates a click on it, and
/// reports what happens. Used to drive UI flows from CI / agents
/// without a real GUI session. Output goes to stderr so it can be
/// piped to a log.
let cmdRunMacroProbe (_args: string list) : int =
    System.Environment.SetEnvironmentVariable("REKOLEKTION_VIZ_HEADLESS", "1")
    use session = Avalonia.Headless.HeadlessUnitTestSession.StartNew(
                        typeof<Rekolektion.Viz.App.HeadlessApp>)
    let task =
        session.Dispatch((fun () ->
            let window = Rekolektion.Viz.App.MainWindow()
            window.Width <- 1400.0
            window.Height <- 900.0
            window.Show()
            // Pump frames so layout completes
            let pump (ms: int64) =
                let sw = System.Diagnostics.Stopwatch.StartNew()
                while sw.ElapsedMilliseconds < ms do
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs()
                    System.Threading.Thread.Sleep 16
            pump 500L
            // Walk the visual tree to find the Run macro button.
            let rec findRunButton (v: Avalonia.Visual) : Avalonia.Controls.Button option =
                match v with
                | :? Avalonia.Controls.Button as b when (b.Content :? string) ->
                    if (b.Content :?> string) = "Run macro..." then Some b
                    else v.GetVisualChildren() |> Seq.tryPick findRunButton
                | _ ->
                    v.GetVisualChildren() |> Seq.tryPick findRunButton
            match findRunButton (window :> Avalonia.Visual) with
            | None ->
                eprintfn "[probe] Run macro button NOT FOUND in visual tree"
            | Some btn ->
                eprintfn "[probe] found button, IsEnabled=%b IsVisible=%b bounds=%A"
                    btn.IsEnabled btn.IsVisible btn.Bounds
                let tl =
                    Avalonia.VisualExtensions.TranslatePoint(
                        btn :> Avalonia.Visual,
                        Avalonia.Point(0.0, 0.0),
                        window :> Avalonia.Visual)
                if tl.HasValue then
                    let p = tl.Value
                    let center = Avalonia.Point(
                                    p.X + btn.Bounds.Width / 2.0,
                                    p.Y + btn.Bounds.Height / 2.0)
                    eprintfn "[probe] clicking at window-coord %A" center
                    Avalonia.Headless.HeadlessWindowExtensions.MouseDown(
                        window, center,
                        Avalonia.Input.MouseButton.Left,
                        Avalonia.Input.RawInputModifiers.None)
                    Avalonia.Headless.HeadlessWindowExtensions.MouseUp(
                        window, center,
                        Avalonia.Input.MouseButton.Left,
                        Avalonia.Input.RawInputModifiers.None)
                else
                    eprintfn "[probe] could not translate button to window coords"
            // Pump for a second so click handler + dialog have a chance
            pump 2000L
            eprintfn "[probe] done"
        ), System.Threading.CancellationToken.None)
    task.GetAwaiter().GetResult()
    0

/// Probe: load a GDS into the App headlessly, simulate left-drag
/// (orbit), right-drag (pan), wheel (zoom), and verify the
/// interactive 3D camera handlers run without throwing. Reports
/// any exceptions to stderr.
let cmdInteractProbe (args: string list) : int =
    let gdsPath =
        args
        |> List.tryFindIndex (fun s -> s = "--gds")
        |> Option.bind (fun i -> args |> List.tryItem (i + 1))
        |> Option.defaultValue "tools/viz/testdata/bitcell_lr.gds"
    System.Environment.SetEnvironmentVariable("REKOLEKTION_VIZ_HEADLESS", "1")
    use session = Avalonia.Headless.HeadlessUnitTestSession.StartNew(
                        typeof<Rekolektion.Viz.App.HeadlessApp>)
    let task =
        session.Dispatch((fun () ->
            let window = Rekolektion.Viz.App.MainWindow()
            window.Width <- 1400.0
            window.Height <- 900.0
            window.Show()
            let pump (ms: int64) =
                let sw = System.Diagnostics.Stopwatch.StartNew()
                while sw.ElapsedMilliseconds < ms do
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs()
                    System.Threading.Thread.Sleep 16
            pump 200L
            // Load the GDS via Msg dispatch.
            Rekolektion.Viz.App.AppDispatch.send (
                Rekolektion.Viz.App.Model.Msg.OpenFile gdsPath)
            pump 500L
            // Switch to 3D so the StackCanvasControl is the active
            // surface. SetTab is dispatched; we then need a tick
            // for the TabControl to swap content.
            Rekolektion.Viz.App.AppDispatch.send (
                Rekolektion.Viz.App.Model.Msg.SetTab Rekolektion.Viz.App.Model.Model.View3D)
            pump 200L
            // Find the 3D canvas and exercise pointer events on it.
            let rec findCanvas (v: Avalonia.Visual)
                    : Rekolektion.Viz.App.Canvas3D.StackCanvasControl.StackCanvasControl option =
                match v with
                | :? Rekolektion.Viz.App.Canvas3D.StackCanvasControl.StackCanvasControl as c ->
                    Some c
                | _ ->
                    v.GetVisualChildren() |> Seq.tryPick findCanvas
            match findCanvas (window :> Avalonia.Visual) with
            | None -> eprintfn "[probe] StackCanvasControl not found"
            | Some canvas ->
                let ctlBounds = canvas.Bounds
                let tl =
                    Avalonia.VisualExtensions.TranslatePoint(
                        canvas :> Avalonia.Visual,
                        Avalonia.Point(0.0, 0.0),
                        window :> Avalonia.Visual)
                if not tl.HasValue then
                    eprintfn "[probe] could not translate canvas to window coords"
                else
                    let p = tl.Value
                    let centerX = p.X + ctlBounds.Width / 2.0
                    let centerY = p.Y + ctlBounds.Height / 2.0
                    let center = Avalonia.Point(centerX, centerY)
                    let offset (dx, dy) = Avalonia.Point(centerX + dx, centerY + dy)
                    eprintfn "[probe] canvas at %A center %A" ctlBounds center
                    // Left-drag: orbit
                    eprintfn "[probe] left-drag orbit"
                    Avalonia.Headless.HeadlessWindowExtensions.MouseDown(
                        window, center, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None)
                    Avalonia.Headless.HeadlessWindowExtensions.MouseMove(
                        window, offset(50.0, 30.0), Avalonia.Input.RawInputModifiers.None)
                    Avalonia.Headless.HeadlessWindowExtensions.MouseUp(
                        window, offset(50.0, 30.0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None)
                    pump 100L
                    // Right-drag: pan
                    eprintfn "[probe] right-drag pan"
                    Avalonia.Headless.HeadlessWindowExtensions.MouseDown(
                        window, center, Avalonia.Input.MouseButton.Right, Avalonia.Input.RawInputModifiers.None)
                    Avalonia.Headless.HeadlessWindowExtensions.MouseMove(
                        window, offset(40.0, -20.0), Avalonia.Input.RawInputModifiers.None)
                    Avalonia.Headless.HeadlessWindowExtensions.MouseUp(
                        window, offset(40.0, -20.0), Avalonia.Input.MouseButton.Right, Avalonia.Input.RawInputModifiers.None)
                    pump 100L
                    // Wheel: zoom
                    eprintfn "[probe] wheel zoom"
                    Avalonia.Headless.HeadlessWindowExtensions.MouseWheel(
                        window, center, Avalonia.Vector(0.0, 1.0), Avalonia.Input.RawInputModifiers.None)
                    pump 100L
            eprintfn "[probe] done"
        ), System.Threading.CancellationToken.None)
    task.GetAwaiter().GetResult()
    0

[<EntryPoint>]
let main argv =
    match argv |> Array.toList with
    | "read" :: rest        -> cmdRead rest
    | "to-gds" :: rest      -> cmdToGds rest
    | "to-lef" :: rest      -> cmdToLef rest
    | "drc" :: rest         -> cmdDrc rest
    | "dump-layer" :: rest  -> cmdDumpLayer rest
    | "render" :: rest      -> cmdRender rest
    | "mesh" :: rest        -> cmdMesh rest
    | "app" :: rest         -> cmdApp rest
    | "viz-render" :: rest  -> cmdVizRender rest
    | "runmacro-probe" :: rest -> cmdRunMacroProbe rest
    | "interact-probe" :: rest -> cmdInteractProbe rest
    | "--help" :: _ | "-h" :: _ | [] -> printUsage(); 0
    | cmd :: _ -> printfn "Unknown command: %s" cmd; printUsage(); 1
