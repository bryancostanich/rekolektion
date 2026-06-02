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
//
// All record literals use `{ SessionState.empty with ... }` so a
// new field added to `State` doesn't force a test edit — the
// default flows from `empty`.
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
    let state =
        { SessionState.empty with
            Layers = [ (68, 20, true, true); (69, 20, false, true) ]
            OpenPaths = [ "/tmp/a.rkt"; "/tmp/b.rkt" ]
            ActivePath = Some "/tmp/a.rkt" }
    roundTrip state |> should equal state

[<Fact>]
let ``window bounds round-trip`` () =
    let state =
        { SessionState.empty with
            Window = Some {
                Width  = 1600.0
                Height = 1000.0
                X      = 120
                Y      = 80
            } }
    let result = roundTrip state
    result.Window |> should equal state.Window

[<Fact>]
let ``window absent stays absent`` () =
    let state =
        { SessionState.empty with
            Layers = [ (68, 20, true, true) ] }
    let result = roundTrip state
    result.Window |> should equal (None : SessionState.WindowBounds option)

[<Fact>]
let ``window with negative position round-trips (off-primary monitor)`` () =
    let state =
        { SessionState.empty with
            Window = Some {
                Width  = 1920.0
                Height = 1080.0
                X      = -1920
                Y      = -100
            } }
    roundTrip state |> should equal state

[<Fact>]
let ``window with fractional dimensions round-trips`` () =
    // Avalonia layout often produces non-integer Width/Height.
    let state =
        { SessionState.empty with
            Window = Some {
                Width  = 1423.5
                Height = 901.25
                X      = 50
                Y      = 60
            } }
    roundTrip state |> should equal state

[<Fact>]
let ``mixed full payload round-trips`` () =
    let state =
        { SessionState.empty with
            Layers = [ (68, 20, true, true); (94, 20, false, true); (255, 1, false, false) ]
            OpenPaths = [ "/a/b/c.rkt"; "/x/y/z.gds" ]
            ActivePath = Some "/a/b/c.rkt"
            Window = Some { Width = 1500.0; Height = 950.0; X = 10; Y = 20 } }
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
    let state =
        { SessionState.empty with
            Layers = [ (68, 20, true, false) ] }
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
let ``drcOther round-trips at the top level`` () =
    let onState = { SessionState.empty with DrcOther = true }
    let offState = { onState with DrcOther = false }
    roundTrip onState  |> should equal onState
    roundTrip offState |> should equal offState

[<Fact>]
let ``serialize includes the drcOther field`` () =
    let state = { SessionState.empty with DrcOther = false }
    let json = SessionState.serialize state
    json |> should haveSubstring "\"drcOther\":false"

[<Fact>]
let ``parse defaults drcOther=true on legacy files missing the field`` () =
    let legacy = """{"layers":[],"openPaths":[]}"""
    let restored = SessionState.parse legacy
    restored.DrcOther |> should equal true

[<Fact>]
let ``parse honors drcOther=false from disk`` () =
    let json = """{"layers":[],"openPaths":[],"drcOther":false}"""
    let restored = SessionState.parse json
    restored.DrcOther |> should equal false

[<Fact>]
let ``drc=false then drc=true round-trips per layer`` () =
    let state =
        { SessionState.empty with
            Layers =
                [ (68, 20, true, true)
                  (69, 20, true, false)
                  (70, 20, false, true)
                  (71, 20, false, false) ] }
    roundTrip state |> should equal state

[<Fact>]
let ``ActivePath with escape characters round-trips`` () =
    // Paths with quotes / backslashes / unicode must survive
    // JSON escaping (sky130 paths sometimes hit ~ expansion etc).
    let state =
        { SessionState.empty with
            OpenPaths = [ "/path/with\"quote.rkt"; "/path/with\\backslash.rkt" ]
            ActivePath = Some "/path/with\"quote.rkt" }
    roundTrip state |> should equal state

// ─────────────────────────────────────────────────────────────────
// User-facing display toggles (snap, ruler, grid, labels,
// ratlines-armed, DRC, dimensions) — persisted across restarts.
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``empty defaults match Model.empty defaults`` () =
    let e = SessionState.empty
    e.SnapEnabled    |> should equal false
    e.ShowAxes       |> should equal true
    e.ShowGrid       |> should equal true
    e.ShowLabels     |> should equal true
    e.RatlinesArmed  |> should equal false
    e.ShowDrc        |> should equal false
    e.ShowDrcLabels  |> should equal true
    e.ShowDimensions |> should equal false

[<Fact>]
let ``each display toggle round-trips through the flipped state`` () =
    let flipped =
        { SessionState.empty with
            SnapEnabled    = true
            ShowAxes       = false
            ShowGrid       = false
            ShowLabels     = false
            RatlinesArmed  = true
            ShowDrc        = true
            ShowDrcLabels  = false
            ShowDimensions = true }
    roundTrip flipped |> should equal flipped

[<Fact>]
let ``parse defaults each display toggle on legacy files missing the fields`` () =
    let legacy = """{"layers":[],"openPaths":[]}"""
    let restored = SessionState.parse legacy
    restored.SnapEnabled    |> should equal false
    restored.ShowAxes       |> should equal true
    restored.ShowGrid       |> should equal true
    restored.ShowLabels     |> should equal true
    restored.RatlinesArmed  |> should equal false
    restored.ShowDrc        |> should equal false
    restored.ShowDrcLabels  |> should equal true
    restored.ShowDimensions |> should equal false

[<Fact>]
let ``serialize emits each display-toggle key`` () =
    let json = SessionState.serialize SessionState.empty
    for key in [ "snapEnabled"; "showAxes"; "showGrid"; "showLabels"
                 "ratlinesArmed"; "showDrc"; "showDrcLabels"; "showDimensions" ] do
        json |> should haveSubstring (sprintf "\"%s\":" key)

[<Fact>]
let ``parse honours each display-toggle value from disk`` () =
    let json =
        """{"layers":[],"openPaths":[],"snapEnabled":true,"showAxes":false,"showGrid":false,"showLabels":false,"ratlinesArmed":true,"showDrc":true,"showDrcLabels":false,"showDimensions":true}"""
    let s = SessionState.parse json
    s.SnapEnabled    |> should equal true
    s.ShowAxes       |> should equal false
    s.ShowGrid       |> should equal false
    s.ShowLabels     |> should equal false
    s.RatlinesArmed  |> should equal true
    s.ShowDrc        |> should equal true
    s.ShowDrcLabels  |> should equal false
    s.ShowDimensions |> should equal true

[<Fact>]
let ``parse falls back to legacy showRuler key when showAxes is missing`` () =
    // Sessions written before the rename (2026-06-02) used
    // "showRuler" for the origin-axes overlay toggle. The new
    // code writes "showAxes" but must still honour the legacy
    // key so an existing user's preference survives upgrade.
    let legacy = """{"layers":[],"openPaths":[],"showRuler":false}"""
    let s = SessionState.parse legacy
    s.ShowAxes |> should equal false

[<Fact>]
let ``parse prefers showAxes when both keys are present`` () =
    // If both the new and legacy keys appear (e.g. a tool wrote
    // both during transition), the new key wins.
    let both =
        """{"layers":[],"openPaths":[],"showAxes":true,"showRuler":false}"""
    let s = SessionState.parse both
    s.ShowAxes |> should equal true
