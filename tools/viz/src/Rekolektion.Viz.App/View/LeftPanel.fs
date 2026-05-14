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

let private endDragPaint () =
    dragActive <- false
    dragVisited <- Set.empty

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
            // with the OPPOSITE of this row's current state as the
            // target. Subsequent rows the cursor enters get
            // painted to the same target (sticky — entering a
            // row already at target = no-op). Use explicit
            // ToggleLayer (key, target) instead of FlipLayer so
            // every row in the drag agrees on direction even when
            // the closure's `visible` value is stale relative to
            // mid-drag dispatches.
            let target = not visible
            dragActive <- true
            dragTarget <- target
            dragVisited <- Set.singleton key
            dispatch (Msg.ToggleLayer (key, target)))
        Border.onPointerEntered (fun e ->
            // Drag-paint: while a drag is in flight AND the left
            // button is still held, painting any unvisited row
            // sets it to the drag's target state.
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

let private netRow
        (toggle: Visibility.ToggleState)
        (dispatch: Msg.Msg -> unit)
        (name: string)
        : IView =
    let highlighted = Visibility.isNetHighlighted toggle name
    let ratlineOn = Visibility.isRatlineVisible toggle name
    StackPanel.create [
        StackPanel.orientation Orientation.Horizontal
        StackPanel.spacing 6.0
        StackPanel.verticalAlignment VerticalAlignment.Center
        StackPanel.children [
            // H column — polygon highlight (cyan/blue)
            clickable
                (fun () -> dispatch (Msg.ToggleNetHighlight name))
                (netIndicator highlighted "#4090ff")
            // R column — ratline (amber, matches the overlay color)
            clickable
                (fun () -> dispatch (Msg.ToggleNetRatline name))
                (netIndicator ratlineOn "#ffc840")
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
        allNets |> List.map (netRow model.Toggle dispatch)

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
