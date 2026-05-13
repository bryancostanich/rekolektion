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
        // Canonical save: emit the in-memory Document directly.
        let text = Rkt.Writer.write mc.Document
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
/// Update.fs *before* applying any edit.
let pushUndoSnapshot (mc: LoadedMacro) : LoadedMacro =
    let stack = mc.Document :: mc.UndoStack
    let trimmed =
        if stack.Length > undoLimit then List.truncate undoLimit stack
        else stack
    { mc with UndoStack = trimmed }
