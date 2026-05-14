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
