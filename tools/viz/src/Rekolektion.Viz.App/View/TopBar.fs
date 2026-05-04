module Rekolektion.Viz.App.View.TopBar

open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.Controls
open Avalonia.Input
open Rekolektion.Viz.App.Model

/// Application menu bar. File menu carries the Open / Run macro
/// commands plus a Recent Files submenu. The 2D/3D toggle stays
/// inside the canvas tabs; window-level actions live here.
let view (model: Model.Model) (dispatch: Msg.Msg -> unit) : IView =
    let recentItems : IView list =
        if List.isEmpty model.RecentFiles then
            [ MenuItem.create [
                MenuItem.header "(none)"
                MenuItem.isEnabled false
              ] :> IView ]
        else
            model.RecentFiles
            |> List.map (fun p ->
                MenuItem.create [
                    MenuItem.header p
                    MenuItem.onClick (fun _ -> dispatch (Msg.RecentFileClicked p))
                ] :> IView)

    Menu.create [
        Menu.background "#1a1a1a"
        Menu.viewItems [
            MenuItem.create [
                MenuItem.header "File"
                MenuItem.viewItems [
                    MenuItem.create [
                        MenuItem.header "Open..."
                        // Display the gesture next to the menu item
                        // (right-aligned in the popup). The actual
                        // hotkey is wired as a Window KeyBinding in
                        // AppView so it fires whether the menu is
                        // open or not.
                        MenuItem.inputGesture (KeyGesture(Key.O, KeyModifiers.Meta))
                        MenuItem.onClick (fun e ->
                            FilePickers.dispatchOpen e.Source dispatch)
                    ]
                    MenuItem.create [
                        MenuItem.header "Run macro..."
                        MenuItem.onClick (fun e ->
                            FilePickers.dispatchRunMacro e.Source dispatch)
                    ]
                    MenuItem.create [
                        MenuItem.header "-"  // separator
                    ]
                    MenuItem.create [
                        MenuItem.header "Recent files"
                        MenuItem.viewItems recentItems
                    ]
                    MenuItem.create [
                        MenuItem.header "-"
                    ]
                    MenuItem.create [
                        MenuItem.header "Close tab"
                        MenuItem.isEnabled (model.ActiveMacroPath.IsSome)
                        MenuItem.onClick (fun _ ->
                            match model.ActiveMacroPath with
                            | Some p -> dispatch (Msg.CloseMacro p)
                            | None -> ())
                    ]
                ]
            ]
            MenuItem.create [
                MenuItem.header "View"
                MenuItem.viewItems [
                    MenuItem.create [
                        MenuItem.header
                            (if model.LogVisible then "Hide log" else "Show log")
                        MenuItem.onClick (fun _ -> dispatch Msg.ToggleLogPane)
                    ]
                ]
            ]
        ]
    ] :> IView
