module Rekolektion.Viz.App.View.LeftPanel

open Avalonia.Input
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media
open Rekolektion.Viz.Core
open Rekolektion.Viz.App.Model

// -- Layer drag-paint state. UI thread only, mutated by row
// pointer handlers; lives at module level so it survives FuncUI
// re-renders.
//
// Layers panel rows now have TWO checkbox cells (V = polygon
// visibility, D = DRC-overlay visibility). Each cell has its own
// drag-paint sequence: starting a drag in V only paints V across
// rows, and starting in D only paints D — matching the H/R
// pattern that the Nets panel already uses.
//
// `layerDragKind` tags which column the in-flight drag is on
// (or `ValueNone` when no drag is active); `layerDragTarget` is
// the cell state we paint onto each subsequent row the cursor
// enters; `layerDragVisited` keeps a row from flipping back when
// the cursor wobbles back over it (sticky drag semantics).
// Cleared by ScrollViewer-level PointerReleased so a release
// outside any row still ends the drag.
type private LayerDragKind =
    | LayerVisibility
    | LayerDrc

let mutable private layerDragKind    : LayerDragKind voption = ValueNone
let mutable private layerDragTarget  : bool = false
let mutable private layerDragVisited : Set<int * int> = Set.empty

// Net-row drag state. Two checkbox columns per row (H + R) need
// independent drag sequences — dragging in the highlight column
// must not paint the ratline column and vice versa. `netDragKind`
// tags which column the in-flight drag is on; `Highlight` for
// `HighlightedNets`, `Ratline` for `VisibleRatlines`.
type private NetDragKind =
    | Highlight
    | Ratline

let mutable private netDragKind   : NetDragKind voption = ValueNone
let mutable private netDragTarget : bool = false
let mutable private netDragVisited : Set<string> = Set.empty

let private endDragPaint () =
    layerDragKind    <- ValueNone
    layerDragVisited <- Set.empty
    netDragKind      <- ValueNone
    netDragVisited   <- Set.empty

/// A single checkbox cell inside a layer row. `kind` tags whether
/// this cell controls polygon visibility (V) or DRC-overlay
/// visibility (D). Drag-paint is scoped by `kind` — starting a
/// drag on a V cell only paints V across rows, and the same for D.
///
/// `readLive` resolves the cell's current state from the LIVE
/// model at press time rather than from the closure-captured
/// `currentlyOn`. FuncUI reuses `Border` instances across renders
/// without rebinding the lambdas, so a capture would go stale
/// after the first dispatch and every subsequent press would
/// compute the press target from outdated data.
let private layerCell
        (kind: LayerDragKind)
        (key: int * int)
        (currentlyOn: bool)
        (readLive: unit -> bool)
        (setMsg: (int * int) -> bool -> Msg.Msg)
        (dispatch: Msg.Msg -> unit)
        (color: string)
        : IView =
    Border.create [
        Border.background "Transparent"
        Border.cursor (new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand))
        Border.onPointerPressed (fun e ->
            e.Handled <- true
            // Avalonia auto-captures the pointer on PointerPressed;
            // while captured, sibling rows don't fire PointerEntered
            // and the drag-paint can't see them. Releasing capture
            // immediately lets the cursor's hover state propagate to
            // adjacent rows so the entered handler can paint them.
            e.Pointer.Capture null
            let target = not (readLive ())
            layerDragKind    <- ValueSome kind
            layerDragTarget  <- target
            layerDragVisited <- Set.singleton key
            dispatch (setMsg key target))
        Border.onPointerEntered (fun e ->
            match layerDragKind with
            | ValueSome k
                when k = kind
                     && not (layerDragVisited.Contains key)
                     && e.GetCurrentPoint(null).Properties.IsLeftButtonPressed ->
                layerDragVisited <- layerDragVisited.Add key
                dispatch (setMsg key layerDragTarget)
            | _ -> ())
        Border.onPointerReleased (fun _ -> endDragPaint ())
        Border.child (
            Border.create [
                Border.width 11.0
                Border.height 11.0
                Border.background (if currentlyOn then color else "#202020")
                Border.borderThickness 1.0
                Border.borderBrush "#888"
                Border.cornerRadius 1.0
                Border.verticalAlignment VerticalAlignment.Center
            ]
        )
    ] :> IView

