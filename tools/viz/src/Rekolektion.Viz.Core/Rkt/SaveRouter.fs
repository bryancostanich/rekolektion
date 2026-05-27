module Rekolektion.Viz.Core.Rkt.SaveRouter

/// Per-file save routing for multi-file `.rkt` projects.
///
/// When the viz App loads a `.rkt` whose `(import …)` graph spans
/// multiple files, edits to a cell defined in an imported file
/// must save *into that file*, not into the root document. The
/// Reader already tracks the source path for every cell
/// (`Library.CellIndex : Map<cellName, path>`); this module is the
/// "actually save it" half — compute per-file diffs and emit one
/// canonical text per changed file.
///
/// See `docs/plans/rkt_per_file_save_routing.md` for the full plan.

open System
open System.IO
open Rekolektion.Viz.Core.Rkt.Types

// ─── Errors ─────────────────────────────────────────────────────────────

type SaveError =
    /// File modified on disk since the App loaded it. App resolves
    /// by prompting Reload-and-Reapply / Overwrite / Cancel.
    | MtimeConflict of path: string * onDiskMtime: DateTime * loadedMtime: DateTime
    /// I/O failure during the atomic-rename phase.
    | WriteFailure of path: string * inner: exn
    /// A cell exists in the in-memory library but has no source-path
    /// mapping (synthesized in-App, never written). App prompts for
    /// a target file before save can complete.
    | OrphanCell of cellName: string

// ─── Diff ───────────────────────────────────────────────────────────────

/// Compute per-file diffs from the original (last-known-on-disk)
/// library vs the current (post-edit) library. Returns one Document
/// per file whose contents changed.
///
/// **Definition of "changed"**: AST inequality of the file's
/// Document. Comment-only edits, formatting-only edits — both
/// count as changes because canonical formatting flows through
/// every save.
/// Project the in-memory merged Document back into per-file
/// Documents using `original`'s CellIndex to route each cell to its
/// source file.
///
/// Inputs:
///   - `original`: the Library as last loaded from disk. Carries the
///     authoritative cellName → sourcePath mapping for cells that
///     existed at load time.
///   - `currentMerged`: the in-memory Document the App edits. Its
///     `Cells` list holds the union of every loaded file's cells
///     plus any cells the user synthesized in-App.
///   - `rootPath`: the path the root document was loaded from. New
///     (orphan) cells without a CellIndex entry AND no entry in
///     `orphanAssignments` land here so the save flow doesn't
///     silently drop them.
///   - `orphanAssignments`: optional `cellName → targetPath` map
///     used to place orphan cells (those not in
///     `original.CellIndex`) into user-chosen files. Cells listed
///     here override the default `rootPath` fallback; cells whose
///     `targetPath` doesn't exist in the library are dropped to
///     `rootPath` instead of silently creating a new file. Pass
///     `Map.empty` (or omit) for default-to-root behaviour.
///
/// Returns a new Library whose Documents map each carries only its
/// own cells, in the order they appeared in `currentMerged.Cells`.
/// Document-level fields (Pdk, Units, Imports, TopCell,
/// HeaderComments) are taken from the original per-file Document so
/// import statements and headers survive. Files in `original` whose
/// cells were all deleted in `currentMerged` still appear in the
/// output with an empty Cells list — the App can then decide
/// whether to write an empty file or skip it.
let projectIntoLibrary
    (original: Reader.Library)
    (currentMerged: Document)
    (rootPath: string)
    (orphanAssignments: Map<string, string>)
    : Reader.Library =
    // Group cells by destination path.
    let perPath = System.Collections.Generic.Dictionary<string, Cell list>()
    for path in original.Documents |> Map.toSeq |> Seq.map fst do
        perPath.[path] <- []
    if not (perPath.ContainsKey rootPath) then
        perPath.[rootPath] <- []
    for c in currentMerged.Cells do
        let dest =
            match Map.tryFind c.Name original.CellIndex with
            | Some p when perPath.ContainsKey p -> p
            | _ ->
                // Orphan — consult the assignment override first;
                // fall back to rootPath if the chosen target file
                // isn't part of the loaded library (we don't
                // synthesise new files here).
                match Map.tryFind c.Name orphanAssignments with
                | Some p when perPath.ContainsKey p -> p
                | _ -> rootPath
        perPath.[dest] <- c :: perPath.[dest]
    // Reverse each list so cells appear in the same order as
    // `currentMerged.Cells` (we prepended).
    let perPathInOrder =
        perPath
        |> Seq.map (fun kv -> kv.Key, List.rev kv.Value)
        |> Map.ofSeq
    // Build the new Documents map.
    let newDocs =
        perPathInOrder
        |> Map.toSeq
        |> Seq.map (fun (path, cells) ->
            let prev =
                match Map.tryFind path original.Documents with
                | Some ld -> ld.Ast
                | None ->
                    // New root path that wasn't in the original
                    // library (rare: synthesized in-App).
                    { emptyDocument with
                        Pdk = currentMerged.Pdk
                        Units = currentMerged.Units }
            let newAst = { prev with Cells = cells }
            let loaded : Reader.LoadedDocument = {
                Path = path
                Cst =
                    match Map.tryFind path original.Documents with
                    | Some ld -> ld.Cst
                    | None ->
                        // Synthesized file — Writer.write produces
                        // fresh formatting from the AST anyway, so a
                        // placeholder CST is fine here.
                        ({ Roots = []
                           Trailing = ""
                           SourcePath = Some path } : Cst.Document)
                Ast = newAst
            }
            path, loaded)
        |> Map.ofSeq
    // CellIndex: rebuild from the projected per-file cell lists so
    // it reflects the new assignment (handles renamed/moved cells).
    let newIndex =
        perPathInOrder
        |> Map.toSeq
        |> Seq.collect (fun (path, cells) ->
            cells |> List.map (fun c -> c.Name, path))
        |> Map.ofSeq
    { original with Documents = newDocs; CellIndex = newIndex }

