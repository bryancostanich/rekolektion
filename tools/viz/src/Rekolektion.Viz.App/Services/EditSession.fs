module Rekolektion.Viz.App.Services.EditSession

open System.IO
open Rekolektion.Viz.Core
open Rekolektion.Viz.App.Model.Model

/// First-form `_edited` path for a given source — no collision
/// check, just `<base>_edited<ext>`. Used by the undo path-revert
/// to recognise an auto-suggested name vs. a user-typed one
/// without touching the filesystem.
let suggestEditedPathFor (originalPath: string) : string =
    let dir = Path.GetDirectoryName originalPath
    let stem = Path.GetFileNameWithoutExtension originalPath
    let ext = Path.GetExtension originalPath
    Path.Combine(dir, sprintf "%s_edited%s" stem ext)

/// Does `candidate` look like an auto-suggested edited-copy path
/// for `originalPath`? Matches `<stem>_edited<ext>` AND the
/// collision-suffixed variants `<stem>_edited_<N><ext>` that
/// `suggestEditedPath` produces when the bare `_edited` name is
/// already taken on disk. Used by the undo path-revert so the
/// tab name snaps back to the original even when the auto-name
/// landed at `_edited_2` etc. User-typed paths (anything that
/// doesn't fit the pattern) are NOT auto-suggested and stay.
let isAutoSuggestedEditedPath (originalPath: string) (candidate: string) : bool =
    let dir = Path.GetDirectoryName originalPath
    let stem = Path.GetFileNameWithoutExtension originalPath
    let ext = Path.GetExtension originalPath
    let cDir = Path.GetDirectoryName candidate
    let cName = Path.GetFileNameWithoutExtension candidate
    let cExt = Path.GetExtension candidate
    if cDir <> dir || cExt <> ext then false
    elif cName = sprintf "%s_edited" stem then true
    else
        let prefix = sprintf "%s_edited_" stem
        if not (cName.StartsWith prefix) then false
        else
            let suffix = cName.Substring prefix.Length
            match System.Int32.TryParse suffix with
            | true, _ -> true
            | _ -> false

/// Compute the `_edited.mag` path used on first edit. If
/// `<base>_edited.mag` already exists, append `_2`, `_3`, … until
/// we land on an unused name. Lives next to the original so the
/// edit copy can resolve subcell references through the same
/// search path as the source.
let suggestEditedPath (originalPath: string) : string =
    let dir = Path.GetDirectoryName originalPath
    let stem = Path.GetFileNameWithoutExtension originalPath
    let ext = Path.GetExtension originalPath
    let candidate n =
        if n = 1 then
            Path.Combine(dir, sprintf "%s_edited%s" stem ext)
        else
            Path.Combine(dir, sprintf "%s_edited_%d%s" stem n ext)
    let mutable n = 1
    while File.Exists (candidate n) do
        n <- n + 1
    candidate n

/// Persist `mc.Document` to disk. The save target's extension
/// determines the writer; the model's canonical `Rkt.Document`
/// converts back to legacy `Gds.Library` only at the boundary for
/// the GDS / Mag writers that still consume it.
///
/// **Read source resolution:** for the Mag writer, prefer `mc.Path`
/// if it exists on disk (the user may have saved before, or be
/// overwriting an existing edited copy); otherwise fall back to
/// `mc.OriginalPath`. The fallback is critical after the user
/// renames an unsaved edit — `mc.Path` will be the new name they
/// typed, but no file with that name exists yet, and the only
/// round-trip-safe source is the original file.
let private extOf (path: string) : string =
    (Path.GetExtension path).ToLowerInvariant()

