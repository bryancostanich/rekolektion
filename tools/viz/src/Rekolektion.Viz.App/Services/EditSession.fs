module Rekolektion.Viz.App.Services.EditSession

open System.IO
open Rekolektion.Viz.Core
open Rekolektion.Viz.App.Model.Model

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
/// Update.fs).  Sets the Dirty flag.  Save writes back to mc.Path
/// — the same path the file was opened from.  "Save As" is the
/// explicit opt-in for writing to a different file; the legacy
/// auto-rename to `<base>_edited.<ext>` on first edit has been
/// removed so viz behaves like a normal editor (Cmd+S overwrites
/// the open file).
let markDirty (mc: LoadedMacro) : LoadedMacro =
    if mc.Dirty then mc
    else { mc with Dirty = true }

/// Maximum undo history per macro. Bounded so a long editing
/// session doesn't grow the heap without limit. 200 is well past
/// what feels useful interactively but small enough that even at
/// production-macro Document sizes the total stays under ~20 MB.
let undoLimit = 200

/// Push the current `(Document, Nets)` onto `mc.UndoStack` so a
/// future Undo can restore both together. Trims to `undoLimit` from
/// the end. Used by Update.fs *before* applying any edit. Clears
/// `RedoStack` — any new edit invalidates the redo history
/// (standard undo/redo).
let pushUndoSnapshot (mc: LoadedMacro) : LoadedMacro =
    let snap : EditSnapshot = { Document = mc.Document; Nets = mc.Nets }
    let stack = snap :: mc.UndoStack
    let trimmed =
        if stack.Length > undoLimit then List.truncate undoLimit stack
        else stack
    { mc with UndoStack = trimmed; RedoStack = [] }