let private layerRow
        (toggle: Visibility.ToggleState)
        (dispatch: Msg.Msg -> unit)
        (layer: Layout.Layer.Layer)
        : IView =
    let key = layer.Number, layer.DataType
    let visible    = Visibility.isLayerVisible toggle key
    let drcVisible = Visibility.isDrcVisibleForLayer toggle key
    let readLiveVis () =
        match Rekolektion.Viz.App.Services.AppDispatch.currentModel with
        | Some (m: Model.Model) -> Visibility.isLayerVisible m.Toggle key
        | None -> visible
    let readLiveDrc () =
        match Rekolektion.Viz.App.Services.AppDispatch.currentModel with
        | Some (m: Model.Model) -> Visibility.isDrcVisibleForLayer m.Toggle key
        | None -> drcVisible
    let setVisMsg k target = Msg.ToggleLayer (k, target)
    let setDrcMsg k target = Msg.ToggleDrcLayer (k, target)
    StackPanel.create [
        StackPanel.orientation Orientation.Horizontal
        StackPanel.spacing 6.0
        StackPanel.verticalAlignment VerticalAlignment.Center
        StackPanel.children [
            // Color swatch (inert — distinct visual identity for the
            // layer; click semantics live on the V and D cells).
            Border.create [
                Border.width 10.0
                Border.height 10.0
                Border.background (sprintf "#%02x%02x%02x" layer.Color.R layer.Color.G layer.Color.B)
                Border.borderThickness 1.0
                Border.borderBrush "#555"
                Border.verticalAlignment VerticalAlignment.Center
            ]
            // V cell — polygon visibility on this layer.
            layerCell LayerVisibility key visible readLiveVis
                setVisMsg dispatch "#4090ff"
            // D cell — per-layer DRC-overlay visibility.
            layerCell LayerDrc key drcVisible readLiveDrc
                setDrcMsg dispatch "#e07040"
            TextBlock.create [
                TextBlock.text layer.Name
                TextBlock.fontSize 12.0
                TextBlock.verticalAlignment VerticalAlignment.Center
            ]
        ]
    ] :> IView
    |> fun child ->
        // Single-row pointer-released hook so a release anywhere
        // inside the row's bounds disarms a drag started elsewhere
        // (the cell handlers also disarm, but a pointer release
        // over the swatch / name area would otherwise leak the
        // drag state into the next row's PointerEntered).
        Border.create [
            Border.background "Transparent"
            Border.onPointerReleased (fun _ -> endDragPaint ())
            Border.child child
        ] :> IView

// -- Net rows: two checkboxes per net (highlight | ratline) +
// a name. Tri-state master checkboxes in the section header
// toggle every net at once.
let private netIndicator
        (on: bool)
        (color: string)
        : IView =
    Border.create [
        Border.width 11.0
        Border.height 11.0
        Border.background (if on then color else "#202020")
        Border.borderThickness 1.0
        Border.borderBrush "#888"
        Border.cornerRadius 1.0
        Border.verticalAlignment VerticalAlignment.Center
    ] :> IView

let private clickable
        (onClick: unit -> unit)
        (child: IView)
        : IView =
    Border.create [
        Border.background "Transparent"
        Border.cursor (new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand))
        Border.onPointerPressed (fun e ->
            e.Handled <- true
            onClick ())
        Border.child child
    ] :> IView

/// Resolve the net name for `rowIdx` from the current model's sorted
/// net list. Returns None when the row no longer maps to a net (rare,
/// only happens between async net-derivation completing and a
/// re-render). Centralizing here keeps the handler closures from
/// having to re-implement the alphabetical lookup three times.
let private liveNetName (rowIdx: int) : string option =
    match Rekolektion.Viz.App.Services.AppDispatch.currentModel with
    | None -> None
    | Some m ->
        match Model.activeMacro m with
        | None -> None
        | Some am ->
            let names = am.Nets |> Map.toList |> List.map fst |> List.sort
            if rowIdx < 0 || rowIdx >= names.Length then None
            else Some names.[rowIdx]