let saveTo (mc: LoadedMacro) (targetPath: string) : string =
    let srcExt = extOf mc.OriginalPath
    let dstExt = extOf targetPath
    // Cross-format save between .gds and .mag isn't supported — the
    // two writers consume different intermediate states. `.rkt` is
    // the universal export target — the in-memory `Document` is
    // already in the canonical model.
    if srcExt <> dstExt && dstExt <> ".rkt" then
        failwithf
            "Save format mismatch: source %s → target %s. The viz \
             editor writes each format back in place; cross-format \
             export to anything other than .rkt isn't supported."
            srcExt dstExt
    match dstExt with
    | ".gds" | ".gds2" ->
        // Convert to the legacy Library shape for the GDS encoder.
        // Geometry round-trips losslessly; comments / nets / port
        // metadata stay in the `.rkt`-side Document.
        let lib = Rkt.ToGds.toLibrary mc.Document
        Gds.Writer.writeGds targetPath lib
    | ".rkt" ->
        // Canonical save: emit the in-memory Document directly,
        // BUT first rewrite each `(import …)` path so it still
        // resolves from the new save location. Imports were stored
        // verbatim from the source file and are typically relative
        // (e.g. `../primitives/foo.rkt`) — saving to a different
        // directory (Save As to /tmp/, etc.) would point them at
        // bogus paths under the target's parent (`/tmp/../primitives`
        // = `/primitives`), breaking the next load. We resolve each
        // relative path against the ORIGINAL file's dir to get its
        // absolute location, then re-express it relative to the
        // target dir.
        let docToWrite =
            let srcDir =
                let raw = Path.GetDirectoryName mc.OriginalPath
                if System.String.IsNullOrEmpty raw then "." else raw
            let tgtDir =
                let raw = Path.GetDirectoryName targetPath
                if System.String.IsNullOrEmpty raw then "." else raw
            let srcFull = Path.GetFullPath srcDir
            let tgtFull = Path.GetFullPath tgtDir
            if srcFull = tgtFull then mc.Document
            else
                let imports' =
                    mc.Document.Imports
                    |> List.map (fun imp ->
                        if Path.IsPathRooted imp.Path then imp
                        else
                            let absRef =
                                Path.GetFullPath(Path.Combine(srcFull, imp.Path))
                            let rel = Path.GetRelativePath(tgtFull, absRef)
                            // Path.GetRelativePath uses the platform
                            // separator on Windows; the .rkt format
                            // is forward-slash everywhere.
                            let normalised = rel.Replace('\\', '/')
                            { imp with Path = normalised })
                { mc.Document with Imports = imports' }
        let text = Rkt.Writer.write docToWrite
        File.WriteAllText(targetPath, text)
    | _ ->
        // Magic writer reads the source file for line-level
        // round-trip preservation and only rewrites the `transform`
        // lines for each top-level instance. It still takes a
        // Library; we materialise one at the boundary.
        let lib = Rkt.ToGds.toLibrary mc.Document
        let readPath =
            if File.Exists mc.Path then mc.Path
            else mc.OriginalPath
        Mag.Writer.writeUpdated readPath lib targetPath
    targetPath

/// Mark a macro as dirty (called by every editing transition in
/// Update.fs). On the first edit of a clean macro this also
/// retargets the path from `foo.mag` to `foo_edited.mag` so
/// subsequent saves don't write over the source — the user
/// explicitly opts back in via Save As if they want that.
let markDirty (mc: LoadedMacro) : LoadedMacro =
    if mc.Dirty then mc
    elif mc.Path = mc.OriginalPath then
        let edited = suggestEditedPath mc.OriginalPath
        { mc with Path = edited; Dirty = true }
    else
        { mc with Dirty = true }

/// Maximum undo history per macro. Bounded so a long editing
/// session doesn't grow the heap without limit. 200 is well past
/// what feels useful interactively but small enough that even at
/// production-macro Document sizes the total stays under ~20 MB.
let undoLimit = 200

/// Push the current `Document` onto `mc.UndoStack` so a future Undo
/// can restore it. Trims to `undoLimit` from the end. Used by
/// Update.fs *before* applying any edit. Clears `RedoStack` —
/// any new edit invalidates the redo history (standard undo/redo).
let pushUndoSnapshot (mc: LoadedMacro) : LoadedMacro =
    let stack = mc.Document :: mc.UndoStack
    let trimmed =
        if stack.Length > undoLimit then List.truncate undoLimit stack
        else stack
    { mc with UndoStack = trimmed; RedoStack = [] }
