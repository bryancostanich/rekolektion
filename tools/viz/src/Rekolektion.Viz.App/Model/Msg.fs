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
    /// Close every open macro (test isolation).
    | CloseAllTabs
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
    /// Flip the membership of `net` in HighlightedNets (multi-select).
    | ToggleNetHighlight of net: string
    /// Replace HighlightedNets wholesale (master "all/none" affordance).
    | SetHighlightedNets of nets: Set<string>
    /// Flip the membership of `net` in VisibleRatlines.
    | ToggleNetRatline of net: string
    /// Replace VisibleRatlines wholesale (master + W hotkey).
    | SetVisibleRatlines of nets: Set<string>
    | IsolateBlock     of block: string option
    | SetTab           of Model.Tab
    | PolygonPicked    of structure: string * index: int
    /// Replace the polygon Selection with `sel` (empty = nothing
    /// selected). Canvas dispatches this when shift-click extends
    /// or marquee picks polygons in bulk.
    | SetPolygonSelection of sel: Set<string * int>
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
    /// Translate a single top-cell polygon (Boundary or Path)
    /// by (dxDbu, dyDbu). `structure` + `index` identify the
    /// element in `Library.Structures.[structure].Elements`.
    /// Snapped before dispatch.
    | MovePolygonDbu of structure: string * index: int * dxDbu: int64 * dyDbu: int64
    /// Translate every polygon in `sel` by (dxDbu, dyDbu) in one
    /// undo step. Used by polygon multi-drag.
    | MovePolygonsDbu of sel: Set<string * int> * dxDbu: int64 * dyDbu: int64
    /// Remove every currently-selected polygon (Selection set) AND
    /// every selected SRef (InstanceSelection) from the active
    /// macro. Labels anchored to a deleted polygon (per the
    /// `Net.Ratlines.anchorForLabel` rule) get removed too — they
    /// were the wire's name; deleting the wire deletes the name.
    /// Pushes one undo snapshot covering all of it.
    | DeleteSelection
    /// Resize a single top-cell polygon (or rect) so its bbox
    /// becomes `(xMin, yMin, xMax, yMax)`. For a `PolyEl`, every
    /// point lerps from the element's current bbox to the new
    /// one. For a `RectEl`, the coords are replaced directly.
    /// Paths and other element kinds are no-ops at v1.
    | ResizePolygonBbox of
            structure: string
            * index: int
            * xMin: int64
            * yMin: int64
            * xMax: int64
            * yMax: int64
    /// Flip the dimension overlay on/off.
    | ToggleDimensions
    /// Flip the in-process DRC overlay on/off.
    | ToggleDrc
    /// Toggle the major/minor grid dot overlay (G key).
    | ToggleGrid
    /// Toggle the origin-anchored ruler overlay (U key).
    | ToggleRuler
    /// Toggle drag-snap (S key). When on, move + resize land on
    /// the user grid (Config.SnapDefaultUm, or Config.SnapAltUm
    /// with Alt held). When off, drags go raw.
    | ToggleSnap
    /// Master "all ratlines on/off" — the W hotkey + the TopBar
    /// button. Implemented as: if VisibleRatlines is non-empty,
    /// clear it; otherwise fill it with every known net.
    | ToggleRatlines
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
    /// Toggle Tighten mode. When entering, the canvas overlays
    /// the cardinal-direction tighten candidates (numbered) for
    /// the current selection. When exiting, the candidates clear
    /// without committing.
    | ToggleTightenMode
    /// Commit the i-th candidate (1-based) from the live
    /// Tighten-mode overlay, then exit mode. No-op if the index
    /// is out of range or mode is off.
    | CommitTighten of index: int
    /// Pop the active macro's undo stack and restore the
    /// previous library. No-op when the stack is empty.
    | UndoActiveMacro
    /// Save the active macro to disk. On first save of an opened
    /// file, writes to `<base>_edited.mag` (auto-suffix on
    /// collision); subsequent saves overwrite that copy in place.
    | SaveActiveMacro
    /// Save the active macro to a chosen path. The macro's Path
    /// retargets to that path; subsequent Save calls overwrite
    /// it in place.
    | SaveActiveMacroAs of targetPath: string
    /// Result message from the async save Cmd.
    | SaveCompleted of writtenPath: string
    | SaveFailed    of reason: string
    /// Enter inline-rename mode for the tab at `path`.
    | BeginRenameTab of path: string
    /// Cancel inline rename without changes.
    | CancelRenameTab
    /// Commit a tab rename. `newName` is the new basename (with
    /// or without `.mag` extension); the new full path is
    /// `dirname(oldPath) + newName(.mag)`. If the file already
    /// exists on disk, it gets renamed; otherwise the in-memory
    /// path retargets and a future Save lands at the new location.
    | CommitRenameTab of oldPath: string * newName: string
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
