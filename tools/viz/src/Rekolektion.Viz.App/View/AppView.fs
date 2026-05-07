module Rekolektion.Viz.App.View.AppView

open System.IO
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
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

let private gds2DFlatAttr (v: Layout.Flatten.FlatPolygon array) : IAttr<GdsCanvasControl> =
    AttrBuilder<GdsCanvasControl>.CreateProperty<Layout.Flatten.FlatPolygon array>(
        GdsCanvasControl.FlatPolygonsProperty, v, ValueNone)

let private gds2DToggleAttr (v: Visibility.ToggleState) : IAttr<GdsCanvasControl> =
    AttrBuilder<GdsCanvasControl>.CreateProperty<Visibility.ToggleState>(
        GdsCanvasControl.ToggleProperty, v, ValueNone)

let private stack3DLibraryAttr (v: Library option) : IAttr<StackCanvasControl> =
    AttrBuilder<StackCanvasControl>.CreateProperty<Library option>(
        StackCanvasControl.LibraryProperty, v, ValueNone)

let private stack3DFlatAttr (v: Layout.Flatten.FlatPolygon array) : IAttr<StackCanvasControl> =
    AttrBuilder<StackCanvasControl>.CreateProperty<Layout.Flatten.FlatPolygon array>(
        StackCanvasControl.FlatPolygonsProperty, v, ValueNone)

let private stack3DToggleAttr (v: Visibility.ToggleState) : IAttr<StackCanvasControl> =
    AttrBuilder<StackCanvasControl>.CreateProperty<Visibility.ToggleState>(
        StackCanvasControl.ToggleProperty, v, ValueNone)

let private stack3DPickedAttr (handler: System.Action<string, int>) : IAttr<StackCanvasControl> =
    AttrBuilder<StackCanvasControl>.CreateProperty<System.Action<string, int>>(
        StackCanvasControl.PolygonPickedHandlerProperty, handler, ValueNone)

/// Render the canvas (tab control wrapping the 2D + 3D views).
/// Reads the active macro via Model.activeMacro so opening another
/// tab swaps the canvas contents without touching the canvas
/// instance itself.
let private canvas (model: Model.Model) (dispatch: Msg.Msg -> unit) : IView =
    let active = Model.activeMacro model
    let lib = active |> Option.map (fun m -> m.Library)
    let flat =
        active
        |> Option.map (fun m -> m.FlatPolygons)
        |> Option.defaultValue [||]

    let canvas2D : IView =
        ViewBuilder.Create<GdsCanvasControl>
            [ gds2DLibraryAttr lib
              gds2DFlatAttr    flat
              gds2DToggleAttr   model.Toggle ]

    let pickedHandler =
        System.Action<string, int>(fun s i ->
            dispatch (Msg.PolygonPicked (s, i)))

    let canvas3D : IView =
        ViewBuilder.Create<StackCanvasControl>
            [ stack3DLibraryAttr lib
              stack3DFlatAttr    flat
              stack3DToggleAttr   model.Toggle
              stack3DPickedAttr  pickedHandler ]

    let activeIndex =
        match model.ActiveTab with
        | Model.View2D -> 0
        | Model.View3D -> 1

    TabControl.create [
        TabControl.selectedIndex activeIndex
        TabControl.onSelectedIndexChanged (fun idx ->
            let tab = if idx = 1 then Model.View3D else Model.View2D
            dispatch (Msg.SetTab tab))
        TabControl.viewItems [
            TabItem.create [
                TabItem.header "2D"
                // FluentTheme dark variant binds the tab header text
                // colour to the TabItem's Foreground via a template
                // binding; setting Foreground here propagates down
                // through the template so the label is visible
                // regardless of selected/hover state. Setting
                // FontSize keeps the headers readable on retina.
                TabItem.foreground "#ffffff"
                TabItem.fontSize 16.0
                TabItem.fontWeight FontWeight.SemiBold
                TabItem.content canvas2D
            ]
            TabItem.create [
                TabItem.header "3D"
                TabItem.foreground "#ffffff"
                TabItem.fontSize 16.0
                TabItem.fontWeight FontWeight.SemiBold
                TabItem.content canvas3D
            ]
        ]
    ] :> IView