/// Net-column drag-paint cell. Wires both PointerPressed (arm drag)
/// and PointerEntered (paint during drag) so the user can click +
/// sweep through a column to toggle many nets at once. The cell
/// resolves the net it acts on via `liveNetName rowIdx` at click
/// time — capturing `name` directly went stale because FuncUI
/// reuses Border instances across renders without rebinding the
/// lambdas (same trap layerRow documents). When the async
/// `NetsLoaded` shifts row positions (alphabetical insert) the
/// stale capture would dispatch against whatever name the row
/// originally rendered, e.g. clicking "A" toggling "D".
let private netCell
        (kind: NetDragKind)
        (rowIdx: int)
        (currentlyOn: bool)
        (readLive: unit -> bool)
        (setMsg: string -> bool -> Msg.Msg)
        (dispatch: Msg.Msg -> unit)
        (color: string)
        : IView =
    Border.create [
        Border.background "Transparent"
        Border.cursor (new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand))
        Border.onPointerPressed (fun e ->
            e.Handled <- true
            e.Pointer.Capture null
            match liveNetName rowIdx with
            | None -> ()
            | Some name ->
                let target = not (readLive ())
                netDragKind <- ValueSome kind
                netDragTarget <- target
                netDragVisited <- Set.singleton name
                dispatch (setMsg name target))
        Border.onPointerEntered (fun e ->
            match netDragKind, liveNetName rowIdx with
            | ValueSome k, Some name
                when k = kind
                     && not (netDragVisited.Contains name)
                     && e.GetCurrentPoint(null).Properties.IsLeftButtonPressed ->
                netDragVisited <- netDragVisited.Add name
                dispatch (setMsg name netDragTarget)
            | _ -> ())
        Border.onPointerReleased (fun _ -> endDragPaint ())
        Border.child (netIndicator currentlyOn color)
    ] :> IView

let private netRow
        (toggle: Visibility.ToggleState)
        (dispatch: Msg.Msg -> unit)
        (rowIdx: int)
        (name: string)
        : IView =
    let highlighted = Visibility.isNetHighlighted toggle name
    let ratlineOn = Visibility.isRatlineVisible toggle name
    // Live readers resolve the net name from the row index against
    // the CURRENT model so they never act on a stale captured name.
    let readLiveHighlight () =
        match liveNetName rowIdx,
              Rekolektion.Viz.App.Services.AppDispatch.currentModel with
        | Some n, Some (m: Model.Model) -> Visibility.isNetHighlighted m.Toggle n
        | _ -> highlighted
    let readLiveRatline () =
        match liveNetName rowIdx,
              Rekolektion.Viz.App.Services.AppDispatch.currentModel with
        | Some n, Some (m: Model.Model) -> Visibility.isRatlineVisible m.Toggle n
        | _ -> ratlineOn
    let setHighlightMsg (currentName: string) (target: bool) =
        // ToggleNetHighlight flips the membership; for the drag
        // target case we want explicit polarity instead. Use
        // SetHighlightedNets with the appropriately-built set.
        match Rekolektion.Viz.App.Services.AppDispatch.currentModel with
        | Some m ->
            let next =
                if target then m.Toggle.HighlightedNets.Add currentName
                else m.Toggle.HighlightedNets.Remove currentName
            Msg.SetHighlightedNets next
        | None ->
            Msg.ToggleNetHighlight currentName
    let setRatlineMsg (currentName: string) (target: bool) =
        match Rekolektion.Viz.App.Services.AppDispatch.currentModel with
        | Some m ->
            let next =
                if target then m.Toggle.VisibleRatlines.Add currentName
                else m.Toggle.VisibleRatlines.Remove currentName
            Msg.SetVisibleRatlines next
        | None ->
            Msg.ToggleNetRatline currentName
    StackPanel.create [
        StackPanel.orientation Orientation.Horizontal
        StackPanel.spacing 6.0
        StackPanel.verticalAlignment VerticalAlignment.Center
        StackPanel.children [
            // H column — polygon highlight (cyan/blue).
            netCell Highlight rowIdx highlighted readLiveHighlight
                setHighlightMsg dispatch "#4090ff"
            // R column — ratline (amber, matches overlay color).
            netCell Ratline rowIdx ratlineOn readLiveRatline
                setRatlineMsg dispatch "#ffc840"
            TextBlock.create [
                TextBlock.text name
                TextBlock.fontSize 11.0
                TextBlock.verticalAlignment VerticalAlignment.Center
            ]
        ]
    ] :> IView

