module Rekolektion.Viz.Core.Cif

open System
open System.IO
open System.Diagnostics
open Rekolektion.Viz.Core.Rkt.Types

/// `.mag` → silicon-truth `Rkt.Document` translation by spawning
/// Magic itself. Magic owns the CIF rules (`sky130A.tech`,
/// `sky130B.tech`) that turn editing-layer geometry (`viali`,
/// `mvnmos`, …) into actual mask layers (`mcon` arrays, unioned
/// `diff`, generated implants). Reimplementing those rules in F#
/// is months of work and a permanent maintenance burden; bundling
/// Magic as a sidecar binary and shelling out gives us correct
/// silicon truth for free.
///
/// Today this module locates Magic via, in order:
///   1. `REKOLEKTION_MAGIC` env var if set
///   2. `~/.local/bin/magic` (the rekolektion / khalkulo build location)
///   3. `magic` on PATH
///
/// Future: vendor a pinned Magic binary inside the rekolektion repo
/// (or a Nix / brew formula) so users don't have to install it.
/// The module signature stays the same — only the discovery
/// changes.
///
/// The Tcl script we generate:
///   - cd's into the file's directory (Magic's `load` resolves
///     subcells relative to its current working directory, plus
///     anything added via `addpath`).
///   - `addpath` for each extra search dir, so cross-directory
///     subcell references in the rekolektion / khalkulo
///     `cell_designs/<group>/layout/` ↔ `cell_designs/primitives/`
///     layout resolve correctly.
///   - `load <basename>` parses the .mag (recursively).
///   - `gds write <tmp.gds>` runs CIF translation and writes the
///     silicon-truth GDS.
///   - `quit -noprompt`.
///
/// On the F# side we read the temp GDS via the existing
/// `Gds.Reader.readGds`, which already routes through
/// `Rkt.OfGds.fromLibrary` to land as `Rkt.Document`. The temp
/// file is deleted on success and on failure.
///
/// Information that round-trips through Magic → GDS → Rkt:
///   - Silicon-truth geometry (everything that lands on a mask layer).
///   - Cell hierarchy (SRef / ARef).
/// Information that does NOT survive (Magic-side only, not in GDS):
///   - `flagstring` port flags on `rlabel` directives.
///   - Magic's `<< properties >>` block.
///   - Magic-only marker layers (`checkpaint`, `error`, `feedback`).
/// Port flags can be added back through the .rkt-side editor in
/// later passes if needed.

/// Locate the Magic binary. Returns `Some path` if findable, `None`
/// if neither env var, common install location, nor PATH yielded
/// a usable binary.
let private locateMagic () : string option =
    let envOverride =
        Environment.GetEnvironmentVariable "REKOLEKTION_MAGIC"
        |> Option.ofObj
        |> Option.filter (fun s -> s.Length > 0 && File.Exists s)
    let homeBuild =
        let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
        let candidate = Path.Combine(home, ".local", "bin", "magic")
        if File.Exists candidate then Some candidate else None
    let onPath =
        try
            let psi =
                ProcessStartInfo(
                    FileName = "/usr/bin/which",
                    Arguments = "magic",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true)
            use proc = Process.Start psi
            let stdout = proc.StandardOutput.ReadToEnd().Trim()
            proc.WaitForExit()
            if proc.ExitCode = 0 && stdout.Length > 0 && File.Exists stdout
            then Some stdout
            else None
        with _ -> None
    envOverride
    |> Option.orElse homeBuild
    |> Option.orElse onPath

/// Parse the `tech <name>` line in a `.mag` to know which sky130
/// flavor to point Magic at. Defaults to `sky130A` when the file
/// doesn't declare one — the modern PDK shuttles use sky130B but
/// sky130A is the upstream Magic install's default.
let private techFromMag (magPath: string) : string =
    try
        seq {
            use stream =
                new FileStream(
                    magPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite ||| FileShare.Delete)
            use reader = new StreamReader(stream)
            let mutable line = reader.ReadLine()
            while not (isNull line) do
                yield line
                line <- reader.ReadLine()
        }
        |> Seq.tryPick (fun line ->
            let trimmed = line.Trim()
            if trimmed.StartsWith "tech " then
                Some (trimmed.Substring(5).Trim())
            else None)
        |> Option.defaultValue "sky130A"
    with _ -> "sky130A"

/// Path to the magicrc for a tech. Honors `PDK_ROOT` if set; falls
/// back to `~/.volare` (the rekolektion / khalkulo convention).
let private magicrcFor (tech: string) : string option =
    let root =
        match Environment.GetEnvironmentVariable "PDK_ROOT" with
        | null | "" ->
            Path.Combine(
                Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
                ".volare")
        | r -> r
    let candidate =
        Path.Combine(root, tech, "libs.tech", "magic", sprintf "%s.magicrc" tech)
    if File.Exists candidate then Some candidate else None

