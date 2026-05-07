module Rekolektion.Viz.Core.Layout.LayoutLoader

open System.IO
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Gds.Types

/// Format-agnostic loader. Switches on file extension:
///   .gds / .gds2 → Gds.Reader.readGds
///   .mag         → Mag.Reader + Layout.MagToLayout (with subcell
///                  resolution rooted at the file's directory)
///
/// Returns the Library plus any warnings (Mag's unknown-layer log
/// + missing-subcell notices). GDS path doesn't currently emit
/// warnings; the warning list is empty for `.gds` inputs.
let load (path: string) : Library * string list =
    let ext =
        try (Path.GetExtension path).ToLowerInvariant()
        with _ -> ""
    let lib, warnings =
        match ext with
        | ".gds" | ".gds2" ->
            Rekolektion.Viz.Core.Gds.Reader.readGds path, []
        | ".mag" ->
            MagToLayout.loadFile path []
        | _ ->
            // Be forgiving: try GDS reader first (handles a few legacy
            // extensions like .stream), surface a clearer error if it
            // fails outright.
            try Rekolektion.Viz.Core.Gds.Reader.readGds path, []
            with ex ->
                failwithf "Unsupported layout extension '%s' for file %s (%s)"
                    ext path ex.Message
    // Translate any non-standard SkyWater layer IDs to their
    // SKY130 equivalents (e.g. sky130_fd_pr_reram cells use
    // 6/0, 7/0, 8/0, 40/0 instead of 65/20 etc.). No-op for
    // files that already use standard IDs — most cells.
    LayerAlias.normalize lib, warnings