let view (model: Model.Model) (dispatch: Msg.Msg -> unit) : IView =
    let allNets : string list =
        match Model.activeMacro model with
        | None -> []
        | Some m -> m.Nets |> Map.toList |> List.map fst |> List.sort

    let netRows : IView list =
        allNets |> List.mapi (fun i n -> netRow model.Toggle dispatch i n)

    // Header has a "H" / "R" mini-label row + master select-all
    // affordances. The master button next to each glyph flips the
    // whole set: empty -> full, non-empty -> empty.
    let allNetsSet = Set.ofList allNets
    let highlightAllOn =
        not allNetsSet.IsEmpty
        && model.Toggle.HighlightedNets = allNetsSet
    let highlightSomeOn = not model.Toggle.HighlightedNets.IsEmpty
    let ratlineAllOn =
        not allNetsSet.IsEmpty
        && model.Toggle.VisibleRatlines = allNetsSet
    let ratlineSomeOn = not model.Toggle.VisibleRatlines.IsEmpty

    let masterIndicator (allOn: bool) (someOn: bool) (color: string) : IView =
        // Tri-state visual: full = all checked, dim = mixed,
        // empty = none.
        let bg =
            if allOn then color
            elif someOn then "#555555"
            else "#202020"
        Border.create [
            Border.width 11.0
            Border.height 11.0
            Border.background bg
            Border.borderThickness 1.0
            Border.borderBrush "#888"
            Border.cornerRadius 1.0
            Border.verticalAlignment VerticalAlignment.Center
        ] :> IView

    let netsHeader : IView =
        DockPanel.create [
            DockPanel.lastChildFill false
            DockPanel.children [
                TextBlock.create [
                    TextBlock.text "Nets"
                    TextBlock.fontWeight FontWeight.Bold
                    TextBlock.verticalAlignment VerticalAlignment.Center
                    DockPanel.dock Dock.Left
                ] :> IView
                StackPanel.create [
                    StackPanel.orientation Orientation.Horizontal
                    StackPanel.spacing 6.0
                    DockPanel.dock Dock.Right
                    StackPanel.children [
                        // Master highlight toggle: blue square label
                        // + "H" letter for column legend.
                        clickable
                            (fun () ->
                                // Resolve the toggle target from the LIVE
                                // model.  Capturing `highlightSomeOn` /
                                // `allNetsSet` at render time goes stale —
                                // FuncUI reuses the cached header
                                // StackPanel across renders without re-
                                // binding the lambda, so the click would
                                // keep dispatching the original (often
                                // empty) set even after nets loaded.
                                match Rekolektion.Viz.App.Services.AppDispatch.currentModel with
                                | Some m ->
                                    let liveNets =
                                        match Model.activeMacro m with
                                        | None -> Set.empty
                                        | Some am ->
                                            am.Nets |> Map.toSeq
                                            |> Seq.map fst |> Set.ofSeq
                                    let liveSomeOn =
                                        not m.Toggle.HighlightedNets.IsEmpty
                                    let next = if liveSomeOn then Set.empty else liveNets
                                    dispatch (Msg.SetHighlightedNets next)
                                | None -> ())
                            (StackPanel.create [
                                StackPanel.orientation Orientation.Horizontal
                                StackPanel.spacing 3.0
                                StackPanel.children [
                                    masterIndicator highlightAllOn highlightSomeOn "#4090ff"
                                    TextBlock.create [
                                        TextBlock.text "H"
                                        TextBlock.fontSize 10.0
                                        TextBlock.foreground "#bbb"
                                        TextBlock.verticalAlignment VerticalAlignment.Center
                                    ] :> IView
                                ]
                            ] :> IView)
                        clickable
                            (fun () ->
                                // See the H header comment above — same
                                // stale-closure trap, same fix: derive the
                                // toggle target from the live model.
                                match Rekolektion.Viz.App.Services.AppDispatch.currentModel with
                                | Some m ->
                                    let liveNets =
                                        match Model.activeMacro m with
                                        | None -> Set.empty
                                        | Some am ->
                                            am.Nets |> Map.toSeq
                                            |> Seq.map fst |> Set.ofSeq
                                    let liveSomeOn =
                                        not m.Toggle.VisibleRatlines.IsEmpty
                                    let next = if liveSomeOn then Set.empty else liveNets
                                    dispatch (Msg.SetVisibleRatlines next)
                                | None -> ())
                            (StackPanel.create [
                                StackPanel.orientation Orientation.Horizontal
                                StackPanel.spacing 3.0
                                StackPanel.children [
                                    masterIndicator ratlineAllOn ratlineSomeOn "#ffc840"
                                    TextBlock.create [
                                        TextBlock.text "R"
                                        TextBlock.fontSize 10.0
                                        TextBlock.foreground "#bbb"
                                        TextBlock.verticalAlignment VerticalAlignment.Center
                                    ] :> IView
                                ]
                            ] :> IView)
                    ]
                ] :> IView
            ]
        ] :> IView

    let blockButtons : IView list =
        match Model.activeMacro model with
        | None -> []
        | Some m ->
            m.Blocks
            |> List.map (fun b ->
                let isActive = (model.Toggle.IsolatedBlock = Some b.Name)
                Button.create [
                    Button.content b.Name
                    Button.fontSize 11.0
                    Button.padding (Avalonia.Thickness(6.0, 2.0))
                    Button.background (if isActive then "#4090ff" else "Transparent")
                    Button.foreground (if isActive then "#000" else "#ddd")
                    Button.onClick (fun _ ->
                        if isActive then dispatch (Msg.IsolateBlock None)
                        else dispatch (Msg.IsolateBlock (Some b.Name)))
                ] :> IView)
    let blocksHeader : IView =
        DockPanel.create [
            DockPanel.lastChildFill false
            DockPanel.children [
                TextBlock.create [
                    TextBlock.text "Blocks"
                    TextBlock.fontWeight FontWeight.Bold
                    TextBlock.verticalAlignment VerticalAlignment.Center
                    DockPanel.dock Dock.Left
                ] :> IView
                Button.create [
                    Button.content "Clear"
                    Button.fontSize 10.0
                    Button.padding (Avalonia.Thickness(6.0, 1.0))
                    Button.isEnabled (model.Toggle.IsolatedBlock.IsSome)
                    DockPanel.dock Dock.Right
                    Button.onClick (fun _ -> dispatch (Msg.IsolateBlock None))
                ] :> IView
            ]
        ] :> IView

    let layerRows : IView list =
        // Top-of-stack (met5) at the top of the list; allDrawing is
        // ordered bottom-up, so reverse for a top-down view that
        // matches how you'd read the cross-section.
        Layout.Layer.allDrawing
        |> List.sortByDescending (fun l -> l.StackZ)
        |> List.map (layerRow model.Toggle dispatch)

    // Layer-visibility (V) and DRC-viz (D) master tri-state.
    // Mirrors the Nets section's H/R pattern: a small color square
    // next to the column letter. Tri-state visual — full color =
    // all rows on, gray = mixed, dark = all off. Click flips the
    // whole set with empty -> full / non-empty -> empty.
    let allLayerKeys =
        Layout.Layer.allDrawing
        |> List.map (fun l -> (l.Number, l.DataType))
    let allLayerKeysSet = Set.ofList allLayerKeys
    // V master state.
    let vVisibleCount =
        allLayerKeys
        |> List.filter (Visibility.isLayerVisible model.Toggle)
        |> List.length
    let vAllOn  = vVisibleCount = allLayerKeys.Length && allLayerKeys.Length > 0
    let vSomeOn = vVisibleCount > 0
    // D master state — counts include the "Other" bucket so the
    // tri-state reflects the literal "every DRC tile on/off"
    // question.
    let dPanelVisibleCount =
        allLayerKeys
        |> List.filter (Visibility.isDrcVisibleForLayer model.Toggle)
        |> List.length
    let dOtherOn = Visibility.isDrcVisibleOther model.Toggle
    let dTotalSlots = allLayerKeys.Length + 1   // panel + Other
    let dOnCount = dPanelVisibleCount + (if dOtherOn then 1 else 0)
    let dAllOn  = dOnCount = dTotalSlots && dTotalSlots > 0
    let dSomeOn = dOnCount > 0

    let layersHeader : IView =
        DockPanel.create [
            DockPanel.lastChildFill false
            DockPanel.children [
                TextBlock.create [
                    TextBlock.text "Layers"
                    TextBlock.fontWeight FontWeight.Bold
                    TextBlock.verticalAlignment VerticalAlignment.Center
                    DockPanel.dock Dock.Left
                ] :> IView
                StackPanel.create [
                    StackPanel.orientation Orientation.Horizontal
                    StackPanel.spacing 6.0
                    DockPanel.dock Dock.Right
                    StackPanel.children [
                        // V master: blue square + "V" letter.
                        clickable
                            (fun () ->
                                // Read live so the click target is
                                // computed from the current model
                                // (FuncUI's lambda re-bind trap).
                                match Rekolektion.Viz.App.Services.AppDispatch.currentModel with
                                | Some m ->
                                    let liveSomeOn =
                                        allLayerKeys
                                        |> List.exists (Visibility.isLayerVisible m.Toggle)
                                    let target = not liveSomeOn
                                    dispatch (Msg.SetAllLayers target)
                                | None -> ())
                            (StackPanel.create [
                                StackPanel.orientation Orientation.Horizontal
                                StackPanel.spacing 3.0
                                StackPanel.children [
                                    masterIndicator vAllOn vSomeOn "#4090ff"
                                    TextBlock.create [
                                        TextBlock.text "V"
                                        TextBlock.fontSize 10.0
                                        TextBlock.foreground "#bbb"
                                        TextBlock.verticalAlignment VerticalAlignment.Center
                                    ] :> IView
                                ]
                            ] :> IView)
                        // D master: orange square + "D" letter.
                        clickable
                            (fun () ->
                                match Rekolektion.Viz.App.Services.AppDispatch.currentModel with
                                | Some m ->
                                    let liveSomeOn =
                                        (allLayerKeys
                                         |> List.exists (Visibility.isDrcVisibleForLayer m.Toggle))
                                        || Visibility.isDrcVisibleOther m.Toggle
                                    let target = not liveSomeOn
                                    dispatch (Msg.SetAllDrcVisible target)
                                | None -> ())
                            (StackPanel.create [
                                StackPanel.orientation Orientation.Horizontal
                                StackPanel.spacing 3.0
                                StackPanel.children [
                                    masterIndicator dAllOn dSomeOn "#e07040"
                                    TextBlock.create [
                                        TextBlock.text "D"
                                        TextBlock.fontSize 10.0
                                        TextBlock.foreground "#bbb"
                                        TextBlock.verticalAlignment VerticalAlignment.Center
                                    ] :> IView
                                ]
                            ] :> IView)
                    ]
                ] :> IView
            ]
        ] :> IView

    // Column-header strip: "V" and "D" letters above their
    // respective checkbox columns. Layout mirrors `layerRow`'s
    // children with the same widths + spacing so the letters land
    // directly above the V and D cells.
    //
    //   row order:   [swatch 10][V cell 11][D cell 11][name text]
    //   header row:  [spacer 10][  V  11  ][  D  11  ][          ]
    //
    // Spacing between adjacent elements is the row's StackPanel
    // spacing (6.0); we use the same here so the letters track
    // the cells visually even if the row spacing changes.
    let layersColumnHeader : IView =
        StackPanel.create [
            StackPanel.orientation Orientation.Horizontal
            StackPanel.spacing 6.0
            StackPanel.children [
                // Spacer where the swatch sits.
                Border.create [
                    Border.width 10.0
                    Border.height 11.0
                    Border.background "Transparent"
                ] :> IView
                TextBlock.create [
                    TextBlock.text "V"
                    TextBlock.fontSize 10.0
                    TextBlock.foreground "#bbb"
                    TextBlock.width 11.0
                    TextBlock.textAlignment TextAlignment.Center
                    TextBlock.verticalAlignment VerticalAlignment.Center
                ] :> IView
                TextBlock.create [
                    TextBlock.text "D"
                    TextBlock.fontSize 10.0
                    TextBlock.foreground "#bbb"
                    TextBlock.width 11.0
                    TextBlock.textAlignment TextAlignment.Center
                    TextBlock.verticalAlignment VerticalAlignment.Center
                ] :> IView
            ]
        ] :> IView

    // "Other" row — gates the layerless DRC bucket. No swatch
    // (the bucket is layerless); no V column (polygon visibility
    // doesn't apply); only a D checkbox + "Other" label. Sits at
    // the bottom of the Layers list so it doesn't visually
    // interrupt the layer stack.
    let otherDrcOn = Visibility.isDrcVisibleOther model.Toggle
    let otherDrcRow : IView =
        let readLive () =
            match Rekolektion.Viz.App.Services.AppDispatch.currentModel with
            | Some (m: Model.Model) -> Visibility.isDrcVisibleOther m.Toggle
            | None -> otherDrcOn
        StackPanel.create [
            StackPanel.orientation Orientation.Horizontal
            StackPanel.spacing 6.0
            StackPanel.verticalAlignment VerticalAlignment.Center
            StackPanel.children [
                // Spacer matching the swatch column.
                Border.create [
                    Border.width 10.0
                    Border.height 11.0
                    Border.background "Transparent"
                ] :> IView
                // Empty V slot — keeps the D cell aligned with the
                // D column above.
                Border.create [
                    Border.width 11.0
                    Border.height 11.0
                    Border.background "Transparent"
                ] :> IView
                // D cell — single click, no drag (only one row,
                // nothing to drag across).
                Border.create [
                    Border.background "Transparent"
                    Border.cursor (new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand))
                    Border.onPointerPressed (fun e ->
                        e.Handled <- true
                        let target = not (readLive ())
                        dispatch (Msg.ToggleDrcOther target))
                    Border.child (
                        Border.create [
                            Border.width 11.0
                            Border.height 11.0
                            Border.background
                                (if otherDrcOn then "#e07040" else "#202020")
                            Border.borderThickness 1.0
                            Border.borderBrush "#888"
                            Border.cornerRadius 1.0
                            Border.verticalAlignment VerticalAlignment.Center
                        ]
                    )
                ] :> IView
                TextBlock.create [
                    TextBlock.text "Other"
                    TextBlock.fontSize 12.0
                    TextBlock.fontStyle FontStyle.Italic
                    TextBlock.foreground "#aaa"
                    TextBlock.verticalAlignment VerticalAlignment.Center
                ] :> IView
            ]
        ] :> IView

    // Pack layer rows in a tight inner panel so per-row gaps
    // stay 0 even though the outer panel uses 4.0 spacing for
    // section separation.
    let layersBlock : IView =
        StackPanel.create [
            StackPanel.spacing 3.0
            StackPanel.children
                (layersColumnHeader :: layerRows @ [ otherDrcRow ])
        ] :> IView

    let children : IView list =
        [
            yield layersHeader
            yield layersBlock
            yield Separator.create [] :> IView
            yield netsHeader
            yield! netRows
            yield Separator.create [] :> IView
            yield blocksHeader
            yield! blockButtons
        ]

    ScrollViewer.create [
        // Catch a release that happens between rows (gap area) or
        // outside the row hit region but still inside the panel.
        // Without this the drag-paint state stays armed and the
        // next time the user enters a row their hover would paint
        // unintentionally.
        ScrollViewer.onPointerReleased (fun _ -> endDragPaint ())
        ScrollViewer.content (
            StackPanel.create [
                StackPanel.spacing 4.0
                StackPanel.margin 8.0
                StackPanel.children children
            ]
        )
    ] :> IView