/// Build the Tcl script Magic runs. The script is intentionally
/// terse: any Tcl error aborts and the parent surfaces the stderr.
let private buildTclScript
        (magPath: string)
        (outGds: string)
        (searchDirs: string list)
        : string =
    let baseName = Path.GetFileNameWithoutExtension magPath
    let cellDir = Path.GetDirectoryName(Path.GetFullPath magPath)
    let escape (s: string) = s.Replace("\\", "\\\\").Replace("\"", "\\\"")
    let addPaths =
        searchDirs
        |> List.filter Directory.Exists
        |> List.map (fun d -> sprintf "addpath \"%s\"" (escape d))
        |> String.concat "\n"
    sprintf
        "drc off\n\
         cd \"%s\"\n\
         %s\n\
         load \"%s\"\n\
         gds write \"%s\"\n\
         quit -noprompt\n"
        (escape cellDir)
        addPaths
        (escape baseName)
        (escape outGds)

/// Result of a Magic CIF run.
type private RunResult = {
    GdsPath  : string
    Stderr   : string
    ExitCode : int
}

let private runMagic
        (magicBin: string)
        (rcfile: string option)
        (script: string)
        (outGds: string)
        : RunResult =
    let args =
        let baseArgs = [ "-dnull"; "-noconsole" ]
        match rcfile with
        | Some p -> baseArgs @ [ "-rcfile"; p ]
        | None -> baseArgs
    let psi =
        ProcessStartInfo(
            FileName = magicBin,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true)
    for a in args do
        psi.ArgumentList.Add a
    use proc = Process.Start psi
    proc.StandardInput.Write script
    proc.StandardInput.Close()
    let stderr = proc.StandardError.ReadToEnd()
    let _stdout = proc.StandardOutput.ReadToEnd()
    proc.WaitForExit()
    {
        GdsPath = outGds
        Stderr = stderr
        ExitCode = proc.ExitCode
    }

/// Errors that callers (typically `Layout.LayoutLoader.load`) want
/// to distinguish so they can fall back to the shallow-rename
/// `MagToLayout.load` path when Magic isn't reachable.
type CifError =
    | MagicNotFound
    | TechFileNotFound of tech: string
    | MagicFailed of stderr: string * exitCode: int
    | GdsParseFailed of message: string

let private describeError (err: CifError) : string =
    match err with
    | MagicNotFound ->
        "Magic binary not found (checked $REKOLEKTION_MAGIC, ~/.local/bin/magic, PATH)"
    | TechFileNotFound tech ->
        sprintf "tech file '%s.magicrc' not found under $PDK_ROOT" tech
    | MagicFailed (stderr, code) ->
        sprintf "Magic exited %d: %s" code (stderr.Trim())
    | GdsParseFailed msg ->
        sprintf "GDS parse after Magic CIF failed: %s" msg

/// Translate a `.mag` file (with optional extra search dirs for
/// subcell resolution) into a silicon-truth `Rkt.Document` by
/// running Magic. Returns `Error` when Magic is missing or fails;
/// callers can fall back to a shallow-rename load if the silicon
/// view isn't required.
let magToRkt
        (magPath: string)
        (extraSearchDirs: string list)
        : Result<Document * string list, CifError> =
    match locateMagic () with
    | None -> Error MagicNotFound
    | Some magicBin ->
        let tech = techFromMag magPath
        let rcfile = magicrcFor tech
        if rcfile.IsNone then
            Error (TechFileNotFound tech)
        else
            let outGds =
                Path.Combine(
                    Path.GetTempPath(),
                    sprintf "cif-%s.gds" (Guid.NewGuid().ToString "N"))
            let script = buildTclScript magPath outGds extraSearchDirs
            try
                let run = runMagic magicBin rcfile script outGds
                if run.ExitCode <> 0 || not (File.Exists outGds) then
                    Error (MagicFailed (run.Stderr, run.ExitCode))
                else
                    try
                        let doc = Rekolektion.Viz.Core.Gds.Reader.readGds outGds
                        let warnings =
                            if run.Stderr.Length > 0 then
                                run.Stderr.Split '\n'
                                |> Array.map (fun s -> s.Trim())
                                |> Array.filter (fun s ->
                                    s.Length > 0
                                    && not (s.StartsWith "Warning: no metadata"))
                                |> Array.toList
                            else []
                        Ok (doc, warnings)
                    with ex ->
                        Error (GdsParseFailed ex.Message)
            finally
                try if File.Exists outGds then File.Delete outGds with _ -> ()

/// Convenience: same as `magToRkt` but flattens `Result` to either
/// the document + warnings (with the error message folded into
/// warnings) or throws on a hard failure. Used by code paths that
/// don't have a meaningful fallback.
let magToRktOrFail
        (magPath: string)
        (extraSearchDirs: string list)
        : Document * string list =
    match magToRkt magPath extraSearchDirs with
    | Ok (doc, warnings) -> doc, warnings
    | Error err -> failwith (describeError err)
