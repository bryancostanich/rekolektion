module Rekolektion.Viz.App.View.FilePickers

open System.Collections.Generic
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Platform.Storage
open Rekolektion.Viz.App.Model

/// Walk up the visual tree until we find the hosting `Window`.
/// Needed because Avalonia 11 file pickers and modal dialogs require
/// a real `TopLevel` / `Window` reference; the FuncUI button event
/// gives us the source `Control` only.
let hostWindow (source: obj) : Window option =
    match source with
    | :? Control as c ->
        match TopLevel.GetTopLevel c with
        | :? Window as w -> Some w
        | _ -> None
    | _ -> None

/// Fall back to the application's MainWindow when we don't have a
/// source control to walk up from (e.g. the Cmd+O KeyBinding fires
/// without a routed-event source).
let mainWindow () : Window option =
    match Application.Current with
    | null -> None
    | app ->
        match app.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            match desktop.MainWindow with
            | null -> None
            | w -> Some w
        | _ -> None

let pickGds (win: Window) : System.Threading.Tasks.Task<string option> =
    task {
        let opts = FilePickerOpenOptions()
        opts.Title <- "Open GDS"
        opts.AllowMultiple <- false
        let filter = FilePickerFileType("GDS files")
        filter.Patterns <- List<string>([ "*.gds"; "*.gds2" ])
        filter.AppleUniformTypeIdentifiers <-
            List<string>([ "public.data"; "public.item" ])
        filter.MimeTypes <- List<string>([ "application/octet-stream" ])
        opts.FileTypeFilter <- List<FilePickerFileType>([ filter ])
        let! files = win.StorageProvider.OpenFilePickerAsync(opts)
        if files.Count = 0 then return None
        else
            let path = files.[0].TryGetLocalPath()
            if isNull path then return None else return Some path
    }

/// Show the GDS picker rooted at `source` (or the main window when
/// no source is available), and dispatch `OpenFile` if the user
/// picked a file. Used by the File → Open menu and the Cmd+O hotkey.
let dispatchOpen (source: obj) (dispatch: Msg.Msg -> unit) : unit =
    let win =
        match hostWindow source with
        | Some w -> Some w
        | None -> mainWindow ()
    match win with
    | None -> ()
    | Some w ->
        ignore (
            task {
                let! picked = pickGds w
                match picked with
                | Some path -> dispatch (Msg.OpenFile path)
                | None -> ()
            })

let openRunDialog (win: Window) (initial: Msg.RunMacroParams)
        : System.Threading.Tasks.Task<Msg.RunMacroParams option> =
    task {
        let dlg = RunDialog.RunDialog()
        let! result = dlg.ShowAsync win initial |> Async.StartAsTask
        return result
    }

let dispatchRunMacro (source: obj) (dispatch: Msg.Msg -> unit) : unit =
    let win =
        match hostWindow source with
        | Some w -> Some w
        | None -> mainWindow ()
    match win with
    | None -> ()
    | Some w ->
        ignore (
            task {
                try
                    let! result = openRunDialog w RunDialog.defaultParams
                    match result with
                    | Some p -> dispatch (Msg.RunMacroRequested p)
                    | None -> ()
                with ex ->
                    eprintfn "[viz] Run macro dialog failed: %s\n%s" ex.Message ex.StackTrace
            })
