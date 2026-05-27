module Rekolektion.Viz.App.Services.RoutedSave

/// Per-file routed save for `.rkt`-backed macros.
///
/// The App used to feed every Save through the single-file
/// `EditSession.saveTo`. That works for `.gds` / `.mag` (no
/// import graph) but silently wrong for `.rkt` projects whose
/// imports bring in cells from other files: the merged Document
/// gets written back to the root, overwriting the import
/// structure and dropping any edits to imported cells.
///
/// This module routes saves through `Rkt.SaveRouter`:
///
/// 1. **Plan**: project the current merged Document into a Library
///    against the load-time snapshot. Detect mtime conflicts and
///    orphan cells without writing anything.
/// 2. **Execute**: when the planned diffs pass the conflict /
///    orphan gates, write each changed file atomically via
///    `SaveRouter.saveAll`.
///
/// Non-`.rkt` macros (`LibrarySnapshot = None`) fall through to the
/// existing single-file `EditSession.saveTo` path. The
/// `.rkt`-routed path takes over only when the snapshot is present.

open System
open System.IO
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Rkt
open Rekolektion.Viz.App.Model.Model

/// What the plan step found about the impending save.
type SavePlan = {
    /// Path the root document was loaded from (mc.Path).
    RootPath: string
    /// Per-path Document diffs the writer will commit (empty when
    /// nothing changed).
    Diffs: Map<string, Types.Document>
    /// Mtime conflicts — external edits to files since load time.
    /// Non-empty blocks `execute`; the App's conflict dialog asks
    /// the user how to resolve.
    Conflicts: SaveRouter.SaveError list
    /// Cells in the current Document whose name isn't in the
    /// snapshot's CellIndex. Non-empty blocks `execute`; the App's
    /// orphan dialog asks for a target file per orphan.
    Orphans: string list
}

/// Result of a successful `execute` — the App refreshes its
/// `LibraryMtimes` from `NewMtimes` so the next save's conflict
/// check uses the post-write timestamps.
type ExecuteResult = {
    WrittenPaths: string list
    NewMtimes: Map<string, DateTime>
}

/// Compute a `SavePlan` for an open macro. Pure on the in-memory
/// state — reads disk only for the mtime check.
///
/// `orphanAssignments` lets the caller (typically the OrphanDialog
/// in App.fs) route synthesised cells to user-chosen target files
/// instead of the default root. Pass `Map.empty` on the first plan
/// call to surface the orphan list; pass the dialog's selection on
/// the second call so the diff routes correctly.
let plan
    (mc: LoadedMacro)
    (orphanAssignments: Map<string, string>)
    : SavePlan option =
    match mc.LibrarySnapshot with
    | None -> None
    | Some snapshot ->
        let rootKey = Path.GetFullPath mc.Path
        let projected =
            SaveRouter.projectIntoLibrary
                snapshot mc.Document rootKey orphanAssignments
        let diffs = SaveRouter.diffByFile snapshot projected
        let conflicts = SaveRouter.detectMtimeConflicts diffs mc.LibraryMtimes
        let orphans = SaveRouter.orphanCells projected
        Some {
            RootPath = mc.Path
            Diffs = diffs
            Conflicts = conflicts
            Orphans = orphans
        }

/// Apply a planned set of diffs. Caller is responsible for already
/// having resolved conflicts + orphans (i.e., `Conflicts`/`Orphans`
/// must be empty on the plan that informed `diffs`; this function
/// re-checks neither — it just writes).
let execute (diffs: Map<string, Types.Document>) : Result<ExecuteResult, string> =
    match SaveRouter.saveAll diffs with
    | Error (SaveRouter.WriteFailure (path, ex)) ->
        Error (sprintf "write failed for %s: %s" path ex.Message)
    | Error other -> Error (sprintf "%A" other)
    | Ok () ->
        let mtimes =
            diffs
            |> Map.toSeq
            |> Seq.map (fun (p, _) -> p, File.GetLastWriteTimeUtc p)
            |> Map.ofSeq
        let written = diffs |> Map.toSeq |> Seq.map fst |> List.ofSeq
        Ok { WrittenPaths = written; NewMtimes = mtimes }

/// Three-way merge for the ConflictDialog's Reload-and-Reapply path.
///
/// Reloads the macro's root file from disk (re-walking `(import …)`)
/// and re-applies the user's pending in-memory edits on top:
///
/// 1. Diff in-memory `mc.Document.Cells` against the load-time
///    snapshot to surface the cells the user touched.
/// 2. Re-load the root file → new snapshot reflecting on-disk state.
/// 3. Merge: start from the new snapshot's merged cell list; replace
///    cells the user edited with the user's versions.
/// 4. Flag per-cell conflicts: cells the user edited AND that also
///    changed on disk relative to the old snapshot. v1 policy is
///    "user wins" on these; the names come back as a list so the
///    App can surface them in the log / a follow-up prompt.
///
/// Returns the new `LoadedMacro` with refreshed snapshot, mtimes,
/// and merged Document plus the list of conflicting cell names.
type ReapplyResult = {
    Macro: LoadedMacro
    ConflictingCells: string list
}

