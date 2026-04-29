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
        // Valid fixture shouldn't surface a sidecar error.
        loaded.SidecarError |> should equal (None: string option)

[<Fact>]
let ``GdsLoading.load surfaces malformed sidecar via SidecarError`` () =
    // Copy the fixture GDS to a temp path and write a corrupt .nets.json
    // beside it. Loader has to find the corrupt sidecar (not the real
    // fixture's sidecar), so we use a tmp dir.
    let tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory tmpDir |> ignore
    try
        let gdsPath = Path.Combine(tmpDir, "x.gds")
        File.Copy(fixturePath "bitcell_lr.gds", gdsPath)
        File.WriteAllText(Path.ChangeExtension(gdsPath, ".nets.json"),
                          """{"version":1, "macro": broken""")
        let result = GdsLoading.load gdsPath |> Async.RunSynchronously
        match result with
        | Error e -> failwithf "expected Ok with SidecarError; got Error %s" e
        | Ok loaded ->
            loaded.NetsFromSidecar |> should equal false
            loaded.SidecarError.IsSome |> should equal true
    finally
        Directory.Delete(tmpDir, true)