let diffByFile
    (original: Reader.Library)
    (current: Reader.Library)
    : Map<string, Document> =
    let mutable out = Map.empty
    for KeyValue (path, currentDoc) in current.Documents do
        let changed =
            match Map.tryFind path original.Documents with
            | None -> true                         // new file
            | Some prev -> prev.Ast <> currentDoc.Ast
        if changed then
            out <- Map.add path currentDoc.Ast out
    out

// ─── Atomic multi-file write ────────────────────────────────────────────

/// Write the contents of `path` via a sibling `.tmp` file + rename.
/// Returns the temp-file path on success so the caller can roll back
/// if any sibling write fails.
let internal writeTmp (path: string) (text: string) : Result<string, exn> =
    try
        let tmp = path + ".tmp"
        File.WriteAllText(tmp, text)
        Ok tmp
    with ex -> Error ex

/// Roll back a partially-completed save: delete every temp file
/// that's been written but not yet renamed.
let internal rollbackTmps (tmps: string list) : unit =
    for t in tmps do
        try File.Delete t with _ -> ()

/// Write every diff atomically: tmp-sibling per path, then rename
/// every tmp in sequence. Failure during the write phase rolls back
/// all temps; failure during the rename phase leaves whatever
/// renames have already completed in place and reports the bad
/// path (LEF / OS-level rename failures are rare on the same
/// filesystem, but we don't pretend they can't happen).
let saveAll
    (diffs: Map<string, Document>)
    : Result<unit, SaveError> =
    // Phase 1: write temps.
    let mutable tmps : (string * string) list = []  // (tmp, finalPath)
    let mutable err : SaveError option = None
    for KeyValue (finalPath, doc) in diffs do
        if err.IsNone then
            let text = Writer.write doc
            match writeTmp finalPath text with
            | Ok tmp -> tmps <- (tmp, finalPath) :: tmps
            | Error ex -> err <- Some (WriteFailure (finalPath, ex))
    match err with
    | Some e ->
        rollbackTmps (tmps |> List.map fst)
        Error e
    | None ->
        // Phase 2: rename. If any rename fails, abort but leave
        // already-committed renames in place (rolling those back
        // would require restoring the original contents we don't
        // have in hand).
        let rec renameAll = function
            | [] -> Ok ()
            | (tmp, finalPath) :: rest ->
                try
                    if File.Exists finalPath then File.Delete finalPath
                    File.Move(tmp, finalPath)
                    renameAll rest
                with ex -> Error (WriteFailure (finalPath, ex))
        renameAll tmps

// ─── Orphan-cell detection ──────────────────────────────────────────────

/// Walk the current library and surface any cell whose `Name` isn't
/// in `CellIndex` — those are synthesized-in-App cells that don't
/// yet have a source path. Caller prompts user for a target file
/// before save can complete.
let orphanCells (library: Reader.Library) : string list =
    let indexed = library.CellIndex |> Map.toSeq |> Seq.map fst |> Set.ofSeq
    library.Documents
    |> Map.toSeq
    |> Seq.collect (fun (_, ld) -> ld.Ast.Cells)
    |> Seq.choose (fun (c: Cell) ->
        if Set.contains c.Name indexed then None else Some c.Name)
    |> List.ofSeq

// ─── Mtime conflict detection ───────────────────────────────────────────

/// For each file in the diff, compare the on-disk mtime against the
/// caller-supplied "loaded at" timestamp. Returns the list of paths
/// where the on-disk version is newer (i.e., changed externally
/// since load). App raises a conflict prompt for each.
let detectMtimeConflicts
    (diffs: Map<string, Document>)
    (loadedMtimes: Map<string, DateTime>)
    : SaveError list =
    diffs
    |> Map.toSeq
    |> Seq.choose (fun (path, _) ->
        if not (File.Exists path) then None
        else
            let onDisk = File.GetLastWriteTimeUtc path
            match Map.tryFind path loadedMtimes with
            | None -> None
            | Some loaded when onDisk > loaded ->
                Some (MtimeConflict (path, onDisk, loaded))
            | _ -> None)
    |> List.ofSeq
