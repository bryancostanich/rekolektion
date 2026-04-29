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
        let lib = Reader.readGds path
        let sidecarPath = Path.ChangeExtension(path, ".nets.json")
        let nets, fromSidecar, sidecarError =
            match Loader.load sidecarPath with
            | Ok (Some sc) -> sc.Nets, true, None
            | Ok None      -> Map.empty, false, None
            | Error msg    -> Map.empty, false, Some msg
        let blocks = Layout.Hierarchy.detect lib
        // Walk SRef/ARef to produce flat polygons for the renderers.
        // For a 64×64 SRAM macro this expands ~5k structure-level
        // polygons to ~400k flat polygons. The cost is paid once at
        // load time so per-frame rendering doesn't pay it.
        let flat = Layout.Flatten.flatten lib
        return Ok {
            Path = path
            Library = lib
            FlatPolygons = flat
            Nets = nets
            Blocks = blocks
            NetsFromSidecar = fromSidecar
            SidecarError = sidecarError
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
    return LabelFlood.derive lib
}
