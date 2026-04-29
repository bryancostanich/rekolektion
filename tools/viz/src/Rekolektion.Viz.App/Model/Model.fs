module Rekolektion.Viz.App.Model.Model

open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Gds.Types
open Rekolektion.Viz.Core.Sidecar.Types

type Tab = View2D | View3D

type LoadedMacro = {
    Path     : string
    Library  : Library
    Nets     : Map<string, NetEntry>
    Blocks   : Layout.Hierarchy.Block list
    NetsFromSidecar : bool       // false → derived from labels
    // Some msg when a sidecar file existed alongside the GDS but
    // failed to parse (malformed JSON, wrong version, missing
    // fields). LabelFlood is used as the fallback in that case so
    // nets still resolve, but the UI should surface the message —
    // a corrupted sidecar is a bug in the Python emitter that we
    // do NOT want to silently absorb.
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
