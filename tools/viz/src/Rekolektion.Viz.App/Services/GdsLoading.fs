module Rekolektion.Viz.App.Services.GdsLoading

open System.IO
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Gds
open Rekolektion.Viz.Core.Sidecar
open Rekolektion.Viz.Core.Net
open Rekolektion.Viz.App.Model.Model

/// Open a GDS file and build a fully-loaded LoadedMacro:
/// 1. Parse GDS via Core.Gds.Reader
/// 2. Try to load <path>.nets.json sidecar; fall back to LabelFlood
/// 3. Detect hierarchy
let load (path: string) : Async<Result<LoadedMacro, string>> = async {
    try
        let lib = Reader.readGds path
        let sidecarPath = Path.ChangeExtension(path, ".nets.json")
        // Loader.load returns Result<Sidecar option, string>:
        //   Ok (Some sc)  — sidecar present and parsed; use its nets
        //   Ok None       — no sidecar file; LabelFlood fallback (silent)
        //   Error msg     — sidecar file exists but is corrupt; LabelFlood
        //                   fallback BUT also record the error on
        //                   LoadedMacro so the UI can surface it. Silently
        //                   absorbing a malformed sidecar masks bugs in
        //                   the Python emitter (rekolektion macro).
        let nets, fromSidecar, sidecarError =
            match Loader.load sidecarPath with
            | Ok (Some sc) -> sc.Nets, true, None
            | Ok None      -> LabelFlood.derive lib, false, None
            | Error msg    -> LabelFlood.derive lib, false, Some msg
        let blocks = Layout.Hierarchy.detect lib
        return Ok {
            Path = path
            Library = lib
            Nets = nets
            Blocks = blocks
            NetsFromSidecar = fromSidecar
            SidecarError = sidecarError
        }
    with ex -> return Error ex.Message
}
