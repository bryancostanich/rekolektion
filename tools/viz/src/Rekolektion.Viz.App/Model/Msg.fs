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
    /// Set the active edit layer (or clear with `None`). Auto-shows
    /// the layer when set. Dispatched by the 0/1/2/3/4 hotkeys.
    | SetActiveLayer   of LayerKey option
    /// ADR-0004 — replace the effective DRC ruleset. Dispatched at
    /// app boot once `Drc.Rules.tryLoadEffectiveView` finishes (or
    /// later if the user picks a new override file).
    | SetDrcView       of Rekolektion.Viz.Core.Drc.Rules.RulesetView
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
    /// Replace the set of ratline-selected net names. Empty = none.
    | SetSelectedRatlines of nets: Set<string>
    | IsolateBlock     of block: string option
    | SetTab           of Model.Tab
    | PolygonPicked    of key: Rekolektion.Viz.Core.Layout.Flatten.PolyKey
    /// Replace the polygon Selection with `sel` (empty = nothing
    /// selected). Canvas dispatches this when shift-click extends
    /// or marquee picks polygons in bulk.
    | SetPolygonSelection of sel: Set<Rekolektion.Viz.Core.Layout.Flatten.PolyKey>
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
    | MovePolygonsDbu of sel: Set<Rekolektion.Viz.Core.Layout.Flatten.PolyKey> * dxDbu: int64 * dyDbu: int64
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
    /// Toggle the origin-anchored ruler overlay (L key).
    | ToggleRuler
    /// Toggle the interactive routing tool on/off (W key). When on,
    /// canvas clicks start/extend a draft route on the ActiveLayer;
    /// when off, clicks fall through to normal selection.
    | ToggleRoutingMode
    /// Toggle layout label text rendering (all `(label …)` forms).
    | ToggleLabels
    /// Toggle drag-snap (S key). When on, move + resize land on
    /// the user grid (Config.SnapDefaultUm, or Config.SnapAltUm
    /// with Alt held). When off, drags go raw.
    | ToggleSnap
    /// Master "all ratlines on/off" — the U hotkey + the TopBar
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
    /// Toggle Edit Routing mode on/off. While on, the canvas
    /// hover-detects existing routing geometry and renders drag
    /// handles whose orientation pre-constrains the drag axis.
    /// Hotkey: E.
    | ToggleEditRoutingMode
    /// ADR-0002 — begin drawing a new route on `layer` with `width`,
    /// anchored at the world-coord `anchor`. Initialises
    /// `Model.DraftRoute`. `startSnapLayer` carries the snap
    /// target's actual layer (often equal to `layer`, but differs
    /// when the user starts a met2 wire on a li1 pin and a via
    /// stack is required at commit). No-op when no active macro.
    | StartRoute       of layer: LayerKey * width: int64 * startNet: string * anchor: int64 * int64 * startSnapLayer: LayerKey
    /// Update the snap-target layer under the cursor (or clear it
    /// when cursor leaves any snap). Drives the end-side via-stack
    /// emission at RouteFinish.
    | RouteSetEndLayer of LayerKey option
    /// ADR-0006 — walk-around corner sequence from the background
    /// dispatch. Replaces the active draft's Auto field. An empty
    /// list resets to the straight-L fallback.
    | RouteAutoComputed of corners: (int64 * int64) list
    /// ADR-0002 — update the live cursor position for the in-flight
    /// draft. Triggers tentative-L recomputation. No-op when no
    /// DraftRoute is active.
    | RouteMouseMove   of x: int64 * y: int64
    /// ADR-0002 — fix the current tentative L into the draft as a
    /// new corner. Cursor stays at the click position.
    | RouteFixSegment
    /// ADR-0002 — pop the last fixed corner (Backspace during routing).
    | RouteBackspace
    /// ADR-0002 — flip the L-shape posture for the in-flight draft.
    | RouteFlipPosture
    /// ADR-0002 — commit the entire draft (fixed + tentative) into
    /// the active macro's top cell as one undo step. Clears DraftRoute.
    /// Used by right-click and Enter.
    | RouteFinish
    /// ADR-0002 — commit only the FIXED corners of the draft (drop
    /// the tentative L following the cursor). Used by Esc: the user
    /// is saying "stop here, where my last click landed."
    | RouteStop
    /// ADR-0002 — discard the draft without committing (no key bound
    /// currently; kept for programmatic / test use).
    | RouteAbort
    /// Begin a perpendicular drag of an existing wire segment
    /// (route_editing_plan.md v1.1). Fires on canvas mouse-down
    /// over a wire-tagged rect in idle state (no routing mode,
    /// no other drag in flight). Carries everything the pure
    /// `Routing.SegmentDrag.start` needs to seed the state.
    | SegmentDragStart of
        wireId: int option * cellName: string * segIdx: int
        * rect: Rekolektion.Viz.Core.Rkt.Types.Rectangle
        * pickupX: int64 * pickupY: int64
        * shift: bool
    /// Mouse-move while a segment drag is in flight. Updates
    /// the live cursor; the projected geometry re-derives on
    /// each move. No commit until SegmentDragCommit.
    | SegmentDragMove of x: int64 * y: int64
    /// Mouse-up — commit the projected geometry into the active
    /// macro's top cell as one undo step. Clears SegmentDrag.
    /// Per `feedback_endpoint_over_path`: a zero-delta drag is a
    /// no-op (click-without-move shouldn't churn undo).
    | SegmentDragCommit
    /// Esc / pointer-cancel — drop the drag without committing.
    | SegmentDragCancel
    /// Click without drag in idle state — select the wire
    /// (connected component of same-net top-cell rects, terminating
    /// at labeled pin polygons). Args: world-coord x, y, shift
    /// modifier. With shift: toggle the wire's membership in the
    /// existing selection (add if not present, remove if all
    /// already present). Without shift: replace.
    | WireSelectAt of x: int64 * y: int64 * shift: bool
    /// Toggle the walkaround debug overlay (O key). Paints
    /// obstacle bboxes during active drafts so the user can see
    /// what the router considers blocked.
    | ToggleDebugOverlay
    /// One-shot cleanup of legacy WireId corruption: for each
    /// WireId in the active macro's top cell, if its rectangles
    /// are not all spatially connected via bbox-touching, strip
    /// the WireId from every rect carrying it. Geometry untouched.
    /// Fixes the pre-fix drag bug where `touchingNeighbors`
    /// re-stamped unrelated rects with the dragged wire's id.
    | ScrubDispersedWires
    /// Commit a finished route-slide drag (track OR post). Each
    /// entry in `adjusts` is
    /// (sourceIndex, mx1X, mx1Y, my1X, my1Y, mx2X, mx2Y, my2X, my2Y) —
    /// per-coord (dx, dy) multipliers (0 or 1) the handler
    /// multiplies by the gesture's `dxDbu` / `dyDbu`:
    /// `r.X1' = r.X1 + mx1X·dx + mx1Y·dy`, similarly for the
    /// other three coords. Track slides fill only one delta axis;
    /// post drags use both.
    /// `extensions` is a list of NEW rects to append to the cell —
    /// used by track slides whose anchored endpoints moved past
    /// their anchor's bbox (rail extensions). One undo snapshot
    /// covers everything.
    | RouteSlideCommit of
        cell: string
        * dxDbu: int64
        * dyDbu: int64
        * adjusts:
            (int * int64 * int64 * int64 * int64
                 * int64 * int64 * int64 * int64) list
        * extensions: Rekolektion.Viz.Core.Rkt.Types.Rectangle list
    /// Commit the i-th candidate (1-based) from the live
    /// Tighten-mode overlay, then exit mode. No-op if the index
    /// is out of range or mode is off.
    | CommitTighten of index: int
    /// Pop the active macro's undo stack and restore the
    /// previous library. No-op when the stack is empty.
    | UndoActiveMacro
    | RedoActiveMacro
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
