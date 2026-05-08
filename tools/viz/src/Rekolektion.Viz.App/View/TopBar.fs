module Rekolektion.Viz.App.View.TopBar

open Avalonia
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.FuncUI.Builder
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Rekolektion.Viz.App.Model

/// On macOS, the NativeMenu attached to the window in App.fs is
/// rendered as the system menu bar at the top of the screen — the
/// in-window NativeMenuBar produced here is empty and invisible.
/// On Linux / Windows, NativeMenuBar reads the same attached
/// NativeMenu and renders it as a normal in-window menu strip.
///
/// FuncUI's auto-DSL doesn't expose a `NativeMenuBar.create` helper
/// for this control, so we lift it via `ViewBuilder.Create<T>` —
/// same pattern AppView uses for the canvas controls.
let view (model: Model.Model) (dispatch: Msg.Msg -> unit) : IView =
    let bar : IView = ViewBuilder.Create<NativeMenuBar> []
    let dimsBg = if model.ShowDimensions then "#2c5d6f" else "#262626"
    let dimsFg = if model.ShowDimensions then "#ffffff" else "#bbbbbb"
    let dimensionsToggle : IView =
        Button.create [
            Button.content "Dimensions (D)"
            Button.background dimsBg
            Button.foreground dimsFg
            Button.borderThickness (Thickness(0.0))
            Button.padding (Thickness(10.0, 2.0))
            Button.fontSize 12.0
            Button.verticalAlignment VerticalAlignment.Center
            Button.onClick (fun _ -> dispatch Msg.ToggleDimensions)
        ] :> IView
    Border.create [
        Border.background "#1a1a1a"
        Border.child (
            DockPanel.create [
                DockPanel.lastChildFill false
                DockPanel.children [
                    Border.create [
                        DockPanel.dock Dock.Left
                        Border.child bar
                    ] :> IView
                    Border.create [
                        DockPanel.dock Dock.Right
                        Border.padding (Thickness(8.0, 2.0))
                        Border.child dimensionsToggle
                    ] :> IView
                ]
            ]
        )
    ] :> IView
