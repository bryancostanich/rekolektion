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
    let drcBg = if model.ShowDrc then "#7a2c2c" else "#262626"
    let drcFg = if model.ShowDrc then "#ffffff" else "#bbbbbb"
    let drcToggle : IView =
        Button.create [
            Button.content "DRC (R)"
            Button.background drcBg
            Button.foreground drcFg
            Button.borderThickness (Thickness(0.0))
            Button.padding (Thickness(10.0, 2.0))
            Button.fontSize 12.0
            Button.verticalAlignment VerticalAlignment.Center
            Button.margin (Thickness(0.0, 0.0, 0.0, 0.0))
            Button.onClick (fun _ -> dispatch Msg.ToggleDrc)
        ] :> IView
    // DRC tooltip-label toggle sits flush against the DRC button —
    // same red accent family so the two visually group as
    // "DRC: highlights + labels". Independent of ShowDrc — labels
    // can be hidden even when highlights are on. Hotkey: Shift+R.
    let drcLabelsBg =
        if model.ShowDrcLabels then "#7a2c2c" else "#262626"
    let drcLabelsFg =
        if model.ShowDrcLabels then "#ffffff" else "#bbbbbb"
    let drcLabelsToggle : IView =
        Button.create [
            Button.content "Labels (⇧R)"
            Button.background drcLabelsBg
            Button.foreground drcLabelsFg
            Button.borderThickness (Thickness(0.0))
            Button.padding (Thickness(10.0, 2.0))
            Button.fontSize 12.0
            Button.verticalAlignment VerticalAlignment.Center
            Button.margin (Thickness(0.0, 0.0, 6.0, 0.0))
            Button.onClick (fun _ -> dispatch Msg.ToggleDrcLabels)
        ] :> IView
    // "On" = at least one net's ratline is visible. Master toggle
    // (U key / button) flips between all-on and all-off in Update.
    let ratlinesActive = not model.Toggle.VisibleRatlines.IsEmpty
    let ratBg = if ratlinesActive then "#8a6b1c" else "#262626"
    let ratFg = if ratlinesActive then "#ffffff" else "#bbbbbb"
    let ratlinesToggle : IView =
        Button.create [
            Button.content "Ratlines (U)"
            Button.background ratBg
            Button.foreground ratFg
            Button.borderThickness (Thickness(0.0))
            Button.padding (Thickness(10.0, 2.0))
            Button.fontSize 12.0
            Button.verticalAlignment VerticalAlignment.Center
            Button.margin (Thickness(0.0, 0.0, 6.0, 0.0))
            Button.onClick (fun _ -> dispatch Msg.ToggleRatlines)
        ] :> IView
    // Grid / Ruler / Snap mirror the keyboard hotkeys G / L / S.
    // Cyan accent when active so they pop alongside the existing
    // overlays without colliding with the amber ratlines or red
    // DRC color slots.
    let mkToggle (label: string) (active: bool) (activeBg: string) (msg: Msg.Msg) : IView =
        let bg = if active then activeBg else "#262626"
        let fg = if active then "#ffffff" else "#bbbbbb"
        Button.create [
            Button.content label
            Button.background bg
            Button.foreground fg
            Button.borderThickness (Thickness(0.0))
            Button.padding (Thickness(10.0, 2.0))
            Button.fontSize 12.0
            Button.verticalAlignment VerticalAlignment.Center
            Button.margin (Thickness(0.0, 0.0, 6.0, 0.0))
            Button.onClick (fun _ -> dispatch msg)
        ] :> IView
    let gridToggle    = mkToggle "Grid (G)"     model.ShowGrid    "#2c4b6f" Msg.ToggleGrid
    let rulerToggle   = mkToggle "Ruler (L)"    model.ShowRuler   "#2c4b6f" Msg.ToggleRuler
    let labelsToggle  = mkToggle "Labels"       model.ShowLabels  "#2c4b6f" Msg.ToggleLabels
    let snapToggle    = mkToggle "Snap (S)"     model.SnapEnabled "#2c4b6f" Msg.ToggleSnap
    // Tighten mode lives next to the editor-action toggles so the
    // mode-state indicator (filled = active) reads at a glance.
    // Cyan accent matches Grid/Ruler/Snap; Tighten's keyboard
    // hotkey is T (see App.fs key handler).
    let tightenToggle = mkToggle "Tighten (T)"  model.TightenMode "#2c4b6f" Msg.ToggleTightenMode
    // Edit Routing mode — hover existing routing geometry to get
    // drag handles. Hotkey: E. Distinct accent (orange) so the
    // mode reads at a glance and isn't visually confused with the
    // visibility-style cyan toggles above.
    let editRoutingToggle =
        mkToggle "Edit Routing (E)" model.EditRoutingMode "#a85a1f"
            Msg.ToggleEditRoutingMode
    // Wire tool — interactive routing on the active layer (ADR-0002).
    // Hotkey: W. Same accent family as Edit Routing so the two
    // routing-related modes group visually.
    let wireToggle =
        mkToggle "Wire (W)" model.RoutingMode "#a85a1f" Msg.ToggleRoutingMode
    // One-shot legacy cleanup: strips WireId from any wire whose
    // rects aren't all spatially connected — corruption left over
    // from the pre-fix drag bug.  Not a mode (no active state),
    // just an action button.
    let scrubButton : IView =
        Button.create [
            Button.content "Scrub WireIds"
            Button.background "#262626"
            Button.foreground "#bbbbbb"
            Button.borderThickness (Thickness(0.0))
            Button.padding (Thickness(10.0, 2.0))
            Button.fontSize 12.0
            Button.verticalAlignment VerticalAlignment.Center
            Button.margin (Thickness(0.0, 0.0, 6.0, 0.0))
            Button.onClick (fun _ -> dispatch Msg.ScrubDispersedWires)
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
                        Border.child (
                            StackPanel.create [
                                StackPanel.orientation Orientation.Horizontal
                                StackPanel.children [
                                    snapToggle
                                    rulerToggle
                                    gridToggle
                                    labelsToggle
                                    tightenToggle
                                    editRoutingToggle
                                    wireToggle
                                    scrubButton
                                    ratlinesToggle
                                    drcToggle
                                    drcLabelsToggle
                                    dimensionsToggle
                                ]
                            ]
                        )
                    ] :> IView
                ]
            ]
        )
    ] :> IView
