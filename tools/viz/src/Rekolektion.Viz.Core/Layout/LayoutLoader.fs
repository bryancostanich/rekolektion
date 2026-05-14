module Rekolektion.Viz.Core.Layout.LayoutLoader

open System.IO
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Rkt.Types

/// Format-agnostic loader returning the canonical `Rkt.Document`
/// model. Switches on file extension:
///   .gds / .gds2 → Gds.Reader.readGds (already Rkt-flavored)
///   .mag         → Cif.magToRkt (silicon-truth via Magic CIF)
///                  with MagToLayout.load as fallback when Magic
///                  isn't reachable.
///
/// Returns the document plus any warnings (CIF stderr, Mag's
/// unknown-layer log, missing-subcell notices). GDS produces an
/// empty warning list.
///
/// Legacy ReRAM layer numbers (6/0, 8/0, 40/0, …) resolve inside
/// `Layout.Layer.bySky130Number` via the alias table — no separate
/// normalization pass is needed.
let load (path: string) : Document * string list =
    let ext =
        try (Path.GetExtension path).ToLowerInvariant()
        with _ -> ""
    match ext with
    | ".gds" | ".gds2" ->
        Gds.Reader.readGds path, []
    | ".mag" ->
        // Magic `use` references resolve via a search path. Default
        // build:
        //   1. `$REKOLEKTION_MAG_PATH` (colon-separated dirs).
        //   2. Project-aware: walk up to a `cell_designs/` ancestor
        //      and add every immediate child + each child's `layout/`
        //      subdir. Matches the rekolektion / khalkulo convention.
        // Order is "env first" so user override beats heuristic.
        let extras =
            Mag.SearchPath.fromEnv ()
            @ Mag.SearchPath.inferProjectPaths path
            |> List.distinct
        // Silicon-truth path: spawn Magic, let it run the PDK's CIF
        // rules, read the resulting GDS. Falls back to the
        // shallow-rename `MagToLayout.load` view (editing-layer
        // geometry, not silicon-truth) when Magic is unreachable,
        // with a prominent warning so the user knows what they're
        // looking at.
        match Cif.magToRkt path extras with
        | Ok (doc, cifWarnings) -> doc, cifWarnings
        | Error err ->
            let doc, magWarnings = MagToLayout.load path extras
            let reason =
                match err with
                | Cif.MagicNotFound ->
                    "Magic binary not found ($REKOLEKTION_MAGIC, ~/.local/bin/magic, PATH)"
                | Cif.TechFileNotFound tech ->
                    sprintf "tech file '%s.magicrc' missing under $PDK_ROOT" tech
                | Cif.MagicFailed (stderr, code) ->
                    sprintf "Magic exited %d: %s" code (stderr.Trim())
                | Cif.GdsParseFailed msg ->
                    sprintf "GDS parse after Magic CIF failed: %s" msg
            let banner =
                sprintf "rendering Magic editing-layer view (NOT silicon-truth) — CIF unavailable: %s"
                    reason
            doc, banner :: magWarnings
    | ".rkt" ->
        // Multi-file load with `(import ...)` resolution. The reader
        // walks the import graph (cycle-detected), and we merge every
        // loaded file's cells into the root document so SRef lookups
        // by name find imported cells. Per-cell source path is
        // discarded at this layer — Save still routes per-file once
        // the App tracks each cell's origin (future stage).
        match Rkt.Reader.loadSingle path with
        | Error e ->
            failwithf "rkt load failed: %s%s"
                (match e.Path with Some p -> p + ": " | None -> "")
                e.Message
        | Ok library ->
            let rootPath = System.IO.Path.GetFullPath path
            let rootDoc =
                match Map.tryFind rootPath library.Documents with
                | Some ld -> ld.Ast
                | None -> Rkt.Types.emptyDocument
            // Merge cells from every loaded document, preferring the
            // first occurrence of any duplicate name. Stable across
            // import graph shapes because Map iteration is ordered
            // by key (path).
            let seen = System.Collections.Generic.HashSet<string>()
            let mergedCells =
                let acc = System.Collections.Generic.List<Cell>()
                // Root first so root-defined cells take precedence
                // when an imported file shadows a name.
                for c in rootDoc.Cells do
                    if seen.Add c.Name then acc.Add c
                for kv in library.Documents do
                    if kv.Key <> rootPath then
                        for c in kv.Value.Ast.Cells do
                            if seen.Add c.Name then acc.Add c
                List.ofSeq acc
            let warnings =
                let duplicates =
                    library.Documents
                    |> Seq.collect (fun kv -> kv.Value.Ast.Cells)
                    |> Seq.countBy (fun c -> c.Name)
                    |> Seq.filter (fun (_, n) -> n > 1)
                    |> Seq.toList
                if List.isEmpty duplicates then []
                else
                    duplicates
                    |> List.map (fun (name, n) ->
                        sprintf "cell '%s' defined %d times across imported files; first occurrence wins" name n)
            { rootDoc with Cells = mergedCells }, warnings
    | _ ->
        // Be forgiving: try the GDS reader first (handles a few
        // legacy extensions like .stream), surface a clearer error
        // if it fails outright.
        try Gds.Reader.readGds path, []
        with ex ->
            failwithf "Unsupported layout extension '%s' for file %s (%s)"
                ext path ex.Message

/// Transitional shim — returns the same payload converted back into
/// a `Gds.Types.Library` plus warnings. Kept for callers that still
/// operate on the legacy model. Each call site of this function is a
/// migration candidate; once a consumer takes `Rkt.Document`
/// directly, its call site moves to `load`.
let loadAsLibrary (path: string) : Rekolektion.Viz.Core.Gds.Types.Library * string list =
    let doc, warnings = load path
    Rkt.ToGds.toLibrary doc, warnings
