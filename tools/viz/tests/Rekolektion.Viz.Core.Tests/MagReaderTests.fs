module Rekolektion.Viz.Core.Tests.MagReaderTests

open System.IO
open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Mag

let private tempMag (lines: string list) : string =
    let path = Path.Combine(Path.GetTempPath(), sprintf "mag_test_%s.mag" (System.Guid.NewGuid().ToString("N")))
    File.WriteAllLines(path, lines)
    path

[<Fact>]
let ``layer map known names map to SKY130 keys`` () =
    LayerMap.tryFind "metal1" |> should equal (Some (68, 20))
    LayerMap.tryFind "locali" |> should equal (Some (67, 20))
    LayerMap.tryFind "viali"  |> should equal (Some (67, 44))
    LayerMap.tryFind "poly"   |> should equal (Some (66, 20))
    // Case insensitivity
    LayerMap.tryFind "METAL1" |> should equal (Some (68, 20))

[<Fact>]
let ``layer map unknown name returns None`` () =
    LayerMap.tryFind "doesnotexist" |> should equal None

[<Fact>]
let ``transform identity decodes to no rotation, no reflect`` () =
    let s = Transform.toSref "child" 1.0 0.0 0.0 1.0 100.0 200.0
    s.StructureName |> should equal "child"
    s.Reflected     |> should equal false
    s.Mag           |> should (equalWithin 1e-6) 1.0
    abs s.Angle     |> should (lessThan) 1e-6
    s.Origin.X      |> should equal 100L
    s.Origin.Y      |> should equal 200L

[<Fact>]
let ``transform 90 deg CCW`` () =
    // [a b; c d] = [0 -1; 1 0]
    let s = Transform.toSref "child" 0.0 -1.0 1.0 0.0 0.0 0.0
    s.Reflected |> should equal false
    abs (s.Angle - 90.0) |> should (lessThan) 1e-3

[<Fact>]
let ``transform mirror about X is reflected`` () =
    // [1 0; 0 -1] : flip Y axis. det = -1.
    let s = Transform.toSref "child" 1.0 0.0 0.0 -1.0 0.0 0.0
    s.Reflected |> should equal true
    abs s.Angle |> should (lessThan) 1e-3

[<Fact>]
let ``magscale 1 2 yields half-lambda DBU`` () =
    let path = tempMag [
        "magic"
        "tech sky130A"
        "magscale 1 2"
        "<< metal1 >>"
        "rect 0 0 10 20"
        "<< end >>"
    ]
    try
        let cell = Reader.read path
        cell.MagscaleNum |> should equal 1
        cell.MagscaleDenom |> should equal 2
        // 1 internal unit = lambda * num/denom = 5nm * 1/2 = 2.5nm
        let lib, _ = Layout.MagToLayout.buildLibrary cell [cell]
        lib.UserUnitsPerDbUnit |> should (equalWithin 1e-9) 0.0025
    finally
        File.Delete path

[<Fact>]
let ``rect parser produces closed boundary on the mapped layer`` () =
    let path = tempMag [
        "magic"
        "tech sky130A"
        "magscale 1 1"
        "<< metal1 >>"
        "rect 0 0 100 50"
        "<< end >>"
    ]
    try
        let cell = Reader.read path
        cell.Rects.Length |> should equal 1
        let lib, _ = Layout.MagToLayout.buildLibrary cell [cell]
        lib.Structures.Length |> should equal 1
        let elems = lib.Structures.Head.Elements
        elems.Length |> should equal 1
        match elems.Head with
        | Gds.Types.Boundary b ->
            b.Layer |> should equal 68
            b.DataType |> should equal 20
            // 5 points (closed polygon, first = last)
            b.Points.Length |> should equal 5
        | _ -> failwith "expected Boundary"
    finally
        File.Delete path

[<Fact>]
let ``unknown layer logs a warning, doesn't throw`` () =
    let path = tempMag [
        "magic"
        "tech sky130A"
        "magscale 1 1"
        "<< zzz_made_up_layer >>"
        "rect 0 0 10 10"
        "<< end >>"
    ]
    try
        let cell = Reader.read path
        let _, warnings = Layout.MagToLayout.buildLibrary cell [cell]
        warnings |> should not' (be Empty)
        let combined = warnings |> List.reduce (fun a b -> a + " | " + b)
        combined |> should haveSubstring "zzz_made_up_layer"
    finally
        File.Delete path

[<Fact>]
let ``hierarchy depth 2 resolves subcell from same directory`` () =
    let dir = Path.Combine(Path.GetTempPath(), sprintf "maghier_%s" (System.Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory dir |> ignore
    let childPath = Path.Combine(dir, "leaf.mag")
    let parentPath = Path.Combine(dir, "root.mag")
    File.WriteAllLines(childPath, [
        "magic"
        "tech sky130A"
        "magscale 1 1"
        "<< metal1 >>"
        "rect 0 0 100 100"
        "<< end >>"
    ])
    File.WriteAllLines(parentPath, [
        "magic"
        "tech sky130A"
        "magscale 1 1"
        "<< metal1 >>"
        "rect -200 -200 200 200"
        "use leaf inst1"
        "timestamp 0"
        "transform 1 0 50 0 1 75"
        "box 0 0 100 100"
        "<< end >>"
    ])
    try
        let lib, warnings = Layout.MagToLayout.loadFile parentPath []
        warnings |> List.filter (fun w -> w.Contains "not found") |> should be Empty
        // Two cells: root + leaf
        lib.Structures.Length |> should equal 2
        let root =
            lib.Structures
            |> List.find (fun s -> s.Name = "root")
        // Root has at least one Boundary + one SRef pointing at leaf
        let srefs =
            root.Elements
            |> List.choose (function Gds.Types.SRef s -> Some s | _ -> None)
        srefs.Length |> should equal 1
        srefs.Head.StructureName |> should equal "leaf"
        srefs.Head.Origin.X |> should equal 50L
        srefs.Head.Origin.Y |> should equal 75L
    finally
        File.Delete childPath
        File.Delete parentPath
        Directory.Delete dir

[<Fact>]
let ``missing subcell logs warning, doesn't crash`` () =
    let path = tempMag [
        "magic"
        "tech sky130A"
        "magscale 1 1"
        "use definitely_not_present inst"
        "timestamp 0"
        "transform 1 0 0 0 1 0"
        "box 0 0 1 1"
        "<< end >>"
    ]
    try
        let _, warnings = Layout.MagToLayout.loadFile path []
        warnings
        |> List.exists (fun w -> w.Contains "not found")
        |> should equal true
    finally
        File.Delete path
