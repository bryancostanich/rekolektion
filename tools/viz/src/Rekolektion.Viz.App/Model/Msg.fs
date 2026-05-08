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
    /// Switch which open macro is the active tab.
    | SetActiveMacro   of path: string
    /// Remove a macro from the open-files list.
    | CloseMacro       of path: string
    /// Close whichever tab is currently active. Convenience for
    /// menu / hotkey paths that don't carry a path.
    | CloseActiveTab
    /// Re-read the active tab's GDS from disk. Used by Cmd+R for
    /// the loop where the user generates a macro in another
    /// process and wants the viewer to refresh.
    | ReloadActiveMacro
    // Async net derivation result. `path` matches the macro the
    // nets were derived for; if the user opens a different file
    // in the meantime, the stale message is dropped.
    | NetsLoaded       of path: string * nets: Map<string, NetEntry>
    | ToggleLayer      of LayerKey * visible: bool
    /// Flip the current visible flag for `key` in the update fn.
    /// View clicks dispatch this so the toggle does not depend on
    /// the value captured at row-build time — that closure went
    /// stale across renders and broke re-enable.
    | FlipLayer        of LayerKey
    | SetAllLayers     of visible: bool
    | ToggleNet        of name: string * visible: bool
    | ToggleBlock      of name: string * visible: bool
    | HighlightNet     of net: string option
    | IsolateBlock     of block: string option
    | SetTab           of Model.Tab
    | PolygonPicked    of structure: string * index: int
    | ClearSelection
    /// Replace the current top-instance selection with `indices`
    /// (empty set = nothing selected). The canvas hit-test path
    /// emits this with the result of a left-click; shift-click
    /// extends the prior set before dispatching.
    | SetInstanceSelection of indices: Set<int>
    | ClearInstanceSelection
    /// Translate every currently-selected instance by (dxDbu, dyDbu).
    /// The canvas snaps the delta to the mfg grid before dispatch
    /// (see Layout.Snap), so Update can apply it verbatim.
    | MoveSelectionDbu of dxDbu: int64 * dyDbu: int64
    /// Flip the dimension overlay on/off.
    | ToggleDimensions
    /// Flip the in-process DRC overlay on/off.
    | ToggleDrc
    /// Duplicate every currently-selected top-level SRef. Each
    /// clone is appended to the top cell's Elements with a small
    /// rightward offset (one selection-bbox width, snapped to the
    /// mfg grid) so the duplicates don't sit on top of the
    /// originals; selection moves to the clones so they become
    /// the next drag target.
    | DuplicateSelection
    /// Rotate the current instance selection 90° CCW around the
    /// bbox-of-bboxes centroid (grid-snapped).
    | RotateSelection90
    /// Mirror the selection about the X axis through the
    /// bbox-of-bboxes centroid (flips Y).
    | MirrorSelectionX
    /// Mirror the selection about the Y axis through the
    /// bbox-of-bboxes centroid (flips X).
    | MirrorSelectionY
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
