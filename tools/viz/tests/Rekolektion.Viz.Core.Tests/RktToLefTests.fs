module Rekolektion.Viz.Core.Tests.RktToLefTests

/// Tests for `Rkt.ToLef` — LEF 5.7 emission from canonical `.rkt`.
/// Mirrors the discipline used in `RktToGdsTests`: golden-file
/// comparisons for stable strings, structural assertions for the
/// human-readable bits.

open System
open System.IO
open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt
open Rekolektion.Viz.Core.Rkt.Types

let private fixturesDir =
    Path.Combine(AppContext.BaseDirectory, "testdata", "lef")

let private readFixture (name: string) : Document =
    let path = Path.Combine(fixturesDir, name)
    match Reader.readFile path with
    | Ok (_, doc) -> doc
    | Error e -> failwithf "fixture parse failed (%s): %A" name e

// ─── SIZE / ORIGIN — happy path ────────────────────────────────────────

[<Fact>]
let ``simple_3port emits SIZE and ORIGIN derived from (props (bbox …))`` () =
    let doc = readFixture "simple_3port.rkt"
    match ToLef.emitCell ToLef.EmitOptions.defaults doc "simple_3port" with
    | Error e -> failwithf "emit failed: %s" (ToLef.formatError e)
    | Ok lef ->
        // bbox = (0 0 5000 2000) DBU → 5.0 × 2.0 µm at dbu_nm=1
        lef |> should haveSubstring "SIZE 5 BY 2 ;"
        // No shift needed: origin is (-0, -0) = 0 0.
        lef |> should haveSubstring "ORIGIN 0 0 ;"
        // Macro name preserved.
        lef |> should haveSubstring "MACRO simple_3port"
        lef |> should haveSubstring "END simple_3port"
        // CLASS BLOCK is the standard macro class for SRAM-like
        // assemblies; FOREIGN ties the LEF abstract to its GDS by
        // name + offset.
        lef |> should haveSubstring "CLASS BLOCK ;"
        lef |> should haveSubstring "FOREIGN simple_3port 0 0 ;"

[<Fact>]
let ``description prop emits as a leading comment`` () =
    let doc = readFixture "simple_3port.rkt"
    match ToLef.emitCell ToLef.EmitOptions.defaults doc "simple_3port" with
    | Error e -> failwithf "emit failed: %s" (ToLef.formatError e)
    | Ok lef ->
        lef |> should haveSubstring "# DESCRIPTION: minimal 3-port LEF fixture"

[<Fact>]
let ``EmitDescriptionComment=false suppresses the description comment`` () =
    let doc = readFixture "simple_3port.rkt"
    let opts = { ToLef.EmitOptions.defaults with EmitDescriptionComment = false }
    match ToLef.emitCell opts doc "simple_3port" with
    | Error e -> failwithf "emit failed: %s" (ToLef.formatError e)
    | Ok lef ->
        lef.Contains "# DESCRIPTION" |> should equal false

[<Fact>]
let ``shifted bbox produces ORIGIN equal to negated bbox lower-left`` () =
    let doc = readFixture "shifted_origin.rkt"
    match ToLef.emitCell ToLef.EmitOptions.defaults doc "cim_reram_array_256x64" with
    | Error e -> failwithf "emit failed: %s" (ToLef.formatError e)
    | Ok lef ->
        // width = 6430 - (-1140) = 7570 DBU = 7.57 µm
        // height = 720 - (-720) = 1440 DBU = 1.44 µm
        lef |> should haveSubstring "SIZE 7.57 BY 1.44 ;"
        // ORIGIN = (-(-1140), -(-720)) µm-shifted = (1.14, 0.72)
        lef |> should haveSubstring "ORIGIN 1.14 0.72 ;"

[<Fact>]
let ``MacroName override changes the MACRO name everywhere`` () =
    let doc = readFixture "simple_3port.rkt"
    let opts = { ToLef.EmitOptions.defaults with MacroName = Some "renamed_macro" }
    match ToLef.emitCell opts doc "simple_3port" with
    | Error e -> failwithf "emit failed: %s" (ToLef.formatError e)
    | Ok lef ->
        lef |> should haveSubstring "MACRO renamed_macro"
        lef |> should haveSubstring "FOREIGN renamed_macro 0 0 ;"
        lef |> should haveSubstring "END renamed_macro"

