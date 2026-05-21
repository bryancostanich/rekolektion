module Rekolektion.Viz.App.Model.Model

open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Sidecar.Types

type Tab = View2D | View3D

type LoadedMacro = {
    Path     : string
    Document : Document
    // Flattened polygons after walking SRef/ARef hierarchy. The
    // renderers (LayerPainter, Extruder) iterate this rather than
    // raw `Document.Cells` so hierarchical macros render their
    // full content (e.g. an SRAM macro's bitcell array) instead of
    // showing only the top cell's polygons. Recomputed every time
    // `Document` changes (drag commit, rotate, mirror) so the canvas
    // always renders the edited geometry.
    FlatPolygons : Layout.Flatten.FlatPolygon array
    /// Movable top-level SRef instances, with their world bbox.
    /// Hit-test, selection, and drag operate on these. Recomputed
    /// alongside `FlatPolygons` after each edit. ARefs at the top
    /// are intentionally excluded — array unrolls aren't movable
    /// as a unit at P0.
    TopInstances : Layout.Instances.Instance array
    Nets     : Map<string, NetEntry>
    Blocks   : Layout.Hierarchy.Block list
    NetsFromSidecar : bool       // false → derived from labels
    SidecarError : string option
    /// Path the macro was originally opened from. `Path` flips to
    /// the `_edited.mag` copy on first edit; `OriginalPath` stays
    /// pinned at the source so Save knows where to round-trip
    /// from. Same as `Path` for unedited macros.
    OriginalPath : string
    /// True after the user has made any edit that hasn't been
    /// saved. Drives the title-bar "[edited]" indicator and the
    /// close-with-unsaved-changes prompt.
    Dirty : bool
    /// Per-macro undo stack — snapshots of `Document` from before
    /// each edit (newest first). Capped to keep memory bounded.
    /// Cmd+Z pops and restores; the popped document replaces the
    /// current one and re-derives FlatPolygons / TopInstances.
    UndoStack : Document list
    /// Redo stack — when Undo pops, the CURRENT document goes here
    /// so Cmd+Shift+Z can put it back. Any new edit clears this
    /// stack (standard undo/redo semantics: a new branch invalidates
    /// the redo history).
    RedoStack : Document list
}

type RunState =
    | Idle
    | Running of pid: int * args: string list

