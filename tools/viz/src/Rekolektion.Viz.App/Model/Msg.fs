module Rekolektion.Viz.App.Model.Msg

open Rekolektion.Viz.Core.Visibility
open Rekolektion.Viz.Core.Sidecar.Types

type RunMacroParams = {
    Cell      : string         // foundry | lr
    Words     : int
    Bits      : int
    Mux       : int
    WriteEnable: bool
    ScanChain : bool
    ClockGating: bool
    PowerGating: bool
    WlSwitchoff: bool
    BurnIn    : bool
    ExtractedSpice: bool
    OutputPath: string
}

type Msg =
    | OpenFile         of path: string
    | LoadComplete     of Model.LoadedMacro
    | LoadFailed       of path: string * reason: string
    // Async net derivation result. `path` matches the macro the
    // nets were derived for; if the user opens a different file
    // in the meantime, the stale message is dropped.
    | NetsLoaded       of path: string * nets: Map<string, NetEntry>
    | ToggleLayer      of LayerKey * visible: bool
    | ToggleNet        of name: string * visible: bool
    | ToggleBlock      of name: string * visible: bool
    | HighlightNet     of net: string option
    | IsolateBlock     of block: string option
    | SetTab           of Model.Tab
    | PolygonPicked    of structure: string * index: int
    | ClearSelection
    | Pan2D            of dx: float * dy: float
    | Zoom2D           of factor: float
    | Orbit3D          of dyaw: float * dpitch: float
    | Zoom3D           of factor: float
    | RunMacroRequested of RunMacroParams
    | RunStarted       of pid: int
    | LogLine          of line: string
    | RunCompleted     of outputPath: string
    | RunFailed        of exitCode: int
    | ToggleLogPane
    | RecentFileClicked of path: string
