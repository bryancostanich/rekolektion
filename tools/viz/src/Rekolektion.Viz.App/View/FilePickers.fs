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
        opts.Title <- "Open layout"
        opts.AllowMultiple <- false
        let layoutFilter = FilePickerFileType("Layout files")
        layoutFilter.Patterns <- List<string>([ "*.gds"; "*.gds2"; "*.mag" ])
        layoutFilter.AppleUniformTypeIdentifiers <-
            List<string>([ "public.data"; "public.item" ])
        layoutFilter.MimeTypes <-
            List<string>([ "application/octet-stream"; "text/plain" ])
        let gdsFilter = FilePickerFileType("GDS files")
        gdsFilter.Patterns <- List<string>([ "*.gds"; "*.gds2" ])
        gdsFilter.AppleUniformTypeIdentifiers <-
            List<string>([ "public.data"; "public.item" ])
        gdsFilter.MimeTypes <- List<string>([ "application/octet-stream" ])
        let magFilter = FilePickerFileType("Magic files")
        magFilter.Patterns <- List<string>([ "*.mag" ])
        magFilter.AppleUniformTypeIdentifiers <-
            List<string>([ "public.plain-text"; "public.data" ])
        magFilter.MimeTypes <- List<string>([ "text/plain" ])
        opts.FileTypeFilter <-
            List<FilePickerFileType>([ layoutFilter; gdsFilter; magFilter ])
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

/// Save-file picker for "Save As". Defaults to the same folder
/// as `suggestedPath` and pre-fills with that file's basename.
let pickSavePath (win: Window) (suggestedPath: string)
        : System.Threading.Tasks.Task<string option> =
    task {
        let opts = FilePickerSaveOptions()
        opts.Title <- "Save As"
        // macOS's NSSavePanel auto-appends the extension that
        // matches the FileTypeChoices filter ("*.mag" here). So
        // SuggestedFileName must be the bare stem; if we hand
        // it a name already ending in ".mag" the panel renders
        // ".mag.mag". DefaultExtension is similarly redundant
        // and triggers the same double-suffix on some Avalonia
        // builds, so we leave it unset.
        let stem =
            try System.IO.Path.GetFileNameWithoutExtension suggestedPath
            with _ -> "macro"
        opts.SuggestedFileName <- stem
        // Filter MUST match the source format. macOS's NSSavePanel
        // auto-appends the active filter's extension to whatever the
        // user types; if the only filter is `*.mag` and the source
        // is a `.rkt`, the panel mangles `foo.rkt` → `foo.rkt.mag`
        // and the writer then sees a cross-format save mismatch.
        let primaryExt =
            try
                let e = System.IO.Path.GetExtension suggestedPath
                if System.String.IsNullOrEmpty e then ".mag"
                else e.ToLowerInvariant()
            with _ -> ".mag"
        let label, pattern =
            match primaryExt with
            | ".rkt"          -> "Rekolektion files", "*.rkt"
            | ".gds" | ".gds2" -> "GDS files",         "*.gds"
            | _               -> "Magic files",       "*.mag"
        let primaryFilter = FilePickerFileType(label)
        primaryFilter.Patterns <- List<string>([ pattern ])
        opts.FileTypeChoices <- List<FilePickerFileType>([ primaryFilter ])
        // Anchor the picker to the same folder as the source so
        // SaveAs lands beside the original by default.
        try
            let dir =
                System.IO.Path.GetDirectoryName suggestedPath
            if not (System.String.IsNullOrEmpty dir) && System.IO.Directory.Exists dir then
                let! folder = win.StorageProvider.TryGetFolderFromPathAsync(System.Uri dir)
                if not (isNull folder) then
                    opts.SuggestedStartLocation <- folder
        with _ -> ()
        let! file = win.StorageProvider.SaveFilePickerAsync(opts)
        if isNull file then return None
        else
            let path = file.TryGetLocalPath()
            if isNull path then return None else return Some path
    }

let dispatchSaveAs (source: obj) (suggestedPath: string)
                   (dispatch: Msg.Msg -> unit) : unit =
    let win =
        match hostWindow source with
        | Some w -> Some w
        | None -> mainWindow ()
    match win with
    | None -> ()
    | Some w ->
        ignore (
            task {
                let! picked = pickSavePath w suggestedPath
                match picked with
                | Some path ->
                    // Auto-append the source's extension if the
                    // chosen path has none. macOS's save panel is
                    // happy to write a file without one when
                    // DefaultExtension isn't set; we want the file
                    // to land with the same format it was loaded as.
                    let srcExt =
                        try System.IO.Path.GetExtension suggestedPath
                        with _ -> ".mag"
                    let final =
                        if System.String.IsNullOrEmpty (System.IO.Path.GetExtension path) then
                            path + (if System.String.IsNullOrEmpty srcExt then ".mag" else srcExt)
                        else path
                    dispatch (Msg.SaveActiveMacroAs final)
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
