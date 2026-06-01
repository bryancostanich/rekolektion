namespace Rekolektion.Viz.App

open System
open System.IO
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Input
open Avalonia.Styling
open Avalonia.Themes.Fluent
open Avalonia.FuncUI.Hosts
open Avalonia.FuncUI.Elmish
open Elmish
open Rekolektion.Viz.App.Model
open Rekolektion.Viz.App.Model.Update
open Rekolektion.Viz.App.Services
open Rekolektion.Viz.App.View

/// Module-level handle to the live Elmish dispatcher. Captured by
/// `syncDispatch` below the first time `Program.runWithDispatch`
/// invokes it (during MainWindow construction). Read by services
/// that need to inject Msgs from outside the UI tree —
/// CommandListener (UDS POST endpoints) is the only consumer
/// today, but anything not wired through Elmish Cmd / Sub goes
/// through here.
///
/// The mutable ref is intentionally not thread-safe: `current`
/// is only written once (UI thread, during boot) and read after
/// that, so a plain `option` ref is fine. `send` is a no-op
/// before the dispatcher is wired so early calls (e.g. headless
/// boot) don't NPE. Pattern lifted from Moroder.Viz's App.fs.
/// Shim that re-exports the canonical dispatcher module so the
/// rest of App.fs (and anything that imports `Rekolektion.Viz.App`
/// for `AppDispatch`) keeps the same module path it had before
/// dispatching moved into its own file.
module AppDispatch =
    let send (msg: Msg.Msg) = Services.AppDispatch.send msg
    let setCurrent (d: (Msg.Msg -> unit) option) =
        Services.AppDispatch.current <- d
    let setCurrentActivePath (p: string option) =
        Services.AppDispatch.currentActivePath <- p

/// Pure window-level keymap: given the current `Model` (or `None`
/// during boot) and a keypress, return the `Msg` to dispatch (or
/// `None` if the key isn't bound for that model state). Pulled out
/// of the KeyDown handler so the order-sensitive interactions —
/// layer-focus keys vs. Tighten-mode capture, in-flight route keys
/// vs. global Esc/Delete — are unit-testable without driving
/// Avalonia events.
///
/// Match order matters: F# top-to-bottom first-match. Layer-focus
/// arms (` 1 2 3 4 0) carry `when not tighten` guards so the
/// number-keys-as-Tighten-commit arm at the bottom can catch them
/// when Tighten mode is active.
module KeyMap =
    let dispatchFor (model: Model.Model option) (key: Key) (mods: KeyModifiers) : Msg.Msg option =
        let tighten =
            model
            |> Option.map (fun m -> m.TightenMode)
            |> Option.defaultValue false
        let segmentDrag =
            model
            |> Option.map (fun m -> m.SegmentDrag.IsSome)
            |> Option.defaultValue false
        let routingActive =
            model
            |> Option.map (fun m -> m.RoutingMode || m.DraftRoute.IsSome)
            |> Option.defaultValue false
        let draftRoute =
            model
            |> Option.map (fun m -> m.DraftRoute.IsSome)
            |> Option.defaultValue false
        match key, mods with
        | Key.D,     KeyModifiers.None -> Some Msg.ToggleDimensions
        | Key.R,     KeyModifiers.None -> Some Msg.ToggleDrc
        | Key.O,     KeyModifiers.None -> Some Msg.ToggleDebugOverlay
        | Key.W,     KeyModifiers.None -> Some Msg.ToggleRoutingMode
        | Key.U,     KeyModifiers.None -> Some Msg.ToggleRatlines
        | Key.L,     KeyModifiers.None -> Some Msg.ToggleRuler
        | Key.G,     KeyModifiers.None -> Some Msg.ToggleGrid
        | Key.S,     KeyModifiers.None -> Some Msg.ToggleSnap
        | Key.D,     KeyModifiers.Meta -> Some Msg.DuplicateSelection
        | Key.Z,     KeyModifiers.Meta -> Some Msg.UndoActiveMacro
        | Key.Z,     m when m = (KeyModifiers.Meta ||| KeyModifiers.Shift) ->
            Some Msg.RedoActiveMacro
        | Key.Space, KeyModifiers.None -> Some Msg.RotateSelection90
        | Key.X,     KeyModifiers.None -> Some Msg.MirrorSelectionX
        | Key.Y,     KeyModifiers.None -> Some Msg.MirrorSelectionY
        | Key.T,     KeyModifiers.None -> Some Msg.ToggleTightenMode
        | Key.E,     KeyModifiers.None -> Some Msg.ToggleEditRoutingMode
        | Key.OemTilde, KeyModifiers.None when not tighten ->
            Some (Msg.SetActiveLayer (Some (67, 20)))   // li1
        | Key.D1, KeyModifiers.None when not tighten ->
            Some (Msg.SetActiveLayer (Some (68, 20)))   // met1
        | Key.D2, KeyModifiers.None when not tighten ->
            Some (Msg.SetActiveLayer (Some (69, 20)))   // met2
        | Key.D3, KeyModifiers.None when not tighten ->
            Some (Msg.SetActiveLayer (Some (70, 20)))   // met3
        | Key.D4, KeyModifiers.None when not tighten ->
            Some (Msg.SetActiveLayer (Some (71, 20)))   // met4
        | Key.D0,     KeyModifiers.None
        | Key.NumPad0, KeyModifiers.None when not tighten ->
            Some (Msg.SetActiveLayer None)
        | Key.Escape, KeyModifiers.None when segmentDrag ->
            Some Msg.SegmentDragCancel
        | Key.Escape, KeyModifiers.None when routingActive ->
            Some Msg.RouteStop
        | Key.Enter, KeyModifiers.None when draftRoute ->
            Some Msg.RouteFinish
        | Key.Back, KeyModifiers.None when draftRoute ->
            Some Msg.RouteBackspace
        | Key.OemQuestion, KeyModifiers.None when draftRoute ->
            Some Msg.RouteFlipPosture
        | Key.Escape, KeyModifiers.None when tighten ->
            Some Msg.ToggleTightenMode
        | Key.Delete, KeyModifiers.None
        | Key.Back,   KeyModifiers.None ->
            Some Msg.DeleteSelection
        | k, KeyModifiers.None when tighten ->
            match k with
            | Key.D1 | Key.NumPad1 -> Some (Msg.CommitTighten 1)
            | Key.D2 | Key.NumPad2 -> Some (Msg.CommitTighten 2)
            | Key.D3 | Key.NumPad3 -> Some (Msg.CommitTighten 3)
            | Key.D4 | Key.NumPad4 -> Some (Msg.CommitTighten 4)
            | _ -> None
        | _ -> None