// ─── Determinism (A7) ─────────────────────────────────────────────────

[<Fact>]
let ``same input produces byte-equal output (determinism)`` () =
    let doc = readFixture "simple_3port.rkt"
    let opts = ToLef.EmitOptions.defaults
    let r1 = ToLef.emitCell opts doc "simple_3port"
    let r2 = ToLef.emitCell opts doc "simple_3port"
    r1 |> should equal r2

// ─── Negative cases — required errors (A3, A6) ────────────────────────

[<Fact>]
let ``cell without (props (bbox …)) is rejected with MissingBboxProp`` () =
    let doc = readFixture "no_bbox.rkt"
    match ToLef.emitCell ToLef.EmitOptions.defaults doc "no_bbox_macro" with
    | Ok _ -> failwith "expected MissingBboxProp"
    | Error (ToLef.MissingBboxProp "no_bbox_macro") -> ()
    | Error other -> failwithf "expected MissingBboxProp, got %A" other

[<Fact>]
let ``off-grid extent is rejected with OffGridCoordinate`` () =
    let doc = readFixture "off_grid_port.rkt"
    match ToLef.emitCell ToLef.EmitOptions.defaults doc "off_grid_macro" with
    | Ok _ -> failwith "expected OffGridCoordinate"
    | Error (ToLef.OffGridCoordinate (_, _, "off_grid_macro")) -> ()
    | Error other -> failwithf "expected OffGridCoordinate, got %A" other

[<Fact>]
let ``unknown cell name returns NoSuchCell`` () =
    let doc = readFixture "simple_3port.rkt"
    match ToLef.emitCell ToLef.EmitOptions.defaults doc "does_not_exist" with
    | Ok _ -> failwith "expected NoSuchCell"
    | Error (ToLef.NoSuchCell "does_not_exist") -> ()
    | Error other -> failwithf "expected NoSuchCell, got %A" other

// ─── In-memory document (no fixture file) ─────────────────────────────

[<Fact>]
let ``bbox with negative-or-zero extent fails with InvalidBbox`` () =
    let cell : Cell = {
        Name = "degenerate"
        Meta = None
        Comments = []
        SubFormComments = Map.empty
        Elements = [
            PropsEl {
                Items = [
                    { Key = "bbox"
                      Value = PvTuple [ PvInt 100L; PvInt 100L; PvInt 100L; PvInt 100L ] }
                ]
                Comments = []
                SubFormComments = Map.empty
            }
        ]
    }
    let doc =
        { emptyDocument with
            Cells = [ cell ]
            TopCell = Some "degenerate" }
    match ToLef.emitCell ToLef.EmitOptions.defaults doc "degenerate" with
    | Ok _ -> failwith "expected InvalidBbox"
    | Error (ToLef.InvalidBbox ("degenerate", _)) -> ()
    | Error other -> failwithf "expected InvalidBbox, got %A" other

// ─── Pin emission (P1) ────────────────────────────────────────────────

[<Fact>]
let ``simple_3port emits one PIN per (port …) element`` () =
    let doc = readFixture "simple_3port.rkt"
    match ToLef.emitCell ToLef.EmitOptions.defaults doc "simple_3port" with
    | Error e -> failwithf "emit failed: %s" (ToLef.formatError e)
    | Ok lef ->
        // Three ports → three PIN blocks.
        lef |> should haveSubstring "PIN VDD"
        lef |> should haveSubstring "PIN VSS"
        lef |> should haveSubstring "PIN A"
        // VDD power on met4.
        lef |> should haveSubstring "USE POWER ;"
        lef |> should haveSubstring "LAYER met4 ;"
        // VSS ground on met4.
        lef |> should haveSubstring "USE GROUND ;"
        // Signal A on met3.
        lef |> should haveSubstring "LAYER met3 ;"
        // RECT shape coords (no bbox shift since bbox starts at 0,0).
        lef |> should haveSubstring "RECT 0.1 0.5 0.2 0.6 ;"

