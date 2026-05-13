module Rekolektion.Viz.App.Services.GdsLoading

open System.IO
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Gds
open Rekolektion.Viz.Core.Sidecar
open Rekolektion.Viz.Core.Net
open Rekolektion.Viz.App.Model.Model

/// Open a GDS file and build a fully-loaded LoadedMacro:
/// 1. Parse GDS via Core.Gds.Reader
/// 2. Try to load <path>.nets.json sidecar
/// 3. Detect hierarchy
///
/// Net derivation via LabelFlood is NOT done here when there's no
/// sidecar — it's an O(N²) algorithm that takes 10+ seconds for
/// production-size macros and would block the canvas behind a long
/// synchronous wait. Update.fs schedules `deriveNets` as a deferred
/// Cmd after `LoadComplete` so the macro renders immediately and
/// nets fill in when the background derivation finishes.
let load (path: string) : Async<Result<LoadedMacro, string>> = async {
    try
        // Dispatch on file extension. .mag uses the Magic parser
        // (with subcell search rooted at the file's directory);
        // .gds uses the existing GDS reader. Both produce the same
        // Library shape so the rest of the pipeline is unchanged.
        // Transitional: LayoutLoader.load now returns Rkt.Document.
        // The App's downstream code (Hierarchy/Flatten/Instances)
        // still operates on Gds.Library, so we use loadAsLibrary as
        // a shim until those consumers migrate. Each consumer
        // migration will let us drop this shim further upstream.
        let lib, magWarnings = Layout.LayoutLoader.loadAsLibrary path
        for w in magWarnings do
            eprintfn "[viz] %s" w
        let sidecarPath = Path.ChangeExtension(path, ".nets.json")
        let nets, fromSidecar, sidecarError =
            match Loader.load sidecarPath with
            | Ok (Some sc) -> sc.Nets, true, None
            | Ok None      -> Map.empty, false, None
            | Error msg    -> Map.empty, false, Some msg
        // Hierarchy now operates on Rkt.Document; the App's Library
        // still holds Gds.Library, so we convert via OfGds at the
        // call site. Removes one Library reference from this file's
        // future migration.
        let blocks = Layout.Hierarchy.detect (Rkt.OfGds.fromLibrary lib)
        // Walk SRef/ARef to produce flat polygons for the renderers.
        // For a 64×64 SRAM macro this expands ~5k structure-level
        // polygons to ~400k flat polygons. The cost is paid once at
        // load time so per-frame rendering doesn't pay it.
        let flat = Layout.Flatten.flatten (Rkt.OfGds.fromLibrary lib)
        let instances = Layout.Instances.Library.enumerate lib
        return Ok {
            Path = path
            Library = lib
            FlatPolygons = flat
            TopInstances = instances
            Nets = nets
            Blocks = blocks
            NetsFromSidecar = fromSidecar
            SidecarError = sidecarError
            OriginalPath = path
            Dirty = false
            UndoStack = []
        }
    with ex -> return Error ex.Message
}

/// Derive nets via LabelFlood as a deferred async. Slow (10+ s for
/// production-size macros). Caller is expected to invoke this from
/// a Cmd after `load` completes, so the canvas renders immediately
/// while nets fill in later.
///
/// SwitchToThreadPool is critical: Elmish's Cmd.OfAsync starts the
/// async on the caller's SyncContext (= UI thread when dispatched
/// from Update). Without an explicit switch the entire LabelFlood
/// pass runs on the UI thread and blocks the canvas for 10+ seconds,
/// defeating the whole point of the deferred derivation.
let deriveNets (lib: Rekolektion.Viz.Core.Gds.Types.Library)
        : Async<Map<string, Rekolektion.Viz.Core.Sidecar.Types.NetEntry>> = async {
    do! Async.SwitchToThreadPool ()
    // LabelFlood consumes Rkt.Document now; the App's Library is
    // still Gds-flavored so we convert at the call site.
    return LabelFlood.derive (Rkt.OfGds.fromLibrary lib)
}
