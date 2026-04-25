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
        |> Program.runWithDispatch Subscriptions.uiDispatch ()

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
                let screenshotHandle =
                    ScreenshotListener.start sockPath (fun () ->
                        Some (mainWindow :> Avalonia.Controls.TopLevel))
                desktop.Exit.Add(fun _ -> screenshotHandle.Dispose())
                // TODO(Task 24): wire up the CommandListener for
                // agent-driven Msg dispatch on a sibling socket
                // (CommandListener can't share viz.sock — only one
                // listener per UDS path).
        | _ -> ()
        base.OnFrameworkInitializationCompleted()