[<Fact>]
let ``each direction maps to its LEF DIRECTION clause`` () =
    let doc = readFixture "all_directions.rkt"
    match ToLef.emitCell ToLef.EmitOptions.defaults doc "all_directions" with
    | Error e -> failwithf "emit failed: %s" (ToLef.formatError e)
    | Ok lef ->
        lef |> should haveSubstring "PIN IN_PIN"
        lef |> should haveSubstring "DIRECTION INPUT ;"
        lef |> should haveSubstring "PIN OUT_PIN"
        lef |> should haveSubstring "DIRECTION OUTPUT ;"
        lef |> should haveSubstring "PIN BI_PIN"
        lef |> should haveSubstring "DIRECTION INOUT ;"
        // Unspecified direction omits the DIRECTION clause entirely;
        // verify by scanning the UN_PIN block.
        let unBlock =
            let s = lef.IndexOf "PIN UN_PIN"
            let e = lef.IndexOf("END UN_PIN", s)
            lef.Substring(s, e - s)
        unBlock.Contains "DIRECTION" |> should equal false

[<Fact>]
let ``every flag combo maps to USE and CLASS per the mapping table`` () =
    let doc = readFixture "all_flag_combos.rkt"
    match ToLef.emitCell ToLef.EmitOptions.defaults doc "all_flag_combos" with
    | Error e -> failwithf "emit failed: %s" (ToLef.formatError e)
    | Ok lef ->
        let blockFor (pin: string) : string =
            let s = lef.IndexOf(sprintf "PIN %s" pin)
            let e = lef.IndexOf(sprintf "END %s" pin, s)
            lef.Substring(s, e - s)
        // No flags → USE SIGNAL, no CLASS.
        let b = blockFor "P_NOFLAGS"
        b |> should haveSubstring "USE SIGNAL ;"
        b.Contains "CLASS " |> should equal false
        // signal → USE SIGNAL, no CLASS.
        let b = blockFor "P_SIGNAL"
        b |> should haveSubstring "USE SIGNAL ;"
        b.Contains "CLASS " |> should equal false
        // power → USE POWER.
        blockFor "P_POWER" |> should haveSubstring "USE POWER ;"
        // ground → USE GROUND.
        blockFor "P_GROUND" |> should haveSubstring "USE GROUND ;"
        // clock → USE CLOCK.
        blockFor "P_CLOCK" |> should haveSubstring "USE CLOCK ;"
        // analog → USE ANALOG.
        blockFor "P_ANALOG" |> should haveSubstring "USE ANALOG ;"
        // scan alone → USE SIGNAL + CLASS SCAN.
        let b = blockFor "P_SCAN_ALONE"
        b |> should haveSubstring "USE SIGNAL ;"
        b |> should haveSubstring "CLASS SCAN ;"
        // signal+scan → USE SIGNAL + CLASS SCAN.
        let b = blockFor "P_SIG_SCAN"
        b |> should haveSubstring "USE SIGNAL ;"
        b |> should haveSubstring "CLASS SCAN ;"

[<Fact>]
let ``power+ground is rejected with ConflictingFlags`` () =
    let doc = readFixture "conflicting_flags.rkt"
    match ToLef.emitCell ToLef.EmitOptions.defaults doc "conflicting_flags" with
    | Ok _ -> failwith "expected ConflictingFlags"
    | Error (ToLef.ConflictingFlags ("BAD", _)) -> ()
    | Error other -> failwithf "expected ConflictingFlags, got %A" other

[<Fact>]
let ``unknown:N/D layer port is rejected with UnknownLayer`` () =
    let doc = readFixture "unknown_layer_port.rkt"
    match ToLef.emitCell ToLef.EmitOptions.defaults doc "unknown_layer_macro" with
    | Ok _ -> failwith "expected UnknownLayer"
    | Error (ToLef.UnknownLayer (Unknown (999, 77), "unknown_layer_macro")) -> ()
    | Error other -> failwithf "expected UnknownLayer, got %A" other

