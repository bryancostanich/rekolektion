/// Pure parser for `rekolektion viz-render` CLI flags. Produces a
/// `VizRenderArgs` record consumed by `Cli.Program.cmdVizRender`,
/// which then translates the parsed values into pre-render `Msg`s
/// dispatched against the headless Elmish loop in
/// `HeadlessRender.renderToPng` before `CaptureRenderedFrame`.
///
/// Hand-rolled deliberately: keeps the App project free of an Argu
/// dependency and avoids dragging an arg-parser library into the
/// MCP server (Task 29) which will reuse this module.
///
/// No Avalonia references — the file lives in `Rekolektion.Viz.App`
/// only because both `HeadlessRender.fs` and `Cli/Program.fs`
/// consume it; placement is between `Model/Update.fs` and
/// `Services/RekolektionCli.fs` in the fsproj compile order.
module Rekolektion.Viz.App.HeadlessRenderArgs

open System

/// Parsed `viz-render` arguments. `Toggles` is captured in CLI
/// order so a `--toggle-layer met2=off --toggle-layer met2=on`
/// sequence resolves to the final `on` state, matching how the
/// Msg.ToggleLayer reducer in `Visibility.toggleLayer` overwrites
/// per-key state. `Tab` is held as a string (`"2D"` | `"3D"`)
/// rather than the App's `Model.Tab` DU so this module stays
/// dependency-light; the CLI maps it into `Msg.SetTab` itself.
type VizRenderArgs = {
    Gds        : string
    Output     : string
    Toggles    : (string * bool) list
    Highlight  : string option
    Tab        : string
    Width      : int
    Height     : int
    HoldMs     : int
}

/// Parse argv (after the leading `viz-render` token has been
/// stripped). Returns `Error` for: unknown flag, malformed
/// `--toggle-layer` value, non-numeric `--width`/`--height`/
/// `--hold-ms`, non-`{2D,3D}` `--tab`, missing trailing value
/// after a flag, or missing required `--gds`/`--output`.
///
/// Numeric parsing deviates from the plan code by using
/// `Int32.TryParse` instead of `int`, so a bad value surfaces as
/// a structured `Result<_, string>` error rather than a
/// `FormatException` propagating out of the CLI entry point.
let parseVizRenderArgs (args: string list) : Result<VizRenderArgs, string> =
    let tryParseInt (flag: string) (v: string) : Result<int, string> =
        match Int32.TryParse v with
        | true, n -> Ok n
        | false, _ -> Error (sprintf "bad %s value: %s (expected integer)" flag v)

    let rec loop (a: string list) (acc: VizRenderArgs) : Result<VizRenderArgs, string> =
        match a with
        | [] ->
            if String.IsNullOrEmpty acc.Gds || String.IsNullOrEmpty acc.Output then
                Error "missing required --gds or --output"
            else
                Ok acc
        | "--gds" :: v :: rest        -> loop rest { acc with Gds = v }
        | "--output" :: v :: rest     -> loop rest { acc with Output = v }
        | "--toggle-layer" :: v :: rest ->
            match v.Split '=' with
            | [| name; "on"  |] -> loop rest { acc with Toggles = acc.Toggles @ [name, true] }
            | [| name; "off" |] -> loop rest { acc with Toggles = acc.Toggles @ [name, false] }
            | _ -> Error (sprintf "bad --toggle-layer value: %s" v)
        | "--highlight-net" :: v :: rest -> loop rest { acc with Highlight = Some v }
        | "--tab" :: v :: rest ->
            match v with
            | "2D" | "3D" -> loop rest { acc with Tab = v }
            | _ -> Error (sprintf "bad --tab value: %s (expected 2D or 3D)" v)
        | "--width" :: v :: rest ->
            match tryParseInt "--width" v with
            | Ok n -> loop rest { acc with Width = n }
            | Error e -> Error e
        | "--height" :: v :: rest ->
            match tryParseInt "--height" v with
            | Ok n -> loop rest { acc with Height = n }
            | Error e -> Error e
        | "--hold-ms" :: v :: rest ->
            match tryParseInt "--hold-ms" v with
            | Ok n -> loop rest { acc with HoldMs = n }
            | Error e -> Error e
        | flag :: [] when flag.StartsWith "--" ->
            Error (sprintf "missing value for %s" flag)
        | unknown :: _ -> Error (sprintf "unknown arg: %s" unknown)

    let init = {
        Gds = ""
        Output = ""
        Toggles = []
        Highlight = None
        Tab = "2D"
        Width = 1400
        Height = 900
        HoldMs = 500
    }
    loop args init
