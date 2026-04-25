module Rekolektion.Viz.App.Tests.HeadlessRenderTests

open System.IO
open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.App

[<Fact>]
let ``Headless render of empty MainWindow produces non-empty PNG`` () =
    let outPath = Path.GetTempFileName() + ".png"
    try
        let exitCode = HeadlessRender.renderToPng outPath 800 600 1500 []
        exitCode |> should equal 0
        let bytes = File.ReadAllBytes outPath
        bytes.Length |> should be (greaterThan 1000)
    finally
        if File.Exists outPath then File.Delete outPath
