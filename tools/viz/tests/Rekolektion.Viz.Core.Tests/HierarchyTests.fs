module Rekolektion.Viz.Core.Tests.HierarchyTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout

let private mkCell name : Cell =
    { Name = name; Meta = None; Elements = []; Comments = [] }

let private mkDoc (cells: Cell list) : Document =
    { emptyDocument with Cells = cells }

[<Fact>]
let ``Hierarchy.detect identifies known sram sub-blocks`` () =
    let doc =
        mkDoc [
            mkCell "macro_v2_top"
            mkCell "sram_array"
            mkCell "precharge_row"
            mkCell "column_mux"
            mkCell "sense_amp_row"
            mkCell "wl_driver_row"
            mkCell "row_decoder"
            mkCell "ctrl_logic"
            mkCell "unrelated_thing"
        ]
    let blocks = Hierarchy.detect doc
    blocks |> List.map (fun b -> b.Name) |> List.contains "sram_array" |> should equal true
    blocks |> List.map (fun b -> b.Name) |> List.contains "precharge_row" |> should equal true
    blocks |> List.length |> should be (greaterThanOrEqualTo 7)

[<Fact>]
let ``Hierarchy.detect classifies blocks by role`` () =
    let doc = mkDoc [ mkCell "sram_array"; mkCell "row_decoder" ]
    let blocks = Hierarchy.detect doc
    let arr = blocks |> List.find (fun b -> b.Name = "sram_array")
    arr.Role |> should equal Hierarchy.BlockRole.Array
    let dec = blocks |> List.find (fun b -> b.Name = "row_decoder")
    dec.Role |> should equal Hierarchy.BlockRole.Decoder

[<Fact>]
let ``Hierarchy.closure walks SRef and ARef edges`` () =
    // top -> mid (SRef) -> leaf (ARef)
    // top -> standalone (SRef)
    let leaf = mkCell "leaf"
    let mid : Cell = {
        Name = "mid"
        Meta = None
        Comments = []
        Elements = [
            ARefEl {
                Cell = "leaf"
                Origin = { X = 0L; Y = 0L }
                Cols = 1; Rows = 1
                ColPitch = { X = 0L; Y = 0L }; RowPitch = { X = 0L; Y = 0L }
                Rot = 0.0; Mag = 1.0; Reflect = false
                Props = []; Comments = []
            }
        ]
    }
    let top : Cell = {
        Name = "top"
        Meta = None
        Comments = []
        Elements = [
            SRefEl {
                Cell = "mid"
                Origin = { X = 0L; Y = 0L }
                Rot = 0.0; Mag = 1.0; Reflect = false
                Props = []; Comments = []
            }
            SRefEl {
                Cell = "standalone"
                Origin = { X = 0L; Y = 0L }
                Rot = 0.0; Mag = 1.0; Reflect = false
                Props = []; Comments = []
            }
        ]
    }
    let doc = mkDoc [ top; mid; leaf; mkCell "standalone"; mkCell "unrelated" ]
    let reachable = Hierarchy.closure doc "top"
    reachable |> Set.contains "top" |> should equal true
    reachable |> Set.contains "mid" |> should equal true
    reachable |> Set.contains "leaf" |> should equal true
    reachable |> Set.contains "standalone" |> should equal true
    reachable |> Set.contains "unrelated" |> should equal false
