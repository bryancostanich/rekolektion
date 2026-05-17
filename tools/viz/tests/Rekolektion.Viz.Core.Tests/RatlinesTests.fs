module Rekolektion.Viz.Core.Tests.RatlinesTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Net

let private pin (x: int64) (y: int64) : Ratlines.Pin =
    { TopInstanceIndex = None
      Position = { X = x; Y = y }
      ZUm = 0.0
      LabelCount = 1 }

let private edgeLength (pins: Ratlines.Pin array) (e: Ratlines.NetEdge) : int64 =
    let a = pins.[e.From].Position
    let b = pins.[e.To].Position
    abs (a.X - b.X) + abs (a.Y - b.Y)

[<Fact>]
let ``mstOf returns empty for fewer than two pins`` () =
    Ratlines.mstOf [||] |> should equal ([||] : Ratlines.NetEdge array)
    Ratlines.mstOf [| pin 0L 0L |] |> should equal ([||] : Ratlines.NetEdge array)

[<Fact>]
let ``mstOf yields exactly N-1 edges`` () =
    let pins = [|
        pin 0L 0L
        pin 100L 0L
        pin 0L 100L
        pin 100L 100L
        pin 50L 50L
    |]
    let edges = Ratlines.mstOf pins
    edges.Length |> should equal 4

[<Fact>]
let ``mstOf picks the shortest spanning tree (Manhattan)`` () =
    // Four pins on a 100-unit square. Optimal MST uses three side
    // edges (total = 300) rather than diagonals (each = 200, total
    // = 600 for a cycle). Any valid MST has length 300.
    let pins = [|
        pin 0L 0L
        pin 100L 0L
        pin 100L 100L
        pin 0L 100L
    |]
    let edges = Ratlines.mstOf pins
    let total = edges |> Array.sumBy (edgeLength pins)
    total |> should equal 300L

[<Fact>]
let ``mstOf produces a connected tree`` () =
    let pins = [|
        pin 0L 0L
        pin 50L 30L
        pin 100L 80L
        pin 200L 200L
        pin 30L 90L
    |]
    let edges = Ratlines.mstOf pins
    // Flood-fill from node 0 — every node should be reachable.
    let visited = System.Collections.Generic.HashSet<int>([| 0 |])
    let queue = System.Collections.Generic.Queue<int>([| 0 |])
    while queue.Count > 0 do
        let v = queue.Dequeue()
        for e in edges do
            let other =
                if e.From = v then Some e.To
                elif e.To = v then Some e.From
                else None
            match other with
            | Some n when visited.Add n -> queue.Enqueue n
            | _ -> ()
    visited.Count |> should equal pins.Length

// ─── isLikelyPowerNet heuristic ─────────────────────────────────────────

[<Theory>]
[<InlineData "VDD">]
[<InlineData "VSS">]
[<InlineData "GND">]
[<InlineData "VPWR">]
[<InlineData "VGND">]
[<InlineData "VPB">]
[<InlineData "VNB">]
[<InlineData "vdd">]    // case-insensitive
[<InlineData "AVDD">]
[<InlineData "DVSS">]
[<InlineData "VDD_1V8">]
[<InlineData "VSS_CORE">]
[<InlineData "GND_PAD">]
let ``isLikelyPowerNet matches power patterns`` (name: string) =
    Ratlines.isLikelyPowerNet name |> should equal true

[<Theory>]
[<InlineData "BL">]
[<InlineData "WL">]
[<InlineData "CLK">]
[<InlineData "RESET_N">]
[<InlineData "SEL[0]">]
[<InlineData "GATE">]
[<InlineData "">]
[<InlineData " ">]
[<InlineData "VDDISH">]   // VDD-prefix without separator → still a signal
[<InlineData "GNDLY">]    // ditto
let ``isLikelyPowerNet rejects signal nets`` (name: string) =
    Ratlines.isLikelyPowerNet name |> should equal false

// ─── compute: kind-filtered net-pin extraction ─────────────────────────

let private mkLabel (text: string) (origin: Point) (kind: LabelKind) : Element =
    LabelEl {
        Layer = Named ("sky130", "met1_label")
        Text = text
        Origin = origin
        Class = None
        Props = []
        Comments = []
        IsInternal = false
        Kind = kind
    }

let private docWithLabels (labels: Element list) : Document =
    { emptyDocument with
        Cells = [
            { Name = "top"; Meta = None; Comments = []; Elements = labels }
        ]
        TopCell = Some "top" }

[<Fact>]
let ``compute counts NetName labels as ratline pins`` () =
    // Two separate top-level labels with the same net name produce
    // one route with two pins (and one MST edge between them).
    let doc =
        docWithLabels [
            mkLabel "VDD" { X = 0L; Y = 0L } NetName
            mkLabel "VDD" { X = 1000L; Y = 0L } NetName
        ]
    let flat = Rekolektion.Viz.Core.Layout.Flatten.flatten doc
    let routes = Ratlines.compute doc flat
    routes |> Array.length |> should equal 1
    let route = routes.[0]
    route.Name |> should equal "VDD"
    route.Pins |> Array.length |> should equal 2

[<Fact>]
let ``compute skips DeviceTerminal labels`` () =
    // FET port labels never become ratline routes — they're device
    // pin annotations, not nets. This is the bug track 06 fixes.
    let doc =
        docWithLabels [
            mkLabel "G" { X = 0L; Y = 0L } DeviceTerminal
            mkLabel "G" { X = 1000L; Y = 0L } DeviceTerminal
            mkLabel "D" { X = 0L; Y = 500L } DeviceTerminal
            mkLabel "D" { X = 1000L; Y = 500L } DeviceTerminal
        ]
    let flat = Rekolektion.Viz.Core.Layout.Flatten.flatten doc
    let routes = Ratlines.compute doc flat
    routes |> Array.length |> should equal 0

[<Fact>]
let ``compute mixes filters NetName in, DeviceTerminal out`` () =
    // Three labels: one VDD net (NetName), two G device terminals.
    // Only VDD should produce a route; G labels are excluded.
    let doc =
        docWithLabels [
            mkLabel "VDD" { X = 0L; Y = 0L } NetName
            mkLabel "VDD" { X = 2000L; Y = 0L } NetName
            mkLabel "G" { X = 500L; Y = 0L } DeviceTerminal
            mkLabel "G" { X = 1500L; Y = 0L } DeviceTerminal
        ]
    let flat = Rekolektion.Viz.Core.Layout.Flatten.flatten doc
    let routes = Ratlines.compute doc flat
    routes |> Array.length |> should equal 1
    routes.[0].Name |> should equal "VDD"
