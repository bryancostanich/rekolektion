module Rekolektion.Viz.App.View.TopBar

open System.Collections.Generic
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Platform.Storage
open Rekolektion.Viz.App.Model

/// Walk up the visual tree until we find the hosting `Window`.
/// Needed because Avalonia 11 file pickers and modal dialogs require
/// a real `TopLevel` / `Window` reference; the FuncUI button event
/// gives us the source `Control` only.
let private hostWindow (source: obj) : Window option =
    match source with
    | :? Control as c ->
        match TopLevel.GetTopLevel c with
        | :? Window as w -> Some w
        | _ -> None
    | _ -> None

let private pickGds (source: obj) : System.Threading.Tasks.Task<string option> =
    task {
        match hostWindow source with
        | None -> return None
        | Some win ->
            let opts = FilePickerOpenOptions()
            opts.Title <- "Open GDS"
            opts.AllowMultiple <- false
            let filter = FilePickerFileType("GDS files")
            filter.Patterns <- List<string>([ "*.gds"; "*.gds2" ])
            opts.FileTypeFilter <- List<FilePickerFileType>([ filter ])
            let! files = win.StorageProvider.OpenFilePickerAsync(opts)
            if files.Count = 0 then return None
            else
                let path = files.[0].TryGetLocalPath()
                if isNull path then return None else return Some path
    }

let private openRunDialog (source: obj) (initial: Msg.RunMacroParams)
        : System.Threading.Tasks.Task<Msg.RunMacroParams option> =
    task {
        match hostWindow source with
        | None -> return None
        | Some win ->
            let dlg = RunDialog.RunDialog()
            let! result = dlg.ShowAsync win initial |> Async.StartAsTask
            return result
    }

let view (model: Model.Model) (dispatch: Msg.Msg -> unit) : IView =
    let currentLabel =
        model.Macro
        |> Option.map (fun m -> m.Path)
        |> Option.defaultValue "(no file)"

    let openClick (e: Avalonia.Interactivity.RoutedEventArgs) =
        let src = e.Source
        // Fire-and-forget: bridge the picker Task back to dispatch on
        // completion. If the user cancels, no message is dispatched.
        ignore (
            task {
                let! picked = pickGds src
                match picked with
                | Some path -> dispatch (Msg.OpenFile path)
                | None -> ()
            })

    let runClick (e: Avalonia.Interactivity.RoutedEventArgs) =
        let src = e.Source
        ignore (
            task {
                let! result = openRunDialog src RunDialog.defaultParams
                match result with
                | Some p -> dispatch (Msg.RunMacroRequested p)
                | None -> ()
            })

    DockPanel.create [
        DockPanel.height 36.0
        DockPanel.background "#1a1a1a"
        DockPanel.children [
            StackPanel.create [
                StackPanel.orientation Orientation.Horizontal
                StackPanel.spacing 8.0
                StackPanel.margin (8.0, 4.0, 8.0, 4.0)
                StackPanel.children [
                    Button.create [
                        Button.content "Open..."
                        Button.onClick openClick
                    ]
                    Button.create [
                        Button.content "Run macro..."
                        Button.onClick runClick
                    ]
                    TextBlock.create [
                        TextBlock.text currentLabel
                        TextBlock.foreground "#888"
                        TextBlock.verticalAlignment VerticalAlignment.Center
                    ]
                ]
            ]
        ]
    ] :> IView
