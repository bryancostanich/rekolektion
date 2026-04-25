namespace Rekolektion.Viz.App

open System
open System.IO
open Avalonia
open Avalonia.Controls.ApplicationLifetimes
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
module AppDispatch =
    let mutable current : (Msg.Msg -> unit) option = None
    let send (msg: Msg.Msg) : unit =
        match current with
        | Some d -> d msg
        | None   -> ()

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
        AppDispatch.current <- Some ui
        ui

/// Root Avalonia window. Bootstraps the Elmish MVU loop via FuncUI's
/// `Program.withHost` on construction, threading a live `ServiceBackend`
/// — `OpenGds` wired to `GdsLoading.load`, `RunMacro` wired to
/// `RekolektionCli.runProcess` — into `Update.update`.
type MainWindow() as this =
    inherit HostWindow()
    do
        base.Title <- "rekolektion-viz"
        base.Width <- 1400.0
        base.Height <- 900.0

        let backend : ServiceBackend = {
            OpenGds = GdsLoading.load
            RunMacro = fun p onLog -> async {
                let args = RekolektionCli.buildMacroArgs p
                let! exit = RekolektionCli.runProcess "rekolektion" args onLog
                return (if exit = 0 then Ok p.OutputPath else Error exit) }
        }

        let init () = Model.empty, Cmd.none
        let update = Update.update backend
        let view = AppView.view

        Program.mkProgram init update view
        |> Program.withHost this
        |> Program.runWithDispatch Subscriptions.syncDispatch ()

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

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            let mainWindow = MainWindow()
            desktop.MainWindow <- mainWindow

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
                // Compute the screenshot/command socket path under
                // ~/.rekolektion/viz.sock. Ensure the parent directory
                // exists and stale-cleanup any leftover socket file
                // from a previous run that didn't shut down cleanly.
                let rekoDir =
                    Path.Combine(
                        Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
                        ".rekolektion")
                if not (Directory.Exists rekoDir) then
                    Directory.CreateDirectory rekoDir |> ignore
                let sockPath = Path.Combine(rekoDir, "viz.sock")
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