[<Fact>]
let ``Uppercase PinCase upper-cases pin names`` () =
    // simple_3port already uses uppercase port names; build an
    // in-memory cell with lowercase names to exercise the policy.
    let cell : Cell = {
        Name = "case_test"
        Meta = None
        Comments = []
        SubFormComments = Map.empty
        Elements = [
            PropsEl { Items = [
                { Key = "bbox"
                  Value = PvTuple [ PvInt 0L; PvInt 0L; PvInt 1000L; PvInt 1000L ] } ]
                      Comments = []
                      SubFormComments = Map.empty }
            PortEl {
                Name = "addr"; Direction = Input
                Layer = Named ("sky130", "met3")
                Flags = [ Signal ]
                Shape = RectShape (100L, 100L, 200L, 200L)
                Net = None; Props = []; Comments = []
                SubFormComments = Map.empty
            }
        ]
    }
    let doc = { emptyDocument with Cells = [ cell ] }
    // Verbatim
    let opts = { ToLef.EmitOptions.defaults with PinCase = ToLef.Verbatim }
    match ToLef.emitCell opts doc "case_test" with
    | Error e -> failwithf "emit failed: %s" (ToLef.formatError e)
    | Ok lef -> lef |> should haveSubstring "PIN addr"
    // Uppercase
    let opts = { ToLef.EmitOptions.defaults with PinCase = ToLef.Uppercase }
    match ToLef.emitCell opts doc "case_test" with
    | Error e -> failwithf "emit failed: %s" (ToLef.formatError e)
    | Ok lef ->
        lef |> should haveSubstring "PIN ADDR"
        lef |> should haveSubstring "END ADDR"

[<Fact>]
let ``port coords shift by bbox lower-left`` () =
    let doc = readFixture "shifted_origin.rkt"
    // Add a port to the in-memory cell since the fixture doesn't.
    let cell = doc.Cells |> List.head
    let portedCell =
        { cell with
            Elements =
                cell.Elements @ [
                    PortEl {
                        Name = "A"; Direction = Input
                        Layer = Named ("sky130", "met3")
                        Flags = [ Signal ]
                        // RKT-frame: (-1140, -720) is the bbox origin.
                        // A pin at (0, 0)–(100, 100) RKT lands at
                        // (1140, 720)–(1240, 820) LEF DBU after shift,
                        // = (1.14, 0.72)–(1.24, 0.82) µm.
                        Shape = RectShape (0L, 0L, 100L, 100L)
                        Net = None; Props = []; Comments = []
                        SubFormComments = Map.empty
                    }
                ] }
    let doc = { doc with Cells = [ portedCell ] }
    match ToLef.emitCell ToLef.EmitOptions.defaults doc "cim_reram_array_256x64" with
    | Error e -> failwithf "emit failed: %s" (ToLef.formatError e)
    | Ok lef ->
        lef |> should haveSubstring "RECT 1.14 0.72 1.24 0.82 ;"

[<Fact>]
let ``poly port shape emits as POLYGON with shifted coords`` () =
    let cell : Cell = {
        Name = "poly_port"
        Meta = None
        Comments = []
        SubFormComments = Map.empty
        Elements = [
            PropsEl { Items = [
                { Key = "bbox"
                  Value = PvTuple [ PvInt 0L; PvInt 0L; PvInt 1000L; PvInt 1000L ] } ]
                      Comments = []
                      SubFormComments = Map.empty }
            PortEl {
                Name = "Z"; Direction = Output
                Layer = Named ("sky130", "met2")
                Flags = [ Signal ]
                Shape = PolyShape [
                    { X = 100L; Y = 100L }
                    { X = 200L; Y = 100L }
                    { X = 200L; Y = 200L }
                    { X = 100L; Y = 200L }
                ]
                Net = None; Props = []; Comments = []
                SubFormComments = Map.empty
            }
        ]
    }
    let doc = { emptyDocument with Cells = [ cell ] }
    match ToLef.emitCell ToLef.EmitOptions.defaults doc "poly_port" with
    | Error e -> failwithf "emit failed: %s" (ToLef.formatError e)
    | Ok lef ->
        lef |> should haveSubstring "POLYGON 0.1 0.1 0.2 0.1 0.2 0.2 0.1 0.2 ;"

// ─── BandExcluding ObsPolicy + DecimalPrecision ────────────────────────

