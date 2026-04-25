module Rekolektion.Viz.App.View.LogPane

open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.Controls
open Rekolektion.Viz.App.Model

let view (model: Model.Model) (dispatch: Msg.Msg -> unit) : IView =
    let last = model.Log |> List.tryLast |> Option.defaultValue ""
    let body : IView list =
        if model.LogVisible then
            [
                ScrollViewer.create [
                    ScrollViewer.height 160.0
                    ScrollViewer.content (
                        TextBlock.create [
                            TextBlock.text (System.String.Join("\n", model.Log))
                            TextBlock.fontFamily "Menlo,Consolas,monospace"
                            TextBlock.foreground "#aaa"
                        ]
                    )
                ] :> IView
            ]
        else
            [
                Button.create [
                    Button.content (sprintf "Log - last: %s" last)
                    Button.onClick (fun _ -> dispatch Msg.ToggleLogPane)
                    Button.background "#0d0d0d"
                    Button.foreground "#888"
                ] :> IView
            ]

    DockPanel.create [
        DockPanel.background "#0d0d0d"
        DockPanel.children body
    ] :> IView
