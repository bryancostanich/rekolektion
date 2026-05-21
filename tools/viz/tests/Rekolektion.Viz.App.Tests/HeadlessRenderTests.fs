module Rekolektion.Viz.App.Tests.HeadlessRenderTests

open System.IO
open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.App

[<Fact>]
let ``Headless render of empty MainWindow produces non-empty PNG`` () =
    let outPath = Path.GetTempFileName() + ".png"
    try
        // Share the assembly-wide Avalonia.Headless session so this
        // test composes with the CanvasWiringTests (only one session
        // per process; see TestSession.fs).
        let session = TestSession.instance.Value
        let exitCode =
            HeadlessRender.renderToPngWithSession session outPath 800 600 1500 []
        exitCode |> should equal 0
        let bytes = File.ReadAllBytes outPath
        bytes.Length |> should be (greaterThan 1000)
    finally
        if File.Exists outPath then File.Delete outPath