[<Fact>]
let ``BandExcluding emits full rects for fullLayers + band rects for bandLayers`` () =
    let doc = readFixture "simple_3port.rkt"
    let opts =
        { ToLef.EmitOptions.defaults with
            Obstructions =
                ToLef.BandExcluding (
                    [ "met1"; "met2" ],
                    [ "met3", 0.5m, 1.5m ]) }
    match ToLef.emitCell opts doc "simple_3port" with
    | Error e -> failwithf "emit failed: %s" (ToLef.formatError e)
    | Ok lef ->
        // simple_3port has bbox 0..5000 x 0..2000 DBU = 5 × 2 µm.
        // Full rects on met1+met2.
        lef |> should haveSubstring "LAYER met1 ;\n        RECT 0 0 5 2 ;"
        lef |> should haveSubstring "LAYER met2 ;\n        RECT 0 0 5 2 ;"
        // Band on met3 — y-extent 0.5..1.5, x = full width.
        lef |> should haveSubstring "LAYER met3 ;\n        RECT 0 0.5 5 1.5 ;"

[<Fact>]
let ``BandExcluding rejects off-grid band y-coord`` () =
    let doc = readFixture "simple_3port.rkt"
    let opts =
        { ToLef.EmitOptions.defaults with
            // 0.502 isn't a multiple of 0.005 mfg grid.
            Obstructions = ToLef.BandExcluding ([], [ "met3", 0.502m, 1.5m ]) }
    match ToLef.emitCell opts doc "simple_3port" with
    | Ok _ -> failwith "expected OffGridCoordinate"
    | Error (ToLef.OffGridCoordinate ("obs-band-y", _, _)) -> ()
    | Error other -> failwithf "expected OffGridCoordinate, got %A" other

[<Fact>]
let ``DecimalPrecision = Some 3 emits zero-padded fixed-precision coords`` () =
    let doc = readFixture "shifted_origin.rkt"
    let opts = { ToLef.EmitOptions.defaults with DecimalPrecision = Some 3 }
    match ToLef.emitCell opts doc "cim_reram_array_256x64" with
    | Error e -> failwithf "emit failed: %s" (ToLef.formatError e)
    | Ok lef ->
        // SIZE 7.570 BY 1.440 — three decimals, zero-padded.
        lef |> should haveSubstring "SIZE 7.570 BY 1.440 ;"
        lef |> should haveSubstring "ORIGIN 1.140 0.720 ;"

[<Fact>]
let ``DecimalPrecision = None (default) uses minimal trim`` () =
    let doc = readFixture "shifted_origin.rkt"
    match ToLef.emitCell ToLef.EmitOptions.defaults doc "cim_reram_array_256x64" with
    | Error e -> failwithf "emit failed: %s" (ToLef.formatError e)
    | Ok lef ->
        lef |> should haveSubstring "SIZE 7.57 BY 1.44 ;"
        lef |> should haveSubstring "ORIGIN 1.14 0.72 ;"

// ─── Cosmetic switches — D5 ───────────────────────────────────────────

[<Fact>]
let ``EmitAbutmentShape adds SHAPE ABUTMENT on power and ground pins`` () =
    let doc = readFixture "simple_3port.rkt"
    let opts = { ToLef.EmitOptions.defaults with EmitAbutmentShape = true }
    match ToLef.emitCell opts doc "simple_3port" with
    | Error e -> failwithf "emit failed: %s" (ToLef.formatError e)
    | Ok lef ->
        // VDD (power) and VSS (ground) both pick up SHAPE ABUTMENT.
        let countOccurrences (substring: string) (text: string) =
            (text.Length - text.Replace(substring, "").Length) / substring.Length
        countOccurrences "SHAPE ABUTMENT ;" lef |> should equal 2
        // Signal pin "A" does NOT pick it up.
        let aBlock =
            let s = lef.IndexOf "PIN A"
            let e = lef.IndexOf("END A", s)
            lef.Substring(s, e - s)
        aBlock.Contains "SHAPE ABUTMENT" |> should equal false

[<Fact>]
let ``EmitAbutmentShape defaults to off (no SHAPE clause anywhere)`` () =
    let doc = readFixture "simple_3port.rkt"
    match ToLef.emitCell ToLef.EmitOptions.defaults doc "simple_3port" with
    | Error e -> failwithf "emit failed: %s" (ToLef.formatError e)
    | Ok lef -> lef.Contains "SHAPE ABUTMENT" |> should equal false

[<Fact>]
let ``Symmetry option emits SYMMETRY clause at macro level`` () =
    let doc = readFixture "simple_3port.rkt"
    let opts = { ToLef.EmitOptions.defaults with Symmetry = Some "X Y" }
    match ToLef.emitCell opts doc "simple_3port" with
    | Error e -> failwithf "emit failed: %s" (ToLef.formatError e)
    | Ok lef ->
        lef |> should haveSubstring "    SYMMETRY X Y ;"

