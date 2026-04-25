module Rekolektion.Viz.App.Tests.GdsLoadingTests

open System.IO
open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.App.Services

let private fixturePath name =
    Path.Combine(System.AppContext.BaseDirectory, "testdata", name)

[<Fact>]
let ``GdsLoading.load opens bitcell_lr fixture`` () =
    let path = fixturePath "bitcell_lr.gds"
    let result = GdsLoading.load path |> Async.RunSynchronously
    match result with
    | Error e -> failwithf "expected Ok, got Error %s" e
    | Ok loaded ->
        loaded.Library.Structures |> List.isEmpty |> should equal false
        let sidecarExists = File.Exists (Path.ChangeExtension(path, ".nets.json"))
        loaded.NetsFromSidecar |> should equal sidecarExists
