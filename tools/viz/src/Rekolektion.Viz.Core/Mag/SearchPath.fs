module Rekolektion.Viz.Core.Mag.SearchPath

open System.IO

/// Given the file we're loading + an optional list of extra search
/// directories, return an ordered list of folders the parser should
/// walk when resolving a `use cellname` reference. Mirrors Magic's
/// `path` / `addpath` convention: the file's own directory comes
/// first, then any user-supplied dirs.
///
/// Suggested defaults the caller can include:
///   - $PDK_ROOT/sky130A/libs.ref/sky130_fd_pr_reram/mag/
///   - $PDK_ROOT/sky130A/libs.ref/sky130_fd_sc_hd/mag/
///   - $PDK_ROOT/sky130A/libs.ref/sky130_fd_io/mag/
let buildPath (loadedFile: string) (extras: string list) : string list =
    let here =
        try Path.GetDirectoryName(Path.GetFullPath loadedFile)
        with _ -> ""
    let cleaned =
        extras
        |> List.map (fun d ->
            try Path.GetFullPath d with _ -> d)
        |> List.filter (fun d -> d <> "")
    let all = here :: cleaned
    all
    |> List.distinct
    |> List.filter (fun d -> Directory.Exists d)

/// Locate `<cellname>.mag` inside the search path. Returns the
/// first match, or None if no path resolves. Magic itself stops
/// at the first hit, even if multiple PDK collections expose the
/// same cell name.
let resolve (cellName: string) (searchPath: string list) : string option =
    let fname = sprintf "%s.mag" cellName
    searchPath
    |> List.tryPick (fun dir ->
        let candidate = Path.Combine(dir, fname)
        if File.Exists candidate then Some candidate else None)
