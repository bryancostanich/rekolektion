module Rekolektion.Viz.App.View.TopBar

open Avalonia.Controls
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
let view (_model: Model.Model) (_dispatch: Msg.Msg -> unit) : IView =
    let bar : IView = ViewBuilder.Create<NativeMenuBar> []
    Border.create [
        Border.background "#1a1a1a"
        Border.child bar
    ] :> IView
