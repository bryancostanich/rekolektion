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
// re-renders. `dragActive` arms on PointerPressed over a layer
// row; `dragTarget` is the visibility state we paint onto every
// row the cursor enters next; `dragVisited` keeps a row from
// flipping back-and-forth if the cursor wobbles back over it
// (sticky semantics — drag sets state, doesn't toggle). Cleared
// by ScrollViewer-level PointerReleased so a release outside a
// row still ends the drag.
let mutable private dragActive : bool = false
let mutable private dragTarget : bool = false
let mutable private dragVisited : Set<int * int> = Set.empty

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
    dragActive <- false
    dragVisited <- Set.empty
    netDragKind <- ValueNone
    netDragVisited <- Set.empty

let private layerRow
        (toggle: Visibility.ToggleState)
        (dispatch: Msg.Msg -> unit)
        (layer: Layout.Layer.Layer)
        : IView =
    let key = layer.Number, layer.DataType
    let visible = Visibility.isLayerVisible toggle key
    // The whole row is the click target. A Border wraps a
    // horizontal panel so empty space between the indicator and
    // text label is hit-testable too. Background = Transparent
    // is required for hit-testing on otherwise-empty Borders.
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
            // First press in a drag-paint sequence: arm the drag
            // with the OPPOSITE of this row's CURRENT state as the
            // target. We must NOT use the closure-captured
            // `visible` here — FuncUI reuses Border instances
            // across renders and doesn't rebind the lambda even
            // when the row's prop dependencies change, so
            // `visible` goes stale after the first dispatch and
            // every subsequent press computes target off the
            // outdated value. Read live via
            // `Services.AppDispatch.currentModel` instead.
            let liveVisible =
                match Rekolektion.Viz.App.Services.AppDispatch.currentModel with
                | Some (m: Model.Model) -> Visibility.isLayerVisible m.Toggle key
                | None -> visible
            let target = not liveVisible
            dragActive <- true
            dragTarget <- target
            dragVisited <- Set.singleton key
            dispatch (Msg.ToggleLayer (key, target)))
        Border.onPointerEntered (fun e ->
            if dragActive
               && not (dragVisited.Contains key)
               && e.GetCurrentPoint(null).Properties.IsLeftButtonPressed then
                dragVisited <- dragVisited.Add key
                dispatch (Msg.ToggleLayer (key, dragTarget)))
        Border.onPointerReleased (fun _ -> endDragPaint ())
        Border.child (
            StackPanel.create [
                StackPanel.orientation Orientation.Horizontal
                StackPanel.spacing 6.0
                StackPanel.verticalAlignment VerticalAlignment.Center
                StackPanel.children [
                    // Color swatch
                    Border.create [
                        Border.width 10.0
                        Border.height 10.0
                        Border.background (sprintf "#%02x%02x%02x" layer.Color.R layer.Color.G layer.Color.B)
                        Border.borderThickness 1.0
                        Border.borderBrush "#555"
                        Border.verticalAlignment VerticalAlignment.Center
                    ]
                    // Visibility indicator (purely visual; click is handled by the row)
                    Border.create [
                        Border.width 11.0
                        Border.height 11.0
                        Border.background (if visible then "#4090ff" else "#202020")
                        Border.borderThickness 1.0
                        Border.borderBrush "#888"
                        Border.cornerRadius 1.0
                        Border.verticalAlignment VerticalAlignment.Center
                    ]
                    TextBlock.create [
                        TextBlock.text layer.Name
                        TextBlock.fontSize 12.0
                        TextBlock.verticalAlignment VerticalAlignment.Center
                    ]
                ]
            ]
        )
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
                                let next = if highlightSomeOn then Set.empty else allNetsSet
                                dispatch (Msg.SetHighlightedNets next))
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
                                let next = if ratlineSomeOn then Set.empty else allNetsSet
                                dispatch (Msg.SetVisibleRatlines next))
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
                    StackPanel.spacing 4.0
                    DockPanel.dock Dock.Right
                    StackPanel.children [
                        Button.create [
                            Button.content "All"
                            Button.fontSize 10.0
                            Button.padding (Avalonia.Thickness(6.0, 1.0))
                            Button.onClick (fun _ -> dispatch (Msg.SetAllLayers true))
                        ] :> IView
                        Button.create [
                            Button.content "None"
                            Button.fontSize 10.0
                            Button.padding (Avalonia.Thickness(6.0, 1.0))
                            Button.onClick (fun _ -> dispatch (Msg.SetAllLayers false))
                        ] :> IView
                    ]
                ] :> IView
            ]
        ] :> IView

    // Pack layer rows in a tight inner panel so per-row gaps
    // stay 0 even though the outer panel uses 4.0 spacing for
    // section separation.
    let layersBlock : IView =
        StackPanel.create [
            StackPanel.spacing 3.0
            StackPanel.children layerRows
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
