module Rekolektion.Viz.App.View.AppView

open Avalonia.Controls
open Avalonia.FuncUI.Builder
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Gds.Types
open Rekolektion.Viz.App.Canvas2D.GdsCanvasControl
open Rekolektion.Viz.App.Canvas3D.StackCanvasControl
open Rekolektion.Viz.App.Model

// -- FuncUI lift for the two custom Avalonia controls. Mirrors the
// Moroder DieCanvasView pattern: each AvaloniaProperty is exposed via
// a typed CreateProperty<TValue>(prop, value, ValueNone) attr.

let private gds2DLibraryAttr (v: Library option) : IAttr<GdsCanvasControl> =
    AttrBuilder<GdsCanvasControl>.CreateProperty<Library option>(
        GdsCanvasControl.LibraryProperty, v, ValueNone)

let private gds2DToggleAttr (v: Visibility.ToggleState) : IAttr<GdsCanvasControl> =
    AttrBuilder<GdsCanvasControl>.CreateProperty<Visibility.ToggleState>(
        GdsCanvasControl.ToggleProperty, v, ValueNone)

let private stack3DLibraryAttr (v: Library option) : IAttr<StackCanvasControl> =
    AttrBuilder<StackCanvasControl>.CreateProperty<Library option>(
        StackCanvasControl.LibraryProperty, v, ValueNone)

let private stack3DToggleAttr (v: Visibility.ToggleState) : IAttr<StackCanvasControl> =
    AttrBuilder<StackCanvasControl>.CreateProperty<Visibility.ToggleState>(
        StackCanvasControl.ToggleProperty, v, ValueNone)

let private canvas (model: Model.Model) (_dispatch: Msg.Msg -> unit) : IView =
    let lib = model.Macro |> Option.map (fun m -> m.Library)

    let canvas2D : IView =
        ViewBuilder.Create<GdsCanvasControl>
            [ gds2DLibraryAttr lib
              gds2DToggleAttr   model.Toggle ]

    let canvas3D : IView =
        ViewBuilder.Create<StackCanvasControl>
            [ stack3DLibraryAttr lib
              stack3DToggleAttr   model.Toggle ]

    let activeIndex =
        match model.ActiveTab with
        | Model.View2D -> 0
        | Model.View3D -> 1

    TabControl.create [
        TabControl.selectedIndex activeIndex
        TabControl.onSelectedIndexChanged (fun idx ->
            let tab = if idx = 1 then Model.View3D else Model.View2D
            _dispatch (Msg.SetTab tab))
        TabControl.viewItems [
            TabItem.create [
                TabItem.header "2D"
                TabItem.content canvas2D
            ]
            TabItem.create [
                TabItem.header "3D"
                TabItem.content canvas3D
            ]
        ]
    ] :> IView

let view (model: Model.Model) (dispatch: Msg.Msg -> unit) : IView =
    Grid.create [
        Grid.rowDefinitions "Auto,*,Auto"
        Grid.children [
            Grid.create [
                Grid.row 0
                Grid.children [ TopBar.view model dispatch ]
            ]
            Grid.create [
                Grid.row 1
                Grid.columnDefinitions "240,*,260"
                Grid.children [
                    Border.create [
                        Grid.column 0
                        Border.child (LeftPanel.view model dispatch)
                    ]
                    Border.create [
                        Grid.column 1
                        Border.child (canvas model dispatch)
                    ]
                    Border.create [
                        Grid.column 2
                        Border.child (Inspector.view model dispatch)
                    ]
                ]
            ]
            Grid.create [
                Grid.row 2
                Grid.children [ LogPane.view model dispatch ]
            ]
        ]
    ] :> IView
