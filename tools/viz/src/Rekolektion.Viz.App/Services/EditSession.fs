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

/// Persist `mc.Library` to disk. Reads from the most recent
/// version on disk for line-level round-trip preservation,
/// substitutes the `transform` lines for every top-level
/// instance, and writes to `targetPath`. Returns the path that
/// was actually written.
///
/// **Read source resolution:** prefer `mc.Path` if that file
/// exists on disk (the user may have saved before, or be
/// overwriting an existing edited copy); otherwise fall back to
/// `mc.OriginalPath`. The fallback is critical after the user
/// renames an unsaved edit — `mc.Path` will be the new name
/// they typed, but no file with that name exists yet, and the
/// only round-trip-safe source is the original file.
let private extOf (path: string) : string =
    (Path.GetExtension path).ToLowerInvariant()

let saveTo (mc: LoadedMacro) (targetPath: string) : string =
    let srcExt = extOf mc.OriginalPath
    let dstExt = extOf targetPath
    // Cross-format save between .gds and .mag isn't supported — the
    // two writers consume different intermediate states. `.rkt` is
    // the exception: it's always available as an export target since
    // the in-memory `Library` converts cleanly to the canonical
    // model (`Rkt.OfGds.fromLibrary`).
    if srcExt <> dstExt && dstExt <> ".rkt" then
        failwithf
            "Save format mismatch: source %s → target %s. The viz \
             editor writes each format back in place; cross-format \
             export to anything other than .rkt isn't supported."
            srcExt dstExt
    match dstExt with
    | ".gds" | ".gds2" ->
        // Full regeneration from the in-memory Library — the
        // writer doesn't round-trip the source byte-for-byte
        // but every structure, element, and transform is
        // reproduced. Sufficient for the editor's
        // save-and-reopen loop.
        Gds.Writer.writeGds targetPath mc.Library
    | ".rkt" ->
        // Convert the legacy in-memory `Library` to the canonical
        // `Rkt.Document` and emit canonical text. Layer numbers
        // resolve to named layers; unknown pairs land as
        // `Unknown(n, d)`. Comments are not preserved on this path
        // because the App's model still holds a `Library` (which
        // doesn't carry them); once the model is migrated to
        // `Rkt.Document` natively, edits propagate comments through.
        let doc = Rkt.OfGds.fromLibrary mc.Library
        let text = Rkt.Writer.write doc
        File.WriteAllText(targetPath, text)
    | _ ->
        let readPath =
            if File.Exists mc.Path then mc.Path
            else mc.OriginalPath
        Mag.Writer.writeUpdated readPath mc.Library targetPath
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
/// production-macro library sizes (~100 KB / snapshot) the
/// total stays under ~20 MB.
let undoLimit = 200

/// Push the current `Library` onto `mc.UndoStack` so a future
/// Undo can restore it. Trims to `undoLimit` from the end. Used
/// by Update.fs *before* applying any edit.
let pushUndoSnapshot (mc: LoadedMacro) : LoadedMacro =
    let stack = mc.Library :: mc.UndoStack
    let trimmed =
        if stack.Length > undoLimit then List.truncate undoLimit stack
        else stack
    { mc with UndoStack = trimmed }