module private Subscriptions =

    /// Dispatch wrapper used by `Program.runWithDispatch` below. FuncUI's
    /// Elmish view-render runs on the UI thread, and its diff pass fires
    /// only when `dispatch` is called on that same thread. `Cmd.OfAsync`
    /// callbacks (used for OpenGds and RunMacro in Update.fs) otherwise
    /// dispatch from the thread pool, so the model updates without a
    /// repaint — stale UI (blank canvas, stuck buttons) until the next
    /// user-input event forces a redraw.
    ///
    /// Elmish's canonical `syncDispatch` hook solves this at the Program
    /// boundary: every `dispatch msg`, from any Cmd or any subscription,
    /// goes through this wrapper. If the caller is already on the UI
    /// thread we call inline (avoids a redundant queue round-trip);
    /// otherwise we Post and the Elmish loop runs on the UI thread as
    /// expected. Lifted from Moroder.Viz's App.fs.
    let uiDispatch (inner: Dispatch<Msg.Msg>) : Dispatch<Msg.Msg> =
        fun msg ->
            if Avalonia.Threading.Dispatcher.UIThread.CheckAccess() then
                inner msg
            else
                Avalonia.Threading.Dispatcher.UIThread.Post(fun () -> inner msg)

    /// Wraps `uiDispatch` and additionally publishes the wrapped
    /// dispatcher into `AppDispatch.current` so off-Elmish services
    /// (CommandListener) can fire Msgs through the same UI-thread
    /// marshalling path.
    let syncDispatch (inner: Dispatch<Msg.Msg>) : Dispatch<Msg.Msg> =
        let ui = uiDispatch inner
        Services.AppDispatch.current <- Some ui
        ui