[<Fact>]
let ``OmitForeignOffset drops the trailing 0 0 from FOREIGN`` () =
    let doc = readFixture "simple_3port.rkt"
    let opts = { ToLef.EmitOptions.defaults with OmitForeignOffset = true }
    match ToLef.emitCell opts doc "simple_3port" with
    | Error e -> failwithf "emit failed: %s" (ToLef.formatError e)
    | Ok lef ->
        lef |> should haveSubstring "FOREIGN simple_3port ;"
        // No `FOREIGN <name> 0 0` form should appear.
        lef.Contains "FOREIGN simple_3port 0 0" |> should equal false

[<Fact>]
let ``LegacyZeroShortForm emits 0 instead of 0.000 when precision is fixed`` () =
    let doc = readFixture "simple_3port.rkt"
    let opts =
        { ToLef.EmitOptions.defaults with
            DecimalPrecision = Some 3
            LegacyZeroShortForm = true }
    match ToLef.emitCell opts doc "simple_3port" with
    | Error e -> failwithf "emit failed: %s" (ToLef.formatError e)
    | Ok lef ->
        // Origin is exactly (0,0) — emit as "0 0", not "0.000 0.000".
        lef |> should haveSubstring "ORIGIN 0 0 ;"
        // Non-zero coords still respect the precision.
        lef |> should haveSubstring "SIZE 5.000 BY 2.000 ;"

// ─── First consumer (P5) — Track 09 array shape ────────────────────────

[<Fact>]
let ``Track 09 CIM array fixture emits valid LEF with every port type`` () =
    let doc = readFixture "track09_cim_array.rkt"
    match ToLef.emitCell ToLef.EmitOptions.defaults doc "cim_reram_array_256x64" with
    | Error e -> failwithf "emit failed: %s" (ToLef.formatError e)
    | Ok lef ->
        // Macro shape: SIZE in microns from the bbox (295 × 369 µm).
        lef |> should haveSubstring "MACRO cim_reram_array_256x64"
        lef |> should haveSubstring "SIZE 295 BY 369 ;"
        // Power on met4.
        lef |> should haveSubstring "PIN VDDA1"
        lef |> should haveSubstring "USE POWER ;"
        // Ground on met4.
        lef |> should haveSubstring "PIN VSS"
        lef |> should haveSubstring "USE GROUND ;"
        // Wordlines on met3.
        lef |> should haveSubstring "PIN WL[0]"
        lef |> should haveSubstring "PIN WL[255]"
        // Bitlines on met2.
        lef |> should haveSubstring "PIN BL[0]"
        lef |> should haveSubstring "PIN SL[0]"
        // Multi-wordline enable per-row.
        lef |> should haveSubstring "PIN MWL_EN[0]"
        lef |> should haveSubstring "PIN MWL_EN[255]"
        // Owner prop is stored but not LEF-emitted (description is).
        lef |> should haveSubstring "# DESCRIPTION: 256x64 ReRAM CIM array - Track 09"
        // Closing END LIBRARY trailer.
        lef.TrimEnd().EndsWith "END LIBRARY" |> should equal true

[<Fact>]
let ``Track 09 fixture round-trips deterministically`` () =
    // Same input twice → byte-equal output. Mirrors the determinism
    // test on simple_3port but at the consumer's actual scale.
    let doc = readFixture "track09_cim_array.rkt"
    let opts = ToLef.EmitOptions.defaults
    let r1 = ToLef.emitCell opts doc "cim_reram_array_256x64"
    let r2 = ToLef.emitCell opts doc "cim_reram_array_256x64"
    r1 |> should equal r2

// ─── Obstruction policy (P2) ──────────────────────────────────────────

[<Fact>]
let ``default ObsPolicy = FullSize ["met1";"met2"] emits two full-size obs rects`` () =
    let doc = readFixture "simple_3port.rkt"
    match ToLef.emitCell ToLef.EmitOptions.defaults doc "simple_3port" with
    | Error e -> failwithf "emit failed: %s" (ToLef.formatError e)
    | Ok lef ->
        lef |> should haveSubstring "OBS"
        // Two layers in default policy: met1 + met2. Full-bbox rect
        // is 0 0 (width) (height) — 5 × 2 µm here.
        lef |> should haveSubstring "LAYER met1 ;"
        lef |> should haveSubstring "LAYER met2 ;"
        lef |> should haveSubstring "RECT 0 0 5 2 ;"

