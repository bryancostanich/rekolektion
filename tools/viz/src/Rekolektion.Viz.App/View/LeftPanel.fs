module Rekolektion.Viz.App.View.LeftPanel

open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.Controls
open Avalonia.Layout
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
        StackPanel.spacing 6.0
        StackPanel.children [
            Border.create [
                Border.width 10.0
                Border.height 10.0
                Border.background (sprintf "#%02x%02x%02x" layer.Color.R layer.Color.G layer.Color.B)
                Border.borderThickness 1.0
                Border.borderBrush "#555"
            ]
            CheckBox.create [
                CheckBox.isChecked visible
                CheckBox.content layer.Name
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
        Layout.Layer.allDrawing
        |> List.map (layerRow model.Toggle dispatch)

    let children : IView list =
        [
            yield TextBlock.create [
                TextBlock.text "Layers"
                TextBlock.fontWeight Avalonia.Media.FontWeight.Bold
            ] :> IView
            yield! layerRows
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
