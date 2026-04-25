namespace Rekolektion.Viz.App

open System
open System.IO
open System.Threading
open Avalonia
open Avalonia.Headless
open Avalonia.Threading

/// Subclass of `App` that exposes a `BuildAvaloniaApp` static method.
/// `HeadlessUnitTestSession.StartNew` reflects for that method on the
/// app type and uses its returned AppBuilder instead of the default
/// (which leaves UseHeadlessDrawing=true, breaking CaptureRenderedFrame).
/// Same App behavior, just with the Skia-backed headless platform.
type HeadlessApp() =
    inherit App()

    static member BuildAvaloniaApp () : AppBuilder =
        AppBuilder
            .Configure<HeadlessApp>()
            .UseSkia()
            .UseHeadless(
                AvaloniaHeadlessPlatformOptions(UseHeadlessDrawing = false))

module HeadlessRender =

    /// Boot the Rekolektion.Viz Avalonia app under Avalonia.Headless and
    /// render the MainWindow to a PNG on disk — no on-screen window, no
    /// human interaction required. Designed for one-shot CLI invocation
    /// via `rekolektion viz-render --output <path>`, and for the MCP
    /// `rekolektion_viz_render` tool so agents can inspect the UI layout
    /// without a live Viz process.
    ///
    /// Width/height are the logical pixel size the window is measured
    /// at. Defaults (1400×900) match a typical Viz screenshot size;
    /// callers choosing specific sizes can pass them explicitly.
    ///
    /// Uses `HeadlessApp.BuildAvaloniaApp` (declared above) to get a
    /// Skia-backed platform with UseHeadlessDrawing=false, which is
    /// what CaptureRenderedFrame requires for real pixel output.
    ///
    /// Sets `REKOLEKTION_VIZ_HEADLESS=1` before boot so App.fs skips binding
    /// the ScreenshotListener socket — otherwise this would fight with a
    /// live `rekolektion viz` process for the same unix socket file.
    let renderToPng
            (outputPath: string)
            (width: int)
            (height: int)
            (holdMs: int) : int =
        Environment.SetEnvironmentVariable("REKOLEKTION_VIZ_HEADLESS", "1")

        use session = HeadlessUnitTestSession.StartNew(typeof<HeadlessApp>)
        let task =
            session.Dispatch((fun () ->
                // Avalonia.Headless' ApplicationLifetime is NOT a classic
                // desktop lifetime, so App.OnFrameworkInitializationCompleted's
                // `match :? IClassicDesktopStyleApplicationLifetime` branch
                // never fires and MainWindow is never auto-created. We
                // construct it directly — same type the classic lifetime
                // would have created, same Elmish/FuncUI wiring.
                let window = MainWindow()
                window.Width  <- float width
                window.Height <- float height
                window.Show()
                // Pump dispatcher frames so initial layout, Elmish init,
                // and any async data subscriptions get a chance to render.
                // HoldMs is caller-tunable: async content can take a few
                // hundred ms on cold start.
                let swHold = System.Diagnostics.Stopwatch.StartNew()
                while swHold.ElapsedMilliseconds < int64 holdMs do
                    Dispatcher.UIThread.RunJobs()
                    Thread.Sleep 16
                let frame = window.CaptureRenderedFrame()
                if isNull (frame :> obj) then
                    failwith "CaptureRenderedFrame returned null"
                let dir = Path.GetDirectoryName outputPath
                if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
                    Directory.CreateDirectory dir |> ignore
                frame.Save outputPath), CancellationToken.None)
        task.GetAwaiter().GetResult()
        0
