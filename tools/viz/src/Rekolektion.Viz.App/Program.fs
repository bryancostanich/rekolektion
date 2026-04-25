/// Avalonia desktop bootstrap for the rekolektion-viz GUI.
/// Called from the Cli's `app` subcommand. Kept tiny on purpose:
/// the heavy lifting (Elmish wiring, MainWindow, listeners) lives
/// in App.fs / HeadlessRender.fs; this module is just the
/// classic-desktop-lifetime entry point.
module Rekolektion.Viz.App.Program

open Avalonia

let private buildAvaloniaApp () =
    AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace()

/// Boot the Avalonia desktop application and run the classic
/// desktop lifetime to completion. Returns the lifetime's exit
/// code so the CLI can propagate it.
let runDesktop (argv: string[]) : int =
    buildAvaloniaApp().StartWithClassicDesktopLifetime(argv)
