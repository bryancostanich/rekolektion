module Rekolektion.Viz.Core.Tests.SidecarLoaderTests

open System.IO
open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Sidecar

let private fixturePath name =
    Path.Combine(System.AppContext.BaseDirectory, "testdata", name)

[<Fact>]
let ``Loader.load returns Some for valid sidecar`` () =
    match Loader.load (fixturePath "bitcell_lr.nets.json") with
    | Some sc ->
        sc.Version |> should equal 1
        sc.Macro |> should equal "sky130_sram_6t_bitcell_lr"
        sc.Nets.Count |> should be (greaterThanOrEqualTo 3)
    | None -> failwith "expected Some"

[<Fact>]
let ``Loader.load returns None for missing file`` () =
    Loader.load "/tmp/does-not-exist.nets.json" |> should equal (None: Types.Sidecar option)

[<Fact>]
let ``Sidecar exposes power class for VPWR`` () =
    match Loader.load (fixturePath "bitcell_lr.nets.json") with
    | Some sc ->
        sc.Nets.["VPWR"].Class |> should equal Types.NetClass.Power
    | None -> failwith "expected Some"

[<Fact>]
let ``Loader.load returns None for unsupported version`` () =
    let tmp = Path.GetTempFileName()
    File.WriteAllText(tmp, """{"version":2,"macro":"future","nets":{}}""")
    try
        Loader.load tmp |> should equal (None: Types.Sidecar option)
    finally
        File.Delete tmp
