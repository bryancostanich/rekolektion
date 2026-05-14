module Rekolektion.Viz.Core.Tests.GdsWriterTests

open System.IO
open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Gds.Types

let private withTmp (f: string -> unit) =
    let p = Path.Combine(Path.GetTempPath(),
                         "viz-gds-rt-" + System.Guid.NewGuid().ToString("N") + ".gds")
    try f p
    finally if File.Exists p then File.Delete p

let private mkRect (x1, y1, x2, y2) (layer, dt) : Element =
    Boundary {
        Layer = layer
        DataType = dt
        Points = [
            { X = int64 x1; Y = int64 y1 }
            { X = int64 x2; Y = int64 y1 }
            { X = int64 x2; Y = int64 y2 }
            { X = int64 x1; Y = int64 y2 }
            { X = int64 x1; Y = int64 y1 }
        ]
    }

[<Fact>]
let ``write then read recovers a single-rect library`` () =
    let lib : Library = {
        Name = "test"
        UserUnitsPerDbUnit = 0.001
        DbUnitsInMeters = 1e-9
        Structures = [
            { Name = "top"
              Elements = [ mkRect (0, 0, 100, 200) (68, 20) ] }
        ]
    }
    withTmp (fun path ->
        Gds.Writer.writeGds path lib
        let roundTrip = Gds.Reader.readGdsLibrary path
        roundTrip.Name |> should equal "test"
        roundTrip.Structures.Length |> should equal 1
        let s = roundTrip.Structures.[0]
        s.Name |> should equal "top"
        s.Elements.Length |> should equal 1
        match s.Elements.[0] with
        | Boundary b ->
            b.Layer |> should equal 68
            b.DataType |> should equal 20
            b.Points.Length |> should equal 5
        | _ -> failwith "expected Boundary")

[<Fact>]
let ``write then read recovers a library with SRef`` () =
    let lib : Library = {
        Name = "test"
        UserUnitsPerDbUnit = 0.001
        DbUnitsInMeters = 1e-9
        Structures = [
            { Name = "child"
              Elements = [ mkRect (0, 0, 50, 50) (66, 20) ] }
            { Name = "top"
              Elements = [
                  SRef {
                      StructureName = "child"
                      Origin = { X = 100L; Y = 200L }
                      Mag = 1.0
                      Angle = 0.0
                      Reflected = false
                  }
              ] }
        ]
    }
    withTmp (fun path ->
        Gds.Writer.writeGds path lib
        let rt = Gds.Reader.readGdsLibrary path
        rt.Structures.Length |> should equal 2
        let top = rt.Structures |> List.find (fun s -> s.Name = "top")
        match top.Elements.[0] with
        | SRef sr ->
            sr.StructureName |> should equal "child"
            sr.Origin.X |> should equal 100L
            sr.Origin.Y |> should equal 200L
            sr.Reflected |> should equal false
        | _ -> failwith "expected SRef")

[<Fact>]
let ``write then read preserves rotated mirrored SRef`` () =
    let lib : Library = {
        Name = "test"
        UserUnitsPerDbUnit = 0.001
        DbUnitsInMeters = 1e-9
        Structures = [
            { Name = "child"
              Elements = [ mkRect (0, 0, 50, 50) (66, 20) ] }
            { Name = "top"
              Elements = [
                  SRef {
                      StructureName = "child"
                      Origin = { X = -300L; Y = 400L }
                      Mag = 1.0
                      Angle = 90.0
                      Reflected = true
                  }
              ] }
        ]
    }
    withTmp (fun path ->
        Gds.Writer.writeGds path lib
        let rt = Gds.Reader.readGdsLibrary path
        let top = rt.Structures |> List.find (fun s -> s.Name = "top")
        match top.Elements.[0] with
        | SRef sr ->
            sr.Origin.X |> should equal -300L
            sr.Origin.Y |> should equal 400L
            sr.Angle |> should (equalWithin 1e-6) 90.0
            sr.Reflected |> should equal true
        | _ -> failwith "expected SRef")

[<Fact>]
let ``write then read preserves UNITS reals`` () =
    let lib : Library = {
        Name = "test"
        UserUnitsPerDbUnit = 0.001
        DbUnitsInMeters = 1e-9
        Structures = [ { Name = "x"; Elements = [] } ]
    }
    withTmp (fun path ->
        Gds.Writer.writeGds path lib
        let rt = Gds.Reader.readGdsLibrary path
        rt.UserUnitsPerDbUnit |> should (equalWithin 1e-9) 0.001
        rt.DbUnitsInMeters |> should (equalWithin 1e-15) 1e-9)

[<Fact>]
let ``round-trip preserves the live lshift fixture if present`` () =
    let fixture =
        "/Users/bryancostanich/git_repos/bryan_costanich/khalkulo/source/cell_designs/reram_drv/lshift_1v8_to_3v3_optC.gds"
    if not (System.IO.File.Exists fixture) then ()
    else
        let orig = Gds.Reader.readGdsLibrary fixture
        withTmp (fun path ->
            Gds.Writer.writeGds path orig
            let rt = Gds.Reader.readGdsLibrary path
            rt.Name |> should equal orig.Name
            rt.Structures.Length |> should equal orig.Structures.Length
            // Sanity-check the first structure's element count
            // matches — full byte-identical is not the goal, but
            // logical equivalence is.
            let oStruct = orig.Structures.[0]
            let rStruct =
                rt.Structures |> List.find (fun s -> s.Name = oStruct.Name)
            rStruct.Elements.Length |> should equal oStruct.Elements.Length)
