module Rekolektion.Viz.App.View.LeftPanel

open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media
open Rekolektion.Viz.Core
open Rekolektion.Viz.App.Model

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
            // Dispatch a flip — the update fn reads current state.
            // Capturing `not visible` from this closure was reading
            // stale truth across renders and breaking re-enable.
            dispatch (Msg.FlipLayer key))
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

let view (model: Model.Model) (dispatch: Msg.Msg -> unit) : IView =
    let netButtons : IView list =
        match Model.activeMacro model with
        | None -> []
        | Some m ->
            m.Nets
            |> Map.toList
            |> List.sortBy fst
            |> List.map (fun (name, _) ->
                let isActive = (model.Toggle.HighlightNet = Some name)
                Button.create [
                    Button.content name
                    Button.fontSize 11.0
                    Button.padding (Avalonia.Thickness(6.0, 2.0))
                    // Active net: bright background. Click toggles —
                    // re-clicking the active net clears highlight.
                    Button.background (if isActive then "#4090ff" else "Transparent")
                    Button.foreground (if isActive then "#000" else "#ddd")
                    Button.onClick (fun _ ->
                        if isActive then dispatch (Msg.HighlightNet None)
                        else dispatch (Msg.HighlightNet (Some name)))
                ] :> IView)
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
                Button.create [
                    Button.content "Clear"
                    Button.fontSize 10.0
                    Button.padding (Avalonia.Thickness(6.0, 1.0))
                    Button.isEnabled (model.Toggle.HighlightNet.IsSome)
                    DockPanel.dock Dock.Right
                    Button.onClick (fun _ -> dispatch (Msg.HighlightNet None))
                ] :> IView
            ]
        ] :> IView

    let blockButtons : IView list =
        match Model.activeMacro model with
        | None -> []
        | Some m ->
            m.Blocks
            |> List.map (fun b ->
                Button.create [
                    Button.content b.Name
                    Button.onClick (fun _ -> dispatch (Msg.IsolateBlock (Some b.Name)))
                ] :> IView)

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
            yield! netButtons
            yield Separator.create [] :> IView
            yield TextBlock.create [
                TextBlock.text "Blocks"
                TextBlock.fontWeight Avalonia.Media.FontWeight.Bold
            ] :> IView
            yield! blockButtons
        ]

    ScrollViewer.create [
        ScrollViewer.content (
            StackPanel.create [
                StackPanel.spacing 4.0
                StackPanel.margin 8.0
                StackPanel.children children
            ]
        )
    ] :> IView