let reloadAndReapply (mc: LoadedMacro) : Result<ReapplyResult, string> =
    match mc.LibrarySnapshot with
    | None -> Error "no library snapshot — nothing to merge against"
    | Some oldSnapshot ->
        match Rekolektion.Viz.Core.Rkt.Reader.loadSingle mc.Path with
        | Error e -> Error (sprintf "reload failed: %A" e)
        | Ok newSnapshot ->
            // Index snapshot cells by name for fast diff.
            let cellsByName (lib: Rekolektion.Viz.Core.Rkt.Reader.Library) =
                lib.Documents
                |> Map.toSeq
                |> Seq.collect (fun (_, ld) -> ld.Ast.Cells)
                |> Seq.map (fun c -> c.Name, c)
                |> Map.ofSeq
            let oldCells = cellsByName oldSnapshot
            let newCells = cellsByName newSnapshot
            // Cells the user edited: in mc.Document.Cells but
            // differ from the OLD snapshot version (or are new
            // names not in the old snapshot).
            let userEditedCells =
                mc.Document.Cells
                |> List.filter (fun c ->
                    match Map.tryFind c.Name oldCells with
                    | Some old -> c <> old
                    | None -> true)
            // Per-cell conflicts: a user-edited cell whose disk
            // version ALSO changed between old and new snapshots.
            let conflicts =
                userEditedCells
                |> List.choose (fun userCell ->
                    let old = Map.tryFind userCell.Name oldCells
                    let current = Map.tryFind userCell.Name newCells
                    match old, current with
                    | Some o, Some c when o <> c -> Some userCell.Name
                    | _ -> None)
            // Merged cells: start from the new snapshot's union;
            // replace any cell the user edited with the user's
            // version. Order preserves the new snapshot's iteration
            // for non-edited cells; edited cells follow.
            let userEditedNames =
                userEditedCells |> List.map (fun c -> c.Name) |> Set.ofList
            let mergedCells =
                let baseCells =
                    newSnapshot.Documents
                    |> Map.toSeq
                    |> Seq.collect (fun (_, ld) -> ld.Ast.Cells)
                    |> List.ofSeq
                let kept =
                    baseCells
                    |> List.filter (fun c -> not (Set.contains c.Name userEditedNames))
                kept @ userEditedCells
            let newMtimes =
                newSnapshot.Documents
                |> Map.toSeq
                |> Seq.map (fun (p, _) -> p, File.GetLastWriteTimeUtc p)
                |> Map.ofSeq
            let newDoc = { mc.Document with Cells = mergedCells }
            Ok {
                Macro =
                    { mc with
                        Document = newDoc
                        LibrarySnapshot = Some newSnapshot
                        LibraryMtimes = newMtimes
                        // Stay dirty when the user had pending edits.
                        Dirty = not (List.isEmpty userEditedCells) }
                ConflictingCells = conflicts
            }

/// All-in-one: plan + execute when no conflicts/orphans block.
/// This is the path the `Backend.SaveMacro` dispatch uses when the
/// App doesn't have a dialog wired yet. The richer dialog flow
/// (SR4-7) calls `plan` and `execute` separately so it can render
/// dialogs between them.
let saveOrSurfaceBlockers (mc: LoadedMacro) : Result<ExecuteResult, string> =
    match plan mc Map.empty with
    | None ->
        // No library snapshot — fall back to single-file save.
        try
            let written = EditSession.saveTo mc mc.Path
            let mtime = File.GetLastWriteTimeUtc written
            Ok {
                WrittenPaths = [ written ]
                NewMtimes = Map.ofList [ written, mtime ]
            }
        with ex -> Error ex.Message
    | Some p when not (List.isEmpty p.Conflicts) ->
        let names =
            p.Conflicts
            |> List.choose (function
                | SaveRouter.MtimeConflict (path, _, _) -> Some (Path.GetFileName path)
                | _ -> None)
            |> String.concat ", "
        Error (sprintf "external edit detected on: %s" names)
    | Some p when not (List.isEmpty p.Orphans) ->
        Error (sprintf "orphan cells without source path: %s"
                       (String.concat ", " p.Orphans))
    | Some p when Map.isEmpty p.Diffs ->
        // Nothing to save — return an empty result rather than an
        // error so the App can treat it as a no-op.
        Ok { WrittenPaths = []; NewMtimes = Map.empty }
    | Some p -> execute p.Diffs
