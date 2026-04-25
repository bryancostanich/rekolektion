module Rekolektion.Viz.Core.Tests.HierarchyTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Gds.Types
open Rekolektion.Viz.Core.Layout

let private mkStruct name = { Name = name; Elements = [] }

[<Fact>]
let ``Hierarchy.detect identifies known sram sub-blocks`` () =
    let lib = {
        Name = "test"; UserUnitsPerDbUnit = 0.001; DbUnitsInMeters = 1e-9
        Structures = [
            mkStruct "macro_v2_top"
            mkStruct "sram_array"
            mkStruct "precharge_row"
            mkStruct "column_mux"
            mkStruct "sense_amp_row"
            mkStruct "wl_driver_row"
            mkStruct "row_decoder"
            mkStruct "ctrl_logic"
            mkStruct "unrelated_thing"
        ]
    }
    let blocks = Hierarchy.detect lib
    blocks |> List.map (fun b -> b.Name) |> List.contains "sram_array" |> should equal true
    blocks |> List.map (fun b -> b.Name) |> List.contains "precharge_row" |> should equal true
    blocks |> List.length |> should be (greaterThanOrEqualTo 7)

[<Fact>]
let ``Hierarchy.detect classifies blocks by role`` () =
    let lib = {
        Name = "x"; UserUnitsPerDbUnit = 0.001; DbUnitsInMeters = 1e-9
        Structures = [mkStruct "sram_array"; mkStruct "row_decoder"]
    }
    let blocks = Hierarchy.detect lib
    let arr = blocks |> List.find (fun b -> b.Name = "sram_array")
    arr.Role |> should equal Hierarchy.BlockRole.Array
    let dec = blocks |> List.find (fun b -> b.Name = "row_decoder")
    dec.Role |> should equal Hierarchy.BlockRole.Decoder
