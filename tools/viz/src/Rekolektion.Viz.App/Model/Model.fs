module Rekolektion.Viz.App.Model.Model

open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Gds.Types
open Rekolektion.Viz.Core.Sidecar.Types

type Tab = View2D | View3D

type LoadedMacro = {
    Path     : string
    Library  : Library
    // Flattened polygons after walking SRef/ARef hierarchy. The
    // renderers (LayerPainter, Extruder) iterate this rather than
    // raw `Library.Structures` so hierarchical macros render their
    // full content (e.g. an SRAM macro's bitcell array) instead of
    // showing only the top cell's polygons. Computed once at load
    // time in GdsLoading.load.
    FlatPolygons : Layout.Flatten.FlatPolygon array
    Nets     : Map<string, NetEntry>
    Blocks   : Layout.Hierarchy.Block list
    NetsFromSidecar : bool       // false → derived from labels
    SidecarError : string option
}

type RunState =
    | Idle
    | Running of pid: int * args: string list

type Model = {
    Macro       : LoadedMacro option
    Toggle      : Visibility.ToggleState
    Selection   : (string * int) option   // (structure, element index)
    ActiveTab   : Tab
    View2D      : View2DState
    View3D      : View3DState
    Run         : RunState
    RecentFiles : string list
    LogVisible  : bool
    Log         : string list             // newest last
}
and View2DState = { ZoomFactor: float; OffsetX: float; OffsetY: float }
and View3DState = { OrbitYaw: float; OrbitPitch: float; ZoomFactor: float; Ortho: bool }

let empty : Model = {
    Macro = None
    Toggle = Visibility.empty
    Selection = None
    ActiveTab = View2D
    View2D = { ZoomFactor = 1.0; OffsetX = 0.0; OffsetY = 0.0 }
    View3D = { OrbitYaw = 30.0; OrbitPitch = -25.0; ZoomFactor = 1.0; Ortho = false }
    Run = Idle
    RecentFiles = []
    LogVisible = false
    Log = []
}
