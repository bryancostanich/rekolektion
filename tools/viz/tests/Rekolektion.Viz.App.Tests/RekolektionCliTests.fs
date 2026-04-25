module Rekolektion.Viz.App.Tests.RekolektionCliTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.App.Services

[<Fact>]
let ``runProcess captures stdout to log lines and exit code`` () =
    let lines = ResizeArray<string>()
    let exitCode =
        RekolektionCli.runProcess "/bin/sh" ["-c"; "echo first; echo second"] (fun l -> lines.Add l)
        |> Async.RunSynchronously
    exitCode |> should equal 0
    lines |> should contain "first"
    lines |> should contain "second"

[<Fact>]
let ``runProcess returns non-zero for failing process`` () =
    let exitCode =
        RekolektionCli.runProcess "/bin/sh" ["-c"; "exit 7"] (fun _ -> ())
        |> Async.RunSynchronously
    exitCode |> should equal 7
