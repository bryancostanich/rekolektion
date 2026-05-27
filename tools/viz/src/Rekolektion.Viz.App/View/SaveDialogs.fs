module Rekolektion.Viz.App.View.SaveDialogs

/// Modal dialogs for the routed-save flow (`Services.RoutedSave`).
///
/// Four dialogs, all built imperatively for the same reasons as
/// `RunDialog`: one-shot modals, simple bespoke layout, easier to
/// manage their `TaskCompletionSource` plumbing without FuncUI.
///
/// - `MultiFileSaveDialog` — shown when `≥2` files would be written.
///   Lists each file with a checkbox; user picks which to commit.
/// - `ConflictDialog` — shown when `detectMtimeConflicts` returns
///   non-empty. Reload-and-Reapply / Overwrite / Cancel.
/// - `OrphanDialog` — shown when `orphanCells` returns non-empty.
///   User picks a target file per orphan (defaults to the root).
/// - `SaveAsRerootDialog` — shown when Save-As targets a directory
///   outside the source's directory and there are imports. Mirrors
///   the import tree into the new directory.

open System.IO
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Layout

// ─── Multi-file Save dialog (SR4) ──────────────────────────────────────

/// Result of the multi-file Save dialog.
type MultiFileSaveResult =
    /// User clicked "Save Selected" — write these paths.
    | SaveSelected of paths: Set<string>
    /// User clicked Cancel or closed the window.
    | Cancelled

type MultiFileSaveDialog() as this =
    inherit Window()

    let tcs = TaskCompletionSource<MultiFileSaveResult>()
    let mutable settled = false

    let mutable checkboxes : (string * CheckBox) list = []

    let trySet (v: MultiFileSaveResult) =
        if not settled then
            settled <- true
            tcs.TrySetResult v |> ignore

    let buildBody (entries: (string * int) list) : Control =
        // entries = (path, changedCellCount).
        let header =
            TextBlock(
                Text = sprintf "You are saving edits to %d files:" entries.Length,
                FontWeight = Media.FontWeight.Bold,
                Margin = Thickness(0.0, 0.0, 0.0, 8.0))
        let list = StackPanel(Orientation = Orientation.Vertical, Spacing = 4.0)
        for path, count in entries do
            let cb = CheckBox(IsChecked = System.Nullable<bool>(true))
            let label =
                let cells = if count = 1 then "1 cell" else sprintf "%d cells" count
                sprintf "%s  (%s changed)" path cells
            cb.Content <- label
            checkboxes <- (path, cb) :: checkboxes
            list.Children.Add cb
        // Restore order so the on-screen list matches `entries`.
        checkboxes <- List.rev checkboxes
        let saveBtn = Button(Content = "Save Selected", Width = 140.0)
        saveBtn.Click.Add(fun _ ->
            let chosen =
                checkboxes
                |> List.filter (fun (_, cb) ->
                    cb.IsChecked.HasValue && cb.IsChecked.Value)
                |> List.map fst
                |> Set.ofList
            trySet (SaveSelected chosen)
            this.Close())
        let cancelBtn = Button(Content = "Cancel", Width = 80.0)
        cancelBtn.Click.Add(fun _ ->
            trySet Cancelled
            this.Close())
        let footer =
            StackPanel(Orientation = Orientation.Horizontal, Spacing = 8.0,
                       HorizontalAlignment = HorizontalAlignment.Right,
                       Margin = Thickness(0.0, 12.0, 0.0, 0.0))
        footer.Children.Add saveBtn
        footer.Children.Add cancelBtn
        let outer = StackPanel(Orientation = Orientation.Vertical, Spacing = 8.0,
                               Margin = Thickness(16.0))
        outer.Children.Add header
        outer.Children.Add list
        outer.Children.Add footer
        outer :> Control

    member private this.Build (entries: (string * int) list) =
        this.Title <- "Save changed files"
        this.Width <- 600.0
        this.Height <- 280.0 + float (entries.Length * 28)
        this.WindowStartupLocation <- WindowStartupLocation.CenterOwner
        this.Content <- buildBody entries
        this.Closed.Add(fun _ -> trySet Cancelled)

    member this.ShowAsync
            (owner: Window)
            (entries: (string * int) list)
            : Async<MultiFileSaveResult> =
        async {
            let! showTask =
                Avalonia.Threading.Dispatcher.UIThread.InvokeAsync<Task<obj>>(
                    System.Func<Task<obj>>(fun () ->
                        this.Build entries
                        this.ShowDialog<obj>(owner)))
                    .GetTask()
                |> Async.AwaitTask
            let! _ = showTask |> Async.AwaitTask
            return! tcs.Task |> Async.AwaitTask
        }

// ─── Mtime conflict dialog (SR6) ───────────────────────────────────────

type ConflictResolution =
    | ReloadAndReapply
    | OverwriteAll
    | CancelConflict

