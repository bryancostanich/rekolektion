module Rekolektion.Viz.Core.Tests.SidecarLoaderTests

open System.IO
open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Sidecar

let private fixturePath name =
    Path.Combine(System.AppContext.BaseDirectory, "testdata", name)

[<Fact>]
let ``Loader.load returns Ok Some for valid sidecar`` () =
    match Loader.load (fixturePath "bitcell_lr.nets.json") with
    | Ok (Some sc) ->
        sc.Version |> should equal 1
        sc.Macro |> should equal "sky130_sram_6t_bitcell_lr"
        sc.Nets.Count |> should be (greaterThanOrEqualTo 3)
    | Ok None -> failwith "expected Some sidecar; got Ok None"
    | Error msg -> failwithf "expected Ok Some; got Error %s" msg

[<Fact>]
let ``Loader.load returns Ok None for missing file`` () =
    match Loader.load "/tmp/does-not-exist.nets.json" with
    | Ok None -> ()
    | Ok (Some _) -> failwith "expected Ok None for missing file; got Ok Some"
    | Error msg -> failwithf "expected Ok None; got Error %s" msg

[<Fact>]
let ``Sidecar exposes power class for VPWR`` () =
    match Loader.load (fixturePath "bitcell_lr.nets.json") with
    | Ok (Some sc) ->
        sc.Nets.["VPWR"].Class |> should equal Types.NetClass.Power
    | _ -> failwith "expected Ok Some"

[<Fact>]
let ``Loader.load returns Error for unsupported version`` () =
    let tmp = Path.GetTempFileName()
    File.WriteAllText(tmp, """{"version":2,"macro":"future","nets":{}}""")
    try
        match Loader.load tmp with
        | Error msg ->
            msg |> should haveSubstring "version"
        | other -> failwithf "expected Error; got %A" other
    finally
        File.Delete tmp

[<Fact>]
let ``Loader.load returns Error for malformed JSON`` () =
    let tmp = Path.GetTempFileName()
    File.WriteAllText(tmp, """{"version":1, "macro":"oops", "nets": { broken """)
    try
        match Loader.load tmp with
        | Error _ -> ()
        | other -> failwithf "expected Error for malformed JSON; got %A" other
    finally
        File.Delete tmp

[<Fact>]
let ``Loader.load returns Error for missing required field`` () =
    let tmp = Path.GetTempFileName()
    // Missing the "macro" property.
    File.WriteAllText(tmp, """{"version":1,"nets":{}}""")
    try
        match Loader.load tmp with
        | Error _ -> ()
        | other -> failwithf "expected Error for missing field; got %A" other
    finally
        File.Delete tmp

[<Fact>]
let ``Loader.load returns Error for wrong field type`` () =
    let tmp = Path.GetTempFileName()
    // version should be an integer, not a string.
    File.WriteAllText(tmp, """{"version":"1","macro":"x","nets":{}}""")
    try
        match Loader.load tmp with
        | Error _ -> ()
        | other -> failwithf "expected Error for wrong field type; got %A" other
    finally
        File.Delete tmp