/// Root Avalonia window. Bootstraps the Elmish MVU loop via FuncUI's
/// `Program.withHost` on construction, threading a live `ServiceBackend`
/// — `OpenGds` wired to `GdsLoading.load`, `RunMacro` wired to
/// `RekolektionCli.runProcess` — into `Update.update`.
type MainWindow() as this =
    inherit HostWindow()
    do
        base.Title <- "rekolektion-viz"
        // Restore window geometry from session.json when present.
        // Apply size first (always safe), then position only if the
        // saved location overlaps SOME screen — otherwise leave
        // Avalonia to centre on the primary screen.  Falls back to
        // 1400×900 / OS-chosen position on first launch or when
        // session.json lacks a `window` field.
        let saved = Services.SessionState.load ()
        match saved.Window with
        | Some w when w.Width > 0.0 && w.Height > 0.0 ->
            base.Width  <- w.Width
            base.Height <- w.Height
            // Only restore position when at least one screen contains
            // the saved top-left.  Guards against the monitor that
            // hosted the window having been disconnected between
            // sessions (otherwise the window opens off-screen and
            // looks broken).  Screen list isn't populated yet at
            // construction time on some platforms, so the check is
            // best-effort — if `Screens` is unavailable we apply the
            // position anyway and let Avalonia handle clipping.
            base.WindowStartupLocation <-
                Avalonia.Controls.WindowStartupLocation.Manual
            let onScreen =
                try
                    let screens = this.Screens
                    if isNull screens || screens.All = null
                       || screens.All.Count = 0 then
                        true  // no info → trust the saved value
                    else
                        screens.All
                        |> Seq.exists (fun s ->
                            let b = s.Bounds
                            w.X >= b.X
                            && w.Y >= b.Y
                            && w.X < b.X + b.Width
                            && w.Y < b.Y + b.Height)
                with _ -> true
            if onScreen then
                base.Position <- Avalonia.PixelPoint(w.X, w.Y)
            else
                base.WindowStartupLocation <-
                    Avalonia.Controls.WindowStartupLocation.CenterScreen
        | _ ->
            base.Width <- 1400.0
            base.Height <- 900.0
        // Save geometry on close.  `Closing` fires before the
        // window destroys itself so the bounds + position are still
        // readable.
        this.Closing.Add(fun _ ->
            let p = this.Position
            Services.SessionState.saveWindowBounds {
                Width  = this.Width
                Height = this.Height
                X      = p.X
                Y      = p.Y })

        let backend : ServiceBackend = {
            OpenGds = GdsLoading.load
            DeriveNets = GdsLoading.deriveNets
            RunMacro = fun p onLog -> async {
                let args = RekolektionCli.buildMacroArgs p
                let! exit = RekolektionCli.runProcess "rekolektion" args onLog
                return (if exit = 0 then Ok p.OutputPath else Error exit) }
            SaveMacro = fun mc -> async {
                do! Async.SwitchToThreadPool ()
                // For `.rkt` macros with a Library snapshot, route
                // saves through `Services.RoutedSave` so cells edited
                // from imported files write back to those files
                // instead of collapsing into the root document.  For
                // `.gds` / `.mag`, RoutedSave falls through to the
                // single-file `EditSession.saveTo` path below via
                // the `LibrarySnapshot = None` branch.  Either way
                // the save writes to mc.Path — the same path the
                // file was opened from.  Save As is the explicit
                // opt-in for writing somewhere else.
                match RoutedSave.saveOrSurfaceBlockers mc with
                | Ok result ->
                    // SaveCompleted expects a single root path.  For
                    // routed multi-file writes the root is mc.Path;
                    // for single-file writes WrittenPaths has one
                    // entry which IS that root.
                    let root =
                        match result.WrittenPaths with
                        | [ single ] -> single
                        | _ -> mc.Path
                    return Ok root
                | Error msg -> return Error msg }
            PersistSession = Services.SessionState.persistFromModel
        }

        // Settings load once at startup. Services.Config.current is
        // a mutable singleton the canvas + snap helpers read from;
        // future settings dialog can rewrite the file + reassign.
        Services.Config.current <- Services.Config.load ()
        let init () =
            // Seed layer visibility from the persisted session state
            // so a relaunch reopens with the same layers hidden /
            // shown the user left. SessionState.load returns the
            // entries the user explicitly toggled; everything else
            // inherits the default (visible).
            let sess = Services.SessionState.load ()
            let toggle =
                let baseToggle = Model.empty.Toggle
                sess.Layers
                |> List.fold (fun t (n, d, v, drc) ->
                    t
                    |> Rekolektion.Viz.Core.Visibility.toggleLayer (n, d) v
                    |> Rekolektion.Viz.Core.Visibility.setDrcVisibleLayer (n, d) drc
                ) baseToggle
                |> Rekolektion.Viz.Core.Visibility.setDrcVisibleOther sess.DrcOther
            // ADR-0004 — load the effective DRC ruleset at boot.
            // `loadEffectiveOrDefault` falls back to the F#-coded
            // defaults if the bundled YAML can't be found or fails
            // to parse, so the editor stays usable in either case.
            let drcView =
                Rekolektion.Viz.Core.Drc.RulesYaml.loadEffectiveOrDefault
                    "sky130" None
            // Reopen the tabs the user had at last shutdown.
            // Missing files are skipped silently (best-effort — a
            // file moved between sessions shouldn't crash the
            // launch).  Each path becomes an OpenFile message; the
            // LoadComplete handler then activates that tab.  The
            // last-active tab follows as a SetActiveMacro so focus
            // lands on the right tab once everything has loaded.
            let reopenPaths =
                sess.OpenPaths
                |> List.filter System.IO.File.Exists
            let reopenCmds =
                reopenPaths
                |> List.map (fun p -> Cmd.ofMsg (Msg.OpenFile p))
            let activateCmd =
                match sess.ActivePath with
                | Some p when List.contains p reopenPaths ->
                    [ Cmd.ofMsg (Msg.SetActiveMacro p) ]
                | _ -> []
            Services.Logger.log "session"
                {| op = "init"
                   layersFromSession = sess.Layers.Length
                   toggleLayerEntries = toggle.Layers.Count
                   savedPaths = sess.OpenPaths.Length
                   reopenedPaths = reopenPaths.Length
                   drcRules = drcView.Rules.Length
                   drcProvenanceEntries = drcView.Provenance.Count |}
            { Model.empty with
                RecentFiles = Services.Recents.load ()
                Toggle = toggle
                DrcView = drcView
                // 7 user-facing display toggles persisted across
                // restarts (snap, ruler, grid, labels, ratlines-armed,
                // DRC, dimensions). Defaults come from the SessionState
                // record so a legacy session file (pre-toggle field)
                // reads as the same default Model.empty uses.
                SnapEnabled    = sess.SnapEnabled
                ShowRuler      = sess.ShowRuler
                ShowGrid       = sess.ShowGrid
                ShowLabels     = sess.ShowLabels
                RatlinesArmed  = sess.RatlinesArmed
                ShowDrc        = sess.ShowDrc
                ShowDimensions = sess.ShowDimensions },
            Cmd.batch (reopenCmds @ activateCmd)
        let update = Update.update backend
        let view = AppView.view

        Program.mkProgram init update view
        |> Program.withHost this
        |> Program.runWithDispatch Subscriptions.syncDispatch ()

        // Window-level key handling for editor shortcuts that
        // shouldn't depend on which focusable child currently has
        // keyboard focus. KeyDown bubbles from the focused element
        // up to the window — by handling here we catch the key
        // even when focus is on a button or panel that has no
        // local handler. Routes through AppDispatch so the Elmish
        // loop owns the state transition.
        this.KeyDown.Add(fun e ->
            match KeyMap.dispatchFor
                    Services.AppDispatch.currentModel
                    e.Key e.KeyModifiers with
            | Some msg ->
                AppDispatch.send msg
                e.Handled <- true
            | None -> ())