type ConflictDialog() as this =
    inherit Window()

    let tcs = TaskCompletionSource<ConflictResolution>()
    let mutable settled = false
    let trySet (v: ConflictResolution) =
        if not settled then
            settled <- true
            tcs.TrySetResult v |> ignore

    let buildBody (paths: string list) : Control =
        let header =
            TextBlock(
                Text = "File(s) changed on disk since load:",
                FontWeight = Media.FontWeight.Bold,
                Margin = Thickness(0.0, 0.0, 0.0, 8.0))
        let list = StackPanel(Orientation = Orientation.Vertical, Spacing = 4.0)
        for p in paths do
            list.Children.Add (TextBlock(Text = "  " + p))
        let reloadBtn = Button(Content = "Reload and Re-apply", Width = 180.0)
        reloadBtn.Click.Add(fun _ -> trySet ReloadAndReapply; this.Close())
        let overwriteBtn = Button(Content = "Overwrite", Width = 100.0)
        overwriteBtn.Click.Add(fun _ -> trySet OverwriteAll; this.Close())
        let cancelBtn = Button(Content = "Cancel", Width = 80.0)
        cancelBtn.Click.Add(fun _ -> trySet CancelConflict; this.Close())
        let footer =
            StackPanel(Orientation = Orientation.Horizontal, Spacing = 8.0,
                       HorizontalAlignment = HorizontalAlignment.Right,
                       Margin = Thickness(0.0, 12.0, 0.0, 0.0))
        footer.Children.Add reloadBtn
        footer.Children.Add overwriteBtn
        footer.Children.Add cancelBtn
        let outer = StackPanel(Orientation = Orientation.Vertical, Spacing = 8.0,
                               Margin = Thickness(16.0))
        outer.Children.Add header
        outer.Children.Add list
        outer.Children.Add footer
        outer :> Control

    member private this.Build (paths: string list) =
        this.Title <- "External edit detected"
        this.Width <- 560.0
        this.Height <- 200.0 + float (paths.Length * 24)
        this.WindowStartupLocation <- WindowStartupLocation.CenterOwner
        this.Content <- buildBody paths
        this.Closed.Add(fun _ -> trySet CancelConflict)

    member this.ShowAsync
            (owner: Window)
            (paths: string list)
            : Async<ConflictResolution> =
        async {
            let! showTask =
                Avalonia.Threading.Dispatcher.UIThread.InvokeAsync<Task<obj>>(
                    System.Func<Task<obj>>(fun () ->
                        this.Build paths
                        this.ShowDialog<obj>(owner)))
                    .GetTask()
                |> Async.AwaitTask
            let! _ = showTask |> Async.AwaitTask
            return! tcs.Task |> Async.AwaitTask
        }

// ─── Orphan-cell dialog (SR7) ──────────────────────────────────────────

type OrphanResolution =
    /// Map each orphan cell to a target file path. Cells not in the
    /// map fall back to the root file.
    | AssignTargets of Map<string, string>
    | CancelOrphan

type OrphanDialog() as this =
    inherit Window()

    let tcs = TaskCompletionSource<OrphanResolution>()
    let mutable settled = false
    let trySet (v: OrphanResolution) =
        if not settled then
            settled <- true
            tcs.TrySetResult v |> ignore

    let mutable rowBoxes : (string * TextBox) list = []

    let buildBody (orphans: string list) (defaultPath: string) : Control =
        let header =
            TextBlock(
                Text = "Cells without a source file — pick a target for each:",
                FontWeight = Media.FontWeight.Bold,
                Margin = Thickness(0.0, 0.0, 0.0, 8.0))
        let grid = Grid()
        grid.ColumnDefinitions <- ColumnDefinitions("Auto,*")
        grid.RowDefinitions <-
            RowDefinitions(System.String.Join(",", List.replicate orphans.Length "Auto"))
        for (i, name) in List.indexed orphans do
            let lbl = TextBlock(Text = "  " + name + "  ",
                                Margin = Thickness(0.0, 4.0, 8.0, 4.0))
            Grid.SetRow(lbl, i); Grid.SetColumn(lbl, 0)
            grid.Children.Add lbl
            let box = TextBox(Text = defaultPath, Margin = Thickness(0.0, 2.0, 0.0, 2.0))
            Grid.SetRow(box, i); Grid.SetColumn(box, 1)
            grid.Children.Add box
            rowBoxes <- (name, box) :: rowBoxes
        rowBoxes <- List.rev rowBoxes
        let okBtn = Button(Content = "Assign", Width = 100.0)
        okBtn.Click.Add(fun _ ->
            let assignments =
                rowBoxes
                |> List.map (fun (n, b) -> n, (if isNull b.Text then defaultPath else b.Text))
                |> Map.ofList
            trySet (AssignTargets assignments)
            this.Close())
        let cancelBtn = Button(Content = "Cancel", Width = 80.0)
        cancelBtn.Click.Add(fun _ -> trySet CancelOrphan; this.Close())
        let footer =
            StackPanel(Orientation = Orientation.Horizontal, Spacing = 8.0,
                       HorizontalAlignment = HorizontalAlignment.Right,
                       Margin = Thickness(0.0, 12.0, 0.0, 0.0))
        footer.Children.Add okBtn
        footer.Children.Add cancelBtn
        let outer = StackPanel(Orientation = Orientation.Vertical, Spacing = 8.0,
                               Margin = Thickness(16.0))
        outer.Children.Add header
        outer.Children.Add grid
        outer.Children.Add footer
        outer :> Control

    member private this.Build (orphans: string list) (defaultPath: string) =
        this.Title <- "Assign orphan cells"
        this.Width <- 640.0
        this.Height <- 200.0 + float (orphans.Length * 30)
        this.WindowStartupLocation <- WindowStartupLocation.CenterOwner
        this.Content <- buildBody orphans defaultPath
        this.Closed.Add(fun _ -> trySet CancelOrphan)

    member this.ShowAsync
            (owner: Window)
            (orphans: string list)
            (defaultPath: string)
            : Async<OrphanResolution> =
        async {
            let! showTask =
                Avalonia.Threading.Dispatcher.UIThread.InvokeAsync<Task<obj>>(
                    System.Func<Task<obj>>(fun () ->
                        this.Build orphans defaultPath
                        this.ShowDialog<obj>(owner)))
                    .GetTask()
                |> Async.AwaitTask
            let! _ = showTask |> Async.AwaitTask
            return! tcs.Task |> Async.AwaitTask
        }

