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
    StackPanel.create [
        StackPanel.orientation Orientation.Horizontal
        StackPanel.spacing 4.0
        StackPanel.verticalAlignment VerticalAlignment.Center
        StackPanel.children [
            Border.create [
                Border.width 10.0
                Border.height 10.0
                Border.background (sprintf "#%02x%02x%02x" layer.Color.R layer.Color.G layer.Color.B)
                Border.borderThickness 1.0
                Border.borderBrush "#555"
                Border.verticalAlignment VerticalAlignment.Center
            ]
            CheckBox.create [
                CheckBox.isChecked visible
                CheckBox.content layer.Name
                CheckBox.fontSize 11.0
                CheckBox.padding (Avalonia.Thickness(2.0, 0.0, 0.0, 0.0))
                CheckBox.minHeight 0.0
                CheckBox.verticalAlignment VerticalAlignment.Center
                CheckBox.verticalContentAlignment VerticalAlignment.Center
                // Force a tight row height; the fluent template's
                // default ~24px is what was leaving a big gap even
                // with MinHeight 0.
                CheckBox.height 16.0
                CheckBox.renderTransform (ScaleTransform(0.85, 0.85))
                CheckBox.renderTransformOrigin (Avalonia.RelativePoint(0.0, 0.5, Avalonia.RelativeUnit.Relative))
                CheckBox.onIsCheckedChanged (fun e ->
                    match e.Source with
                    | :? CheckBox as cb ->
                        let isChecked = cb.IsChecked.HasValue && cb.IsChecked.Value
                        dispatch (Msg.ToggleLayer (key, isChecked))
                    | _ -> ())
            ]
        ]
    ] :> IView

let view (model: Model.Model) (dispatch: Msg.Msg -> unit) : IView =
    let netButtons : IView list =
        match model.Macro with
        | None -> []
        | Some m ->
            m.Nets
            |> Map.toList
            |> List.sortBy fst
            |> List.map (fun (name, _) ->
                Button.create [
                    Button.content name
                    Button.onClick (fun _ -> dispatch (Msg.HighlightNet (Some name)))
                ] :> IView)

    let blockButtons : IView list =
        match model.Macro with
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
            StackPanel.spacing 0.0
            StackPanel.children layerRows
        ] :> IView

    let children : IView list =
        [
            yield layersHeader
            yield layersBlock
            yield Separator.create [] :> IView
            yield TextBlock.create [
                TextBlock.text "Nets"
                TextBlock.fontWeight Avalonia.Media.FontWeight.Bold
            ] :> IView
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