[<Fact>]
let ``ObsPolicy = NoObs suppresses the OBS block`` () =
    let doc = readFixture "simple_3port.rkt"
    let opts = { ToLef.EmitOptions.defaults with Obstructions = ToLef.NoObs }
    match ToLef.emitCell opts doc "simple_3port" with
    | Error e -> failwithf "emit failed: %s" (ToLef.formatError e)
    | Ok lef ->
        lef.Contains "OBS\n" |> should equal false

[<Fact>]
let ``DerivedFromGeometry emits a per-layer bbox of drawn geometry`` () =
    let doc = readFixture "with_geometry.rkt"
    let opts =
        { ToLef.EmitOptions.defaults with
            Obstructions = ToLef.DerivedFromGeometry [ "met1"; "met2" ] }
    match ToLef.emitCell opts doc "with_geometry" with
    | Error e -> failwithf "emit failed: %s" (ToLef.formatError e)
    | Ok lef ->
        // met1 rect 100..400 x 200..800 → 0.1 0.2 0.4 0.8 µm
        lef |> should haveSubstring "LAYER met1 ;\n        RECT 0.1 0.2 0.4 0.8 ;"
        // met2 poly 1000..1200 x 100..300 → 1.0 0.1 1.2 0.3 µm
        lef |> should haveSubstring "LAYER met2 ;\n        RECT 1 0.1 1.2 0.3 ;"

[<Fact>]
let ``DerivedFromGeometry skips layers with no geometry`` () =
    let doc = readFixture "with_geometry.rkt"
    let opts =
        { ToLef.EmitOptions.defaults with
            Obstructions = ToLef.DerivedFromGeometry [ "met1"; "met3" ] }
    match ToLef.emitCell opts doc "with_geometry" with
    | Error e -> failwithf "emit failed: %s" (ToLef.formatError e)
    | Ok lef ->
        // met1 present, met3 absent — only one LAYER inside OBS.
        lef |> should haveSubstring "LAYER met1 ;"
        // Should NOT find met3 anywhere in the OBS block.
        let obsStart = lef.IndexOf "OBS\n"
        let obsEnd = lef.IndexOf("END\n", obsStart)
        let obsBlock = lef.Substring(obsStart, obsEnd - obsStart)
        obsBlock.Contains "met3" |> should equal false

[<Fact>]
let ``FullSize with a custom layer list emits one rect per layer`` () =
    let doc = readFixture "simple_3port.rkt"
    let opts =
        { ToLef.EmitOptions.defaults with
            Obstructions = ToLef.FullSize [ "met1"; "met2"; "met3" ] }
    match ToLef.emitCell opts doc "simple_3port" with
    | Error e -> failwithf "emit failed: %s" (ToLef.formatError e)
    | Ok lef ->
        lef |> should haveSubstring "LAYER met1 ;"
        lef |> should haveSubstring "LAYER met2 ;"
        lef |> should haveSubstring "LAYER met3 ;"

[<Fact>]
let ``bbox prop with wrong arity fails with MissingBboxProp`` () =
    // A `(bbox 0 0 100)` (3 ints) doesn't match the canonical 4-int
    // shape — findBbox treats it as absent and the emitter raises
    // MissingBboxProp. Acceptable for v1; a future revision can
    // distinguish "missing" from "malformed" via a dedicated error.
    let cell : Cell = {
        Name = "wrong_arity"
        Meta = None
        Comments = []
        SubFormComments = Map.empty
        Elements = [
            PropsEl {
                Items = [
                    { Key = "bbox"
                      Value = PvTuple [ PvInt 0L; PvInt 0L; PvInt 100L ] }
                ]
                Comments = []
                SubFormComments = Map.empty
            }
        ]
    }
    let doc = { emptyDocument with Cells = [ cell ] }
    match ToLef.emitCell ToLef.EmitOptions.defaults doc "wrong_arity" with
    | Error (ToLef.MissingBboxProp _) -> ()
    | other -> failwithf "expected MissingBboxProp on wrong arity, got %A" other
