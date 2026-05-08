module Rekolektion.Viz.App.Services.EditSession

open System.IO
open Rekolektion.Viz.Core
open Rekolektion.Viz.App.Model.Model

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
let saveTo (mc: LoadedMacro) (targetPath: string) : string =
    let readPath =
        if File.Exists mc.Path then mc.Path
        else mc.OriginalPath
    eprintfn "[viz] save read=%s -> write=%s" readPath targetPath
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