/// Multiple GDS files can be open at once. `OpenMacros` is ordered
/// in tab-display order (left-to-right). `ActiveMacroPath` tracks
/// which one drives the canvas / left panel / inspector. Only the
/// active macro renders in the canvas; the others are kept warm in
/// memory so flipping back is instant. Toggle / Selection are
/// global — they reset when the active macro changes.
type Model = {
    OpenMacros      : LoadedMacro list
    ActiveMacroPath : string option
    Toggle          : Visibility.ToggleState
    Selection       : Set<Layout.Flatten.PolyKey>  // (cell, element index, top-instance)
    /// Selected top-level SRef instances by their stable Index in
    /// the active macro's top structure. Empty set = nothing
    /// selected. Switching tabs / loading a new file clears this.
    InstanceSelection : Set<int>
    /// Net names whose ratline overlay is currently selected.
    /// Clicking a ratline in the canvas toggles membership; the
    /// renderer paints these distinctly so the user can identify
    /// which net a given MST edge represents (useful for diagnosing
    /// suspect cross-net edges).
    SelectedRatlines : Set<string>
    /// Per-tab snapshot of (Selection, InstanceSelection,
    /// SelectedRatlines) for every open macro EXCEPT the active
    /// one. When the user switches tabs, the active tab's three
    /// selection sets get stashed here under the OLD path, and
    /// the NEW path's saved sets (if any) get loaded back into
    /// the top-level fields. This way every tab keeps its
    /// selection across switches — the in-flight active fields
    /// stay the same so the canvas / inspector code is
    /// unchanged.
    SavedSelections : Map<string, Set<Layout.Flatten.PolyKey> * Set<int> * Set<string>>
    /// Whether the canvas draws the dimension overlay (arrows +
    /// µm labels between selected instances and their nearest
    /// in-radius neighbors). Toggleable via TopBar / D key. Off
    /// by default — the overlay can hairball the canvas on dense
    /// layouts.
    ShowDimensions : bool
    /// Whether the canvas runs the in-process DRC and renders
    /// violations. Toggleable via TopBar / R key. Off by default
    /// because DRC runs every frame on edit and is O(N²) per
    /// layer — fine for a single-cell edit, expensive on a full
    /// macro flatten.
    ShowDrc : bool
    /// Magic-compatible names of DRC rules the user has silenced
    /// (e.g. "met1.6" to hide min-area complaints during a sketch
    /// pass; "nwell.2a" to stop showing spacing errors that come
    /// from a foundry-COREID-waivered cell). Passed straight into
    /// `Drc.Check.checkWithToggles` — any rule whose name appears
    /// here is skipped. Empty by default = run every rule.
    DisabledDrcRules : Set<string>
    /// Grid overlay: major + minor dots. Toggled by G. Per-µm
    /// spacing comes from Services.Config.current. Persists
    /// across tab switches. Independent from ShowRuler.
    ShowGrid : bool
    /// Origin-anchored ruler with tick marks and µm labels.
    /// Toggled by U. Independent from ShowGrid so the user can
    /// pick the visual they want.
    ShowRuler : bool
    /// Layout label text (net names, port markers — anything
    /// emitted as a `(label …)` form in the .rkt). When off, the
    /// 2D label painter is skipped so the canvas reads as pure
    /// geometry. Toggled from the TopBar.
    ShowLabels : bool
    /// Snap mode: when true, move/resize drags snap to the user
    /// grid (Config.SnapDefaultUm normally, Config.SnapAltUm
    /// when Alt is held). When false, drags go raw (1 DBU = 1 nm
    /// resolution, no grid snap). Toggled by S.
    SnapEnabled : bool
    /// Tighten mode: when active, the canvas overlays the
    /// candidate cardinal-direction tighten arrows (numbered)
    /// instead of moving anything. Click a number → that single
    /// tighten commits + mode exits. T or Esc exits without
    /// committing. Computed from the active macro + selection
    /// each render.
    TightenMode : bool
    /// Edit Routing mode: when active, the canvas hover-detects
    /// existing routing geometry and overlays drag handles (track
    /// arrows + post spheres). Toggled by E. Click-drag on a
    /// handle reshapes the route; click-drag elsewhere still
    /// orbits / pans. Mode persists across tab switches.
    EditRoutingMode : bool
    /// Routing tool armed (W key). When true, canvas left-clicks
    /// start or extend a draft route on the active layer; when
    /// false, clicks fall through to normal selection. Independent
    /// from `DraftRoute` (the tool may be armed without an in-flight
    /// draft, e.g. just after finishing a previous route).
    RoutingMode : bool
    /// In-flight route the user is drawing (ADR-0002). `None` when
    /// no route is being drawn. Each click during routing appends to
    /// `Points`; FinishRoute commits the whole batch as one undo
    /// step into the active macro's top cell; AbortRoute discards.
    /// The renderer composites these as an overlay on top of the
    /// cell's existing geometry.
    DraftRoute : Routing.Draft.DraftRoute option
    /// Path of the tab currently in inline-rename mode (file-tab
    /// title swapped for a TextBox). None when no tab is being
    /// renamed. Cleared on Esc, on commit, or when the user
    /// switches tabs.
    RenamingPath : string option
    ActiveTab       : Tab
    View2D          : View2DState
    View3D          : View3DState
    Run             : RunState
    RecentFiles     : string list
    LogVisible      : bool
    Log             : string list             // newest last
}
and View2DState = { ZoomFactor: float; OffsetX: float; OffsetY: float }
and View3DState = { OrbitYaw: float; OrbitPitch: float; ZoomFactor: float; Ortho: bool }

/// Resolve the currently focused tab to its macro, if any.
let activeMacro (m: Model) : LoadedMacro option =
    match m.ActiveMacroPath with
    | None -> None
    | Some p -> m.OpenMacros |> List.tryFind (fun mc -> mc.Path = p)

let empty : Model = {
    OpenMacros = []
    ActiveMacroPath = None
    Toggle = Visibility.empty
    Selection = Set.empty
    InstanceSelection = Set.empty
    SelectedRatlines = Set.empty
    SavedSelections = Map.empty
    ShowDimensions = false
    ShowDrc = false
    DisabledDrcRules = Set.empty
    ShowGrid = true
    ShowRuler = true
    ShowLabels = true
    SnapEnabled = false
    TightenMode = false
    EditRoutingMode = false
    RoutingMode = false
    DraftRoute = None
    RenamingPath = None
    ActiveTab = View2D
    View2D = { ZoomFactor = 1.0; OffsetX = 0.0; OffsetY = 0.0 }
    View3D = { OrbitYaw = 225.0; OrbitPitch = 35.0; ZoomFactor = 1.0; Ortho = false }
    Run = Idle
    RecentFiles = []
    LogVisible = false
    Log = []
}
