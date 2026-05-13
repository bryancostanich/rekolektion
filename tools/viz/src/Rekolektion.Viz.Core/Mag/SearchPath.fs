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

/// Env-var override. `REKOLEKTION_MAG_PATH` is colon-separated on
/// Unix (and Windows-style `;`-separated as a fallback). Empty
/// list when the var is unset or empty.
let fromEnv () : string list =
    let raw = System.Environment.GetEnvironmentVariable "REKOLEKTION_MAG_PATH"
    if System.String.IsNullOrEmpty raw then []
    else
        let sep =
            if raw.Contains ':' then [| ':' |]
            elif raw.Contains ';' then [| ';' |]
            else [| ':' |]
        raw.Split(sep, System.StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList
        |> List.map (fun s -> s.Trim())
        |> List.filter (fun s -> s <> "")

/// Project-aware search-path guess for the rekolektion / khalkulo
/// repo layout. Walks up from `loadedFile` looking for an ancestor
/// directory named `cell_designs`. If found, adds:
///   - `<cell_designs>/<each-child>/`
///   - `<cell_designs>/<each-child>/layout/`
///
/// The rekolektion convention puts primitive cells flat under
/// `cell_designs/primitives/` and per-cell projects under
/// `cell_designs/<group>/layout/`. Walking those at load time
/// resolves cross-directory `use` references without the user
/// having to set `REKOLEKTION_MAG_PATH` for files inside the
/// project tree.
///
/// Returns an empty list when no `cell_designs` ancestor exists —
/// files outside the project layout still only get the
/// caller-supplied search path.
let inferProjectPaths (loadedFile: string) : string list =
    let fullPath =
        try Path.GetFullPath loadedFile with _ -> loadedFile
    let rec findCellDesigns (dir: string) : string option =
        if System.String.IsNullOrEmpty dir then None
        else
            let here = Path.Combine(dir, "cell_designs")
            if Directory.Exists here then Some here
            else
                let parent = Path.GetDirectoryName dir
                if parent = dir || System.String.IsNullOrEmpty parent then None
                else findCellDesigns parent
    let startDir =
        try Path.GetDirectoryName fullPath with _ -> ""
    match findCellDesigns startDir with
    | None -> []
    | Some root ->
        try
            Directory.EnumerateDirectories root
            |> Seq.collect (fun d ->
                let layout = Path.Combine(d, "layout")
                if Directory.Exists layout then [ d; layout ] else [ d ])
            |> Seq.toList
        with _ -> []