// ─── Save-As reroot dialog (SR5) ───────────────────────────────────────

type RerootResolution =
    /// Proceed: write each (sourcePath, targetPath) pair to disk.
    | ProceedReroot of Map<string, string>
    | CancelReroot

type SaveAsRerootDialog() as this =
    inherit Window()

    let tcs = TaskCompletionSource<RerootResolution>()
    let mutable settled = false
    let trySet (v: RerootResolution) =
        if not settled then
            settled <- true
            tcs.TrySetResult v |> ignore

    let buildBody (mapping: (string * string) list) : Control =
        // mapping = (originalSourcePath, newTargetPath).
        let header =
            TextBlock(
                Text =
                    sprintf "Save As will write %d file(s) to mirrored locations:"
                            mapping.Length,
                FontWeight = Media.FontWeight.Bold,
                Margin = Thickness(0.0, 0.0, 0.0, 8.0))
        let list = StackPanel(Orientation = Orientation.Vertical, Spacing = 2.0)
        for src, dst in mapping do
            let line = TextBlock(Text = sprintf "  %s\n      → %s"
                                              (Path.GetFileName src) dst,
                                 Margin = Thickness(0.0, 0.0, 0.0, 4.0))
            list.Children.Add line
        let okBtn = Button(Content = "Save All", Width = 120.0)
        okBtn.Click.Add(fun _ ->
            trySet (ProceedReroot (Map.ofList mapping))
            this.Close())
        let cancelBtn = Button(Content = "Cancel", Width = 80.0)
        cancelBtn.Click.Add(fun _ -> trySet CancelReroot; this.Close())
        let footer =
            StackPanel(Orientation = Orientation.Horizontal, Spacing = 8.0,
                       HorizontalAlignment = HorizontalAlignment.Right,
                       Margin = Thickness(0.0, 12.0, 0.0, 0.0))
        footer.Children.Add okBtn
        footer.Children.Add cancelBtn
        let outer = StackPanel(Orientation = Orientation.Vertical, Spacing = 8.0,
                               Margin = Thickness(16.0))
        outer.Children.Add header
        outer.Children.Add list
        outer.Children.Add footer
        outer :> Control

    member private this.Build (mapping: (string * string) list) =
        this.Title <- "Save As — reroot imports"
        this.Width <- 720.0
        this.Height <- 240.0 + float (mapping.Length * 40)
        this.WindowStartupLocation <- WindowStartupLocation.CenterOwner
        this.Content <- buildBody mapping
        this.Closed.Add(fun _ -> trySet CancelReroot)

    member this.ShowAsync
            (owner: Window)
            (mapping: (string * string) list)
            : Async<RerootResolution> =
        async {
            let! showTask =
                Avalonia.Threading.Dispatcher.UIThread.InvokeAsync<Task<obj>>(
                    System.Func<Task<obj>>(fun () ->
                        this.Build mapping
                        this.ShowDialog<obj>(owner)))
                    .GetTask()
                |> Async.AwaitTask
            let! _ = showTask |> Async.AwaitTask
            return! tcs.Task |> Async.AwaitTask
        }
