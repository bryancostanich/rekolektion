module Rekolektion.Viz.App.Tests.SessionStateTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.App.Services

// ─────────────────────────────────────────────────────────────────
// SessionState.serialize / parse round-trip.
//
// Touches only the pure JSON path — never `~/.rekolektion`. Real
// disk I/O is a thin wrapper around `serialize` + `parse` and not
// worth integration-testing here.
// ─────────────────────────────────────────────────────────────────

let private roundTrip (state: SessionState.State) : SessionState.State =
    state
    |> SessionState.serialize
    |> SessionState.parse

[<Fact>]
let ``empty state round-trips`` () =
    roundTrip SessionState.empty |> should equal SessionState.empty

[<Fact>]
let ``layers + openPaths + activePath round-trip`` () =
    let state : SessionState.State = {
        Layers = [ (68, 20, true, true); (69, 20, false, true) ]
        OpenPaths = [ "/tmp/a.rkt"; "/tmp/b.rkt" ]
        ActivePath = Some "/tmp/a.rkt"
        Window = None
    }
    roundTrip state |> should equal state

[<Fact>]
let ``window bounds round-trip`` () =
    let state : SessionState.State = {
        Layers = []
        OpenPaths = []
        ActivePath = None
        Window = Some {
            Width  = 1600.0
            Height = 1000.0
            X      = 120
            Y      = 80
        }
    }
    let result = roundTrip state
    result.Window |> should equal state.Window

[<Fact>]
let ``window absent stays absent`` () =
    let state : SessionState.State = {
        Layers = [ (68, 20, true, true) ]
        OpenPaths = []
        ActivePath = None
        Window = None
    }
    let result = roundTrip state
    result.Window |> should equal (None : SessionState.WindowBounds option)

[<Fact>]
let ``window with negative position round-trips (off-primary monitor)`` () =
    let state : SessionState.State = {
        Layers = []
        OpenPaths = []
        ActivePath = None
        Window = Some {
            Width  = 1920.0
            Height = 1080.0
            X      = -1920
            Y      = -100
        }
    }
    roundTrip state |> should equal state

[<Fact>]
let ``window with fractional dimensions round-trips`` () =
    // Avalonia layout often produces non-integer Width/Height.
    let state : SessionState.State = {
        Layers = []
        OpenPaths = []
        ActivePath = None
        Window = Some {
            Width  = 1423.5
            Height = 901.25
            X      = 50
            Y      = 60
        }
    }
    roundTrip state |> should equal state

[<Fact>]
let ``mixed full payload round-trips`` () =
    let state : SessionState.State = {
        Layers = [ (68, 20, true, true); (94, 20, false, true); (255, 1, false, false) ]
        OpenPaths = [ "/a/b/c.rkt"; "/x/y/z.gds" ]
        ActivePath = Some "/a/b/c.rkt"
        Window = Some { Width = 1500.0; Height = 950.0; X = 10; Y = 20 }
    }
    roundTrip state |> should equal state

[<Fact>]
let ``garbage input parses to empty`` () =
    SessionState.parse "not json at all"
    |> should equal SessionState.empty

[<Fact>]
let ``json with unknown keys parses to empty for known fields`` () =
    // Forward-compatibility: a session.json from a newer viz that
    // adds fields shouldn't crash an older viz.
    let json =
        """{"layers":[],"openPaths":[],"futureField":"ignored"}"""
    let state = SessionState.parse json
    state.Layers |> should equal ([] : (int * int * bool * bool) list)
    state.OpenPaths |> should equal ([] : string list)
    state.ActivePath |> should equal (None : string option)
    state.Window |> should equal (None : SessionState.WindowBounds option)

[<Fact>]
let ``json with malformed window object falls back to None`` () =
    // window.w missing → option falls to None; the rest of the
    // state still parses.
    let json =
        """{"layers":[],"openPaths":[],"window":{"h":900,"x":0,"y":0}}"""
    let state = SessionState.parse json
    state.Window |> should equal (None : SessionState.WindowBounds option)

[<Fact>]
let ``serialize includes the drc field per layer entry`` () =
    let state : SessionState.State =
        { Layers = [ (68, 20, true, false) ]
          OpenPaths = []
          ActivePath = None
          Window = None }
    let json = SessionState.serialize state
    json |> should haveSubstring "\"drc\":false"
    json |> should haveSubstring "\"v\":true"

[<Fact>]
let ``parse defaults drc=true on legacy entries missing the field`` () =
    // Pre-DRC-column session file shape: only n/d/v.
    let legacy =
        """{"layers":[{"n":68,"d":20,"v":true},{"n":69,"d":20,"v":false}]}"""
    let restored = SessionState.parse legacy
    restored.Layers
    |> should equal
        [ (68, 20, true,  true)
          (69, 20, false, true) ]

[<Fact>]
let ``parse handles a mix of legacy and drc-enriched entries in one file`` () =
    let json =
        """{"layers":[{"n":68,"d":20,"v":true,"drc":false},{"n":69,"d":20,"v":false}]}"""
    let restored = SessionState.parse json
    restored.Layers
    |> should equal
        [ (68, 20, true,  false)
          (69, 20, false, true) ]

[<Fact>]
let ``drc=false then drc=true round-trips per layer`` () =
    let state : SessionState.State =
        { Layers =
              [ (68, 20, true, true)
                (69, 20, true, false)
                (70, 20, false, true)
                (71, 20, false, false) ]
          OpenPaths = []
          ActivePath = None
          Window = None }
    roundTrip state |> should equal state

[<Fact>]
let ``ActivePath with escape characters round-trips`` () =
    // Paths with quotes / backslashes / unicode must survive
    // JSON escaping (sky130 paths sometimes hit ~ expansion etc).
    let state : SessionState.State = {
        Layers = []
        OpenPaths = [ "/path/with\"quote.rkt"; "/path/with\\backslash.rkt" ]
        ActivePath = Some "/path/with\"quote.rkt"
        Window = None
    }
    roundTrip state |> should equal state