type App() =
    inherit Application()

    override this.Initialize() =
        // Sets the application name shown in the macOS menu bar, dock
        // tooltip, and other OS chrome. Window.Title controls the
        // titlebar text; Application.Name controls the OS-level app
        // identity.
        this.Name <- "rekolektion-viz"
        this.Styles.Add(FluentTheme())
        // Viz's color vocabulary is tuned for a dark surface — force
        // the Fluent theme into dark variant rather than following the
        // OS appearance setting.
        this.RequestedThemeVariant <- ThemeVariant.Dark

    /// Build the native menu bar. On macOS this becomes the system
    /// menu (the app's menu in the screen's top bar); on Linux /
    /// Windows the same NativeMenu is rendered by NativeMenuBar
    /// inside the window. Items dispatch via AppDispatch.send so
    /// the handlers don't need to live inside the FuncUI tree.
    member private _.BuildNativeMenu (window: Window) : NativeMenu =
        let menu = NativeMenu()

        let fileItem = NativeMenuItem("File")
        let fileSub = NativeMenu()

        let openItem = NativeMenuItem("Open...")
        openItem.Gesture <- KeyGesture(Key.O, KeyModifiers.Meta)
        openItem.Click.Add(fun _ ->
            FilePickers.dispatchOpen (window :> obj) AppDispatch.send)
        fileSub.Items.Add(openItem)

        // Recent files. The submenu is rebuilt whenever the model's
        // RecentFiles list changes (Services.Recents publishes from
        // AppView render). Empty list shows a disabled placeholder.
        let recentItem = NativeMenuItem("Open Recent")
        let recentSub = NativeMenu()
        recentItem.Menu <- recentSub
        let rebuildRecents (paths: string list) =
            Avalonia.Threading.Dispatcher.UIThread.Post(fun () ->
                recentSub.Items.Clear()
                if List.isEmpty paths then
                    let empty = NativeMenuItem("(none)")
                    empty.IsEnabled <- false
                    recentSub.Items.Add(empty)
                else
                    for p in paths do
                        let label = System.IO.Path.GetFileName p
                        let mi = NativeMenuItem(label)
                        mi.ToolTip <- p
                        mi.Click.Add(fun _ ->
                            AppDispatch.send (Msg.RecentFileClicked p))
                        recentSub.Items.Add(mi))
        Services.Recents.subscribe rebuildRecents
        fileSub.Items.Add(recentItem)

        let runItem = NativeMenuItem("Run macro...")
        runItem.Click.Add(fun _ ->
            FilePickers.dispatchRunMacro (window :> obj) AppDispatch.send)
        fileSub.Items.Add(runItem)

        fileSub.Items.Add(NativeMenuItemSeparator())

        let reloadItem = NativeMenuItem("Reload")
        reloadItem.Gesture <- KeyGesture(Key.R, KeyModifiers.Meta)
        reloadItem.Click.Add(fun _ ->
            AppDispatch.send Msg.ReloadActiveMacro)
        fileSub.Items.Add(reloadItem)

        // Routed-save orchestration: when the active macro has a
        // Library snapshot, plan the save synchronously, then drive
        // conflict / orphan / multi-file dialogs as needed before
        // executing the write. Non-routable saves (no snapshot)
        // fall through to the existing single-file dispatch.
        let runRoutedSave () : Async<unit> = async {
            match AppDispatch.currentModel with
            | None ->
                AppDispatch.send Msg.SaveActiveMacro
            | Some model ->
                match Model.activeMacro model with
                | None -> AppDispatch.send Msg.SaveActiveMacro
                | Some mc ->
                    match Services.RoutedSave.plan mc Map.empty with
                    | None ->
                        // Non-`.rkt` — keep the existing path.
                        AppDispatch.send Msg.SaveActiveMacro
                    | Some plan ->
                        // 1. Conflict gate.
                        let! continueAfter =
                            if List.isEmpty plan.Conflicts then async { return true }
                            else
                                let paths =
                                    plan.Conflicts
                                    |> List.choose (function
                                        | Rekolektion.Viz.Core.Rkt.SaveRouter.MtimeConflict (p, _, _) -> Some p
                                        | _ -> None)
                                async {
                                    let dlg = SaveDialogs.ConflictDialog()
                                    let! choice = dlg.ShowAsync window paths
                                    match choice with
                                    | SaveDialogs.OverwriteAll -> return true
                                    | SaveDialogs.ReloadAndReapply ->
                                        // Three-way merge: reload the
                                        // on-disk version and re-apply
                                        // the user's pending edits on
                                        // top. Conflicting cells (user
                                        // + disk both touched) keep the
                                        // user's version; their names
                                        // are logged.
                                        match Services.RoutedSave.reloadAndReapply mc with
                                        | Error msg ->
                                            AppDispatch.send (Msg.SaveFailed msg)
                                        | Ok r ->
                                            AppDispatch.send
                                                (Msg.ReplaceActiveMacro (r.Macro, r.ConflictingCells))
                                        return false
                                    | SaveDialogs.CancelConflict -> return false
                                }
                        if not continueAfter then return () else
                        // 2. Orphan gate. The dialog returns a
                        // cellName → targetPath map; we re-plan with
                        // it so projectIntoLibrary routes the orphans
                        // to the user-chosen files instead of the
                        // default root fallback. Cells whose chosen
                        // target isn't a loaded file silently fall
                        // back to the root (see SaveRouter docs).
                        let! orphanAssignments =
                            if List.isEmpty plan.Orphans then
                                async { return Some Map.empty }
                            else async {
                                let dlg = SaveDialogs.OrphanDialog()
                                let! result =
                                    dlg.ShowAsync window plan.Orphans mc.Path
                                match result with
                                | SaveDialogs.AssignTargets m -> return Some m
                                | SaveDialogs.CancelOrphan -> return None
                            }
                        match orphanAssignments with
                        | None -> return ()
                        | Some assignments ->
                        // Re-plan with the assignments so diffs
                        // reflect the user-chosen orphan targets.
                        let plan =
                            match Services.RoutedSave.plan mc assignments with
                            | Some p -> p
                            | None -> plan  // unreachable: snapshot still Some
                        // 3. Multi-file gate (skip when ≤1 file).
                        let! selected =
                            if plan.Diffs.Count <= 1 then async {
                                return
                                    plan.Diffs
                                    |> Map.toSeq |> Seq.map fst |> Set.ofSeq }
                            else async {
                                let entries =
                                    plan.Diffs
                                    |> Map.toSeq
                                    |> Seq.map (fun (p, doc) ->
                                        p, doc.Cells.Length)
                                    |> List.ofSeq
                                let dlg = SaveDialogs.MultiFileSaveDialog()
                                let! result = dlg.ShowAsync window entries
                                match result with
                                | SaveDialogs.SaveSelected s -> return s
                                | SaveDialogs.Cancelled -> return Set.empty
                            }
                        if Set.isEmpty selected && not (Map.isEmpty plan.Diffs) then
                            // User cancelled the multi-file dialog.
                            return ()
                        else
                            let narrowed =
                                plan.Diffs
                                |> Map.filter (fun p _ -> Set.contains p selected)
                            do! Async.SwitchToThreadPool ()
                            match Services.RoutedSave.execute narrowed with
                            | Ok _ -> AppDispatch.send (Msg.SaveCompleted mc.Path)
                            | Error msg -> AppDispatch.send (Msg.SaveFailed msg)
        }
        let saveItem = NativeMenuItem("Save")
        saveItem.Gesture <- KeyGesture(Key.S, KeyModifiers.Meta)
        saveItem.Click.Add(fun _ ->
            runRoutedSave () |> Async.StartImmediate)
        fileSub.Items.Add(saveItem)

        // Save-As-with-reroot: when the active macro has a Library
        // snapshot with ≥2 files, Save As to a different directory
        // needs to mirror the import tree into the new location.
        // Single-file macros fall through to the legacy SaveAs path.
        let runRoutedSaveAs (targetPath: string) : Async<unit> = async {
            match AppDispatch.currentModel with
            | None -> AppDispatch.send (Msg.SaveActiveMacroAs targetPath)
            | Some model ->
                match Model.activeMacro model with
                | None -> AppDispatch.send (Msg.SaveActiveMacroAs targetPath)
                | Some mc ->
                    match mc.LibrarySnapshot with
                    | None ->
                        AppDispatch.send (Msg.SaveActiveMacroAs targetPath)
                    | Some snapshot when Map.count snapshot.Documents <= 1 ->
                        AppDispatch.send (Msg.SaveActiveMacroAs targetPath)
                    | Some snapshot ->
                        let srcRootDir =
                            let raw = System.IO.Path.GetDirectoryName mc.Path
                            if System.String.IsNullOrEmpty raw then "."
                            else System.IO.Path.GetFullPath raw
                        let dstRootDir =
                            let raw = System.IO.Path.GetDirectoryName targetPath
                            if System.String.IsNullOrEmpty raw then "."
                            else System.IO.Path.GetFullPath raw
                        if srcRootDir = dstRootDir then
                            // Same directory — Save As is really a
                            // rename within the same import graph;
                            // no reroot needed.
                            AppDispatch.send (Msg.SaveActiveMacroAs targetPath)
                        else
                            // Compute mirror: each source file's
                            // relative path from srcRootDir maps to
                            // the same relative path under dstRootDir.
                            // The root file specifically targets
                            // `targetPath` (the user-chosen name).
                            let mapping =
                                snapshot.Documents
                                |> Map.toSeq
                                |> Seq.map (fun (srcPath, _) ->
                                    let mapped =
                                        if srcPath = System.IO.Path.GetFullPath mc.Path then
                                            targetPath
                                        else
                                            let rel =
                                                System.IO.Path.GetRelativePath(
                                                    srcRootDir, srcPath)
                                            System.IO.Path.GetFullPath(
                                                System.IO.Path.Combine(dstRootDir, rel))
                                    srcPath, mapped)
                                |> List.ofSeq
                            let dlg = SaveDialogs.SaveAsRerootDialog()
                            let! result = dlg.ShowAsync window mapping
                            match result with
                            | SaveDialogs.CancelReroot -> return ()
                            | SaveDialogs.ProceedReroot mappingMap ->
                                // Build per-file diffs from the
                                // mapped Library, then write each
                                // file at its mapped target.
                                let projected =
                                    Rekolektion.Viz.Core.Rkt.SaveRouter.projectIntoLibrary
                                        snapshot mc.Document
                                        (System.IO.Path.GetFullPath mc.Path)
                                        Map.empty
                                let remappedDiffs =
                                    projected.Documents
                                    |> Map.toSeq
                                    |> Seq.choose (fun (srcPath, ld) ->
                                        match Map.tryFind srcPath mappingMap with
                                        | Some dst -> Some (dst, ld.Ast)
                                        | None -> None)
                                    |> Map.ofSeq
                                do! Async.SwitchToThreadPool ()
                                match Services.RoutedSave.execute remappedDiffs with
                                | Ok _ ->
                                    AppDispatch.send (Msg.SaveCompleted targetPath)
                                | Error msg ->
                                    AppDispatch.send (Msg.SaveFailed msg)
        }
        let saveAsItem = NativeMenuItem("Save As...")
        saveAsItem.Gesture <-
            KeyGesture(Key.S, KeyModifiers.Meta ||| KeyModifiers.Shift)
        saveAsItem.Click.Add(fun _ ->
            // Use the latest known active path as the picker's
            // suggested location; falls back to "" if no macro is
            // open (the picker will start at the platform default).
            let suggested = AppDispatch.currentActivePath |> Option.defaultValue ""
            // Hook the picker's dispatch so Save-As targets can pass
            // through `runRoutedSaveAs` instead of going straight to
            // Msg.SaveActiveMacroAs.
            FilePickers.dispatchSaveAs (window :> obj) suggested (fun msg ->
                match msg with
                | Msg.SaveActiveMacroAs target ->
                    runRoutedSaveAs target |> Async.StartImmediate
                | other -> AppDispatch.send other))
        fileSub.Items.Add(saveAsItem)

        let closeItem = NativeMenuItem("Close tab")
        closeItem.Gesture <- KeyGesture(Key.W, KeyModifiers.Meta)
        closeItem.Click.Add(fun _ ->
            AppDispatch.send Msg.CloseActiveTab)
        fileSub.Items.Add(closeItem)

        fileItem.Menu <- fileSub
        menu.Items.Add(fileItem)

        // ── Edit menu ─────────────────────────────────────────────
        // Selection ops. Every keymap binding that mutates the
        // active macro lives here so users can discover the hot
        // key alongside the command name.
        let editItem = NativeMenuItem("Edit")
        let editSub = NativeMenu()
        let addItem
                (parent: NativeMenu)
                (label: string)
                (gestureOpt: KeyGesture option)
                (msg: Msg.Msg) : NativeMenuItem =
            let mi = NativeMenuItem(label)
            match gestureOpt with
            | Some g -> mi.Gesture <- g
            | None -> ()
            mi.Click.Add(fun _ -> AppDispatch.send msg)
            parent.Items.Add(mi)
            mi
        let addSeparator (parent: NativeMenu) =
            parent.Items.Add(NativeMenuItemSeparator())
        // Undo / Redo migrated from File menu — macOS convention.
        addItem editSub "Undo"
            (Some (KeyGesture(Key.Z, KeyModifiers.Meta)))
            Msg.UndoActiveMacro |> ignore
        addItem editSub "Redo"
            (Some (KeyGesture(Key.Z, KeyModifiers.Meta ||| KeyModifiers.Shift)))
            Msg.RedoActiveMacro |> ignore
        addSeparator editSub
        addItem editSub "Duplicate selection"
            (Some (KeyGesture(Key.D, KeyModifiers.Meta)))
            Msg.DuplicateSelection |> ignore
        addItem editSub "Delete selection"
            (Some (KeyGesture(Key.Delete, KeyModifiers.None)))
            Msg.DeleteSelection |> ignore
        addSeparator editSub
        addItem editSub "Rotate 90° CCW"
            (Some (KeyGesture(Key.Space, KeyModifiers.None)))
            Msg.RotateSelection90 |> ignore
        addItem editSub "Mirror about X axis (flip Y)"
            (Some (KeyGesture(Key.X, KeyModifiers.None)))
            Msg.MirrorSelectionX |> ignore
        addItem editSub "Mirror about Y axis (flip X)"
            (Some (KeyGesture(Key.Y, KeyModifiers.None)))
            Msg.MirrorSelectionY |> ignore
        addSeparator editSub
        addItem editSub "Tidy duplicate routing geometry"
            None
            Msg.TidyRoutingGeometry |> ignore
        editItem.Menu <- editSub
        menu.Items.Add(editItem)

        // ── View menu ─────────────────────────────────────────────
        // Display toggles (no model mutation).
        let viewItem = NativeMenuItem("View")
        let viewSub = NativeMenu()
        addItem viewSub "Toggle dimensions"
            (Some (KeyGesture(Key.D, KeyModifiers.None)))
            Msg.ToggleDimensions |> ignore
        addItem viewSub "Toggle DRC overlay"
            (Some (KeyGesture(Key.R, KeyModifiers.None)))
            Msg.ToggleDrc |> ignore
        addItem viewSub "Toggle walkaround debug overlay"
            (Some (KeyGesture(Key.O, KeyModifiers.None)))
            Msg.ToggleDebugOverlay |> ignore
        addItem viewSub "Toggle ratlines"
            (Some (KeyGesture(Key.U, KeyModifiers.None)))
            Msg.ToggleRatlines |> ignore
        addItem viewSub "Toggle ruler"
            (Some (KeyGesture(Key.L, KeyModifiers.None)))
            Msg.ToggleRuler |> ignore
        addItem viewSub "Toggle grid"
            (Some (KeyGesture(Key.G, KeyModifiers.None)))
            Msg.ToggleGrid |> ignore
        addItem viewSub "Toggle snap"
            (Some (KeyGesture(Key.S, KeyModifiers.None)))
            Msg.ToggleSnap |> ignore
        addItem viewSub "Toggle labels"
            None
            Msg.ToggleLabels |> ignore
        addSeparator viewSub
        addItem viewSub "Toggle log pane"
            None
            Msg.ToggleLogPane |> ignore
        viewItem.Menu <- viewSub
        menu.Items.Add(viewItem)

        // ── Mode menu ─────────────────────────────────────────────
        // Tool / interaction modes.
        let modeItem = NativeMenuItem("Mode")
        let modeSub = NativeMenu()
        addItem modeSub "Wire (draw routes)"
            (Some (KeyGesture(Key.W, KeyModifiers.None)))
            Msg.ToggleRoutingMode |> ignore
        addItem modeSub "Edit routing (segment gizmos)"
            (Some (KeyGesture(Key.E, KeyModifiers.None)))
            Msg.ToggleEditRoutingMode |> ignore
        addItem modeSub "Tighten (numbered candidates)"
            (Some (KeyGesture(Key.T, KeyModifiers.None)))
            Msg.ToggleTightenMode |> ignore
        modeItem.Menu <- modeSub
        menu.Items.Add(modeItem)

        // ── Layer focus menu ──────────────────────────────────────
        // Number-row shortcuts that focus a routing layer (dims
        // others). The Layer menu lets users discover the binding
        // when they don't know it.
        let layerItem = NativeMenuItem("Layer focus")
        let layerSub = NativeMenu()
        addItem layerSub "li1"
            (Some (KeyGesture(Key.OemTilde, KeyModifiers.None)))
            (Msg.SetActiveLayer (Some (67, 20))) |> ignore
        addItem layerSub "met1"
            (Some (KeyGesture(Key.D1, KeyModifiers.None)))
            (Msg.SetActiveLayer (Some (68, 20))) |> ignore
        addItem layerSub "met2"
            (Some (KeyGesture(Key.D2, KeyModifiers.None)))
            (Msg.SetActiveLayer (Some (69, 20))) |> ignore
        addItem layerSub "met3"
            (Some (KeyGesture(Key.D3, KeyModifiers.None)))
            (Msg.SetActiveLayer (Some (70, 20))) |> ignore
        addItem layerSub "met4"
            (Some (KeyGesture(Key.D4, KeyModifiers.None)))
            (Msg.SetActiveLayer (Some (71, 20))) |> ignore
        addItem layerSub "Clear focus (show all layers)"
            (Some (KeyGesture(Key.D0, KeyModifiers.None)))
            (Msg.SetActiveLayer None) |> ignore
        layerItem.Menu <- layerSub
        menu.Items.Add(layerItem)

        menu

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            let mainWindow = MainWindow()
            desktop.MainWindow <- mainWindow

            // Attach the native menu so macOS shows it in the system
            // menu bar; on other platforms NativeMenuBar in the
            // window's top row will read this same menu.
            let nativeMenu = this.BuildNativeMenu mainWindow
            // Setting the NativeMenu on the main Window is enough for
            // Avalonia's macOS backend to export it as the system
            // menu bar — no separate "export" call is needed in
            // Avalonia 11.x.
            NativeMenu.SetMenu(mainWindow, nativeMenu)

            // Skipped in headless mode: `rekolektion viz-render` boots
            // the exact same App to render one PNG and exit, but must
            // not bind (or worse, tear down on exit) the live Viz
            // socket used by a human-run `rekolektion viz`.
            // `REKOLEKTION_VIZ_HEADLESS=1` is set by HeadlessRender
            // before SetupWithoutStarting.
            let isHeadless =
                let v = Environment.GetEnvironmentVariable "REKOLEKTION_VIZ_HEADLESS"
                not (String.IsNullOrEmpty v) && v <> "0"

            if not isHeadless then
                // Compute the screenshot/command socket path. Honours
                // the `REKOLEKTION_VIZ_SOCKET` env var so v1 and v2 (or
                // any other parallel instance) can bind distinct sockets;
                // defaults to ~/.rekolektion/viz.sock. Ensure the parent
                // directory exists and stale-cleanup any leftover socket
                // file from a previous run that didn't shut down cleanly.
                let sockPath =
                    let env = Environment.GetEnvironmentVariable "REKOLEKTION_VIZ_SOCKET"
                    if not (String.IsNullOrWhiteSpace env) then env
                    else
                        let rekoDir =
                            Path.Combine(
                                Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
                                ".rekolektion")
                        Path.Combine(rekoDir, "viz.sock")
                let sockDir = Path.GetDirectoryName sockPath
                if not (String.IsNullOrEmpty sockDir) && not (Directory.Exists sockDir) then
                    Directory.CreateDirectory sockDir |> ignore
                // Bind the screenshot listener on the project-scoped
                // viz socket so the MCP `rekolektion_viz_screenshot`
                // tool can fetch a PNG of the running window.
                // ScreenshotListener.start does its own stale-socket
                // cleanup before bind, so a leftover viz.sock from a
                // previous crashed run doesn't block this listener.
                // The listener routes by HTTP method+path: GET serves
                // a PNG screenshot; POST delegates to CommandListener
                // for agent-driven Msg dispatch (open file, toggle
                // layer/net, highlight net, switch tab). Both share
                // the same viz.sock — only one UDS listener per path.
                let screenshotHandle =
                    ScreenshotListener.start
                        sockPath
                        (fun () -> Some (mainWindow :> Avalonia.Controls.TopLevel))
                        AppDispatch.send
                desktop.Exit.Add(fun _ -> screenshotHandle.Dispose())
        | _ -> ()
        base.OnFrameworkInitializationCompleted()
