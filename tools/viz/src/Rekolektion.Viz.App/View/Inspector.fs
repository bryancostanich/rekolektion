module Rekolektion.Viz.App.View.Inspector

open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.Controls
open Rekolektion.Viz.App.Model

let view (model: Model.Model) (_dispatch: Msg.Msg -> unit) : IView =
    let body : IView list =
        [
            yield TextBlock.create [
                TextBlock.text "Inspector"
                TextBlock.fontWeight Avalonia.Media.FontWeight.Bold
            ] :> IView
            match model.Selection with
            | None ->
                yield TextBlock.create [
                    TextBlock.text "(nothing selected)"
                    TextBlock.foreground "#888"
                ] :> IView
            | Some (struc, idx) ->
                yield TextBlock.create [ TextBlock.text (sprintf "structure: %s" struc) ] :> IView
                yield TextBlock.create [ TextBlock.text (sprintf "index: %d" idx) ] :> IView
        ]

    StackPanel.create [
        StackPanel.spacing 6.0
        StackPanel.margin 8.0
        StackPanel.children body
    ] :> IView