/// One file-tab in the strip above the 2D/3D tabs. The label and
/// the `×` are independent Buttons so each owns its own click —
/// previously the close × was a Border whose PointerPressed was
/// bubbling up to the surrounding tab Border, which dispatched
/// SetActiveMacro instead of CloseMacro and made × appear broken.
let private fileTab
        (active: bool)
        (path: string)
        (dispatch: Msg.Msg -> unit)
        : IView =
    let label =
        try Path.GetFileName(path)
        with _ -> path
    let bg = if active then "#3a3a3a" else "#1f1f1f"
    let fg = if active then "#ffffff" else "#aaaaaa"
    Border.create [
        Border.background bg
        Border.borderThickness (Thickness(0.0, 0.0, 1.0, 0.0))
        Border.borderBrush "#2a2a2a"
        Border.child (
            StackPanel.create [
                StackPanel.orientation Orientation.Horizontal
                StackPanel.spacing 0.0
                StackPanel.children [
                    // Selectable label: drag-select inside the tab
                    // name to grab the filename for copy/paste.
                    // Tap (single click without drag) activates the
                    // tab — Tapped vs PointerPressed is the
                    // distinction that lets selection still work.
                    // Path lives on Tag so FuncUI's lambda-dedup
                    // can't desync the click target from its label.
                    SelectableTextBlock.create [
                        SelectableTextBlock.text label
                        SelectableTextBlock.foreground fg
                        SelectableTextBlock.padding (Thickness(8.0, 4.0))
                        SelectableTextBlock.verticalAlignment VerticalAlignment.Center
                        SelectableTextBlock.cursor (new Cursor(StandardCursorType.Ibeam))
                        SelectableTextBlock.tag (box path)
                        SelectableTextBlock.onTapped (fun e ->
                            e.Handled <- true
                            match e.Source with
                            | :? Avalonia.Controls.Control as c ->
                                match c.Tag with
                                | :? string as p -> dispatch (Msg.SetActiveMacro p)
                                | _ -> ()
                            | _ -> ())
                    ]
                    Button.create [
                        Button.content "×"
                        Button.fontSize 14.0
                        Button.foreground "#aaa"
                        Button.background "Transparent"
                        Button.borderThickness (Thickness(0.0))
                        Button.padding (Thickness(6.0, 0.0))
                        Button.minWidth 0.0
                        Button.minHeight 0.0
                        Button.verticalAlignment VerticalAlignment.Center
                        Button.cursor (new Cursor(StandardCursorType.Hand))
                        Button.tag (box path)
                        Button.onClick (fun e ->
                            e.Handled <- true
                            match e.Source with
                            | :? Avalonia.Controls.Button as b ->
                                match b.Tag with
                                | :? string as p -> dispatch (Msg.CloseMacro p)
                                | _ -> ()
                            | _ -> ())
                    ]
                ]
            ]
        )
    ] :> IView

/// Horizontal strip of file tabs, one per OpenMacro. Always
/// includes a leading zero-size phantom child so the StackPanel's
/// `children` collection never transitions to empty — FuncUI's
/// 1.6.0 diff was leaving the last tab visually attached when
/// children went from `[tab]` to `[]`. With the phantom in place
/// the collection becomes `[phantom]` and the diff cleanly
/// removes the closed tab.
let private fileTabStrip (model: Model.Model) (dispatch: Msg.Msg -> unit) : IView =
    let phantom : IView =
        Border.create [
            Border.width 0.0
            Border.height 0.0
            Border.background "Transparent"
        ] :> IView
    let tabs =
        model.OpenMacros
        |> List.map (fun m ->
            let active = (model.ActiveMacroPath = Some m.Path)
            fileTab active m.Path dispatch)
    let children = phantom :: tabs
    Border.create [
        Border.height 28.0
        Border.background "#141414"
        Border.borderThickness (Thickness(0.0, 0.0, 0.0, 1.0))
        Border.borderBrush "#2a2a2a"
        Border.child (
            ScrollViewer.create [
                ScrollViewer.horizontalScrollBarVisibility Primitives.ScrollBarVisibility.Auto
                ScrollViewer.verticalScrollBarVisibility Primitives.ScrollBarVisibility.Disabled
                ScrollViewer.content (
                    StackPanel.create [
                        StackPanel.orientation Orientation.Horizontal
                        StackPanel.children children
                    ]
                )
            ]
        )
    ] :> IView

let view (model: Model.Model) (dispatch: Msg.Msg -> unit) : IView =
    // Cmd+O / Ctrl+O hotkeys live on the NativeMenuItem ("Open...")
    // attached to the window in App.fs — Avalonia auto-routes the
    // gesture through the native menu so we don't need an explicit
    // KeyBinding here. Adding one would cause double-dispatch on
    // macOS where the system menu also fires the Click event.
    Grid.create [
        Grid.rowDefinitions "Auto,*,Auto"
        Grid.children [
            Grid.create [
                Grid.row 0
                Grid.children [ TopBar.view model dispatch ]
            ]
            // Two GridSplitters between three resizable panels.
            // ColumnDefinitions:
            //   0  Left panel  (160 px default, draggable)
            //   1  splitter
            //   2  Center: file-tab strip + canvas (* — fills)
            //   3  splitter
            //   4  Inspector   (240 px default, draggable)
            Grid.create [
                Grid.row 1
                Grid.columnDefinitions "160,4,*,4,240"
                Grid.children [
                    Border.create [
                        Grid.column 0
                        Border.child (LeftPanel.view model dispatch)
                    ]
                    GridSplitter.create [
                        Grid.column 1
                        GridSplitter.background "#3a3a3a"
                        GridSplitter.resizeDirection GridResizeDirection.Columns
                    ]
                    // Center column: file-tab strip on top, canvas
                    // below. Wrapped in its own Grid with
                    // RowDefinitions "Auto,*" so the strip stays a
                    // tight band and the canvas takes the rest.
                    Grid.create [
                        Grid.column 2
                        Grid.rowDefinitions "Auto,*"
                        Grid.children [
                            Border.create [
                                Grid.row 0
                                Border.child (fileTabStrip model dispatch)
                            ]
                            Border.create [
                                Grid.row 1
                                Border.child (canvas model dispatch)
                            ]
                        ]
                    ]
                    GridSplitter.create [
                        Grid.column 3
                        GridSplitter.background "#3a3a3a"
                        GridSplitter.resizeDirection GridResizeDirection.Columns
                    ]
                    Border.create [
                        Grid.column 4
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
