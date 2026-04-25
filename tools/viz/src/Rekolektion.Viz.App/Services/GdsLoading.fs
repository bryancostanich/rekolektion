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
        let nets, fromSidecar =
            match Loader.load sidecarPath with
            | Some sc -> sc.Nets, true
            | None -> LabelFlood.derive lib, false
        let blocks = Layout.Hierarchy.detect lib
        return Ok {
            Path = path
            Library = lib
            Nets = nets
            Blocks = blocks
            NetsFromSidecar = fromSidecar
        }
    with ex -> return Error ex.Message
}
