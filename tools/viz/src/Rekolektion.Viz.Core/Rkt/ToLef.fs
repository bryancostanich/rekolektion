module Rekolektion.Viz.Core.Rkt.ToLef

/// LEF 5.7 emitter that consumes a canonical `Rkt.Document`. Each
/// `(cell …)` becomes one `MACRO … END <name>` block. `SIZE` and
/// `ORIGIN` derive from the cell's declared `(props (bbox x0 y0 x1
/// y1))` — never from a polygon-bbox query. Pin entries derive from
/// first-class `(port …)` elements; obstructions derive from drawn
/// geometry under the configured policy.
///
/// See `docs/plans/rkt_to_lef_emitter.md` for the full mapping
/// reference and acceptance criteria.

open System.Text
open System.Globalization
open Rekolektion.Viz.Core.Rkt.Types

// ─── Emit options ───────────────────────────────────────────────────────

type ObsPolicy =
    | FullSize of layers: string list
    | DerivedFromGeometry of layers: string list
    /// Per-layer mix of full-size obs and y-axis bands that exclude
    /// strip regions at the cell's top and bottom (where pins live).
    /// Matches the legacy `lef_generator.py` met3 OBS shape: met1/met2
    /// get full-size rects, met3 gets a band whose y-extent stops
    /// short of the pin strips so a router can still drop access into
    /// them. Band coordinates are in microns, in the LEF-local frame
    /// (after the bbox-origin shift).
    | BandExcluding of fullLayers: string list * bandLayers: (string * decimal * decimal) list
    | NoObs

type PinCase =
    | Verbatim
    | Uppercase

type EmitOptions = {
    /// Override the macro name. `None` uses the cell name verbatim.
    MacroName: string option
    /// Pin-name case policy. `Uppercase` matches the v1 Liberty
    /// convention; `Verbatim` preserves the port's name as authored.
    PinCase: PinCase
    /// Obstruction policy. See `ObsPolicy` for variants.
    Obstructions: ObsPolicy
    /// When `true`, emit the cell's `(props (description …))` as a
    /// `# DESCRIPTION:` comment immediately above the `MACRO` line.
    EmitDescriptionComment: bool
    /// Manufacturing-grid snap in microns. Default 0.005 (sky130
    /// 5 nm). Any input coordinate that doesn't already land on the
    /// grid is an error — the cell's `.rkt` should be authored on
    /// the grid; the emitter does not snap silently.
    MfgGridUm: decimal
    /// Fixed decimal precision for emitted µm coordinates. `None`
    /// uses minimal-trim formatting (`70.49`); `Some n` formats with
    /// exactly `n` decimals zero-padded (`70.490` at n=3, matching
    /// the legacy `lef_generator.py` style). Both forms are valid
    /// LEF — this is purely cosmetic.
    DecimalPrecision: int option
    /// Emit `SHAPE ABUTMENT ;` on every PIN whose port flags include
    /// `Power` or `Ground`. Matches the legacy `lef_generator.py`
    /// PDN-abutment convention. Default false; chip-level PDN
    /// integration may need it.
    EmitAbutmentShape: bool
    /// Emit `SYMMETRY <text> ;` at the macro level. `None` omits the
    /// clause (default); `Some "X Y"` matches the legacy
    /// `lef_generator.py` rectangular-shape hint.
    Symmetry: string option
    /// When `true`, emit `FOREIGN <name> ;` without the trailing
    /// `0 0` offset. Both forms are valid LEF; the new emitter
    /// defaults to including the offset (`false`).
    OmitForeignOffset: bool
    /// When `true` together with `DecimalPrecision = Some n`, render
    /// the `ORIGIN` clause as `ORIGIN 0 0 ;` (short form) when both
    /// coords are exactly zero, instead of `ORIGIN 0.000 0.000 ;`.
    /// Matches the legacy `lef_generator.py` ORIGIN convention. OBS
    /// rects and PIN coords are NOT affected — they keep fixed
    /// precision. Default `false`.
    LegacyZeroShortForm: bool
}

module EmitOptions =
    let defaults : EmitOptions = {
        MacroName = None
        PinCase = Verbatim
        Obstructions = FullSize [ "met1"; "met2" ]
        EmitDescriptionComment = true
        DecimalPrecision = None
        MfgGridUm = 0.005m
        EmitAbutmentShape = false
        Symmetry = None
        OmitForeignOffset = false
        LegacyZeroShortForm = false
    }

// ─── Errors ─────────────────────────────────────────────────────────────

type EmitError =
    /// Cell is missing the `(props (bbox x0 y0 x1 y1))` declaration
    /// that anchors `SIZE`/`ORIGIN`. Required by design — the emitter
    /// never falls back to a polygon-bbox query (the
    /// anti-pattern the cell-level convention exists to prevent).
    | MissingBboxProp of cellName: string
    /// Cell carries a `(bbox …)` prop whose value isn't a 4-int
    /// tuple (wrong arity, wrong type). Surfaces as a usable error
    /// rather than a silent zero-size macro.
    | InvalidBbox of cellName: string * reason: string
    /// A `(port …)` element references a layer the emitter can't map
    /// to a LEF layer name (typically `unknown:N/D` since LEF has no
    /// concept of bare number/datatype pairs).
    | UnknownLayer of layerRef: Layer * cellName: string
    /// Port shape variant the LEF emitter can't represent.
    | UnsupportedPortShape of portName: string * cellName: string
    /// Port flags combine illegally (e.g., `power` + `ground`,
    /// `signal` + `clock`).
    | ConflictingFlags of portName: string * flags: PortFlag list
    /// Coordinate doesn't land on the manufacturing grid declared in
    /// `EmitOptions.MfgGridUm`. Reported as the absolute (post-shift)
    /// value in microns to make the off-grid offset obvious.
    | OffGridCoordinate of axis: string * value: decimal * cellName: string
    /// `emitCell` was asked for a cell name not present in the
    /// document.
    | NoSuchCell of cellName: string

let formatError (err: EmitError) : string =
    match err with
    | MissingBboxProp cell ->
        sprintf "cell '%s' has no (props (bbox …)) declaration — required for LEF SIZE" cell
    | InvalidBbox (cell, reason) ->
        sprintf "cell '%s' has invalid bbox: %s" cell reason
    | UnknownLayer (Named (pdk, name), cell) ->
        sprintf "cell '%s' uses layer %s:%s which has no LEF mapping" cell pdk name
    | UnknownLayer (Unknown (n, d), cell) ->
        sprintf "cell '%s' uses unknown:%d/%d which has no LEF layer name" cell n d
    | UnsupportedPortShape (port, cell) ->
        sprintf "cell '%s' port '%s' uses a shape the LEF emitter can't represent" cell port
    | ConflictingFlags (port, flags) ->
        sprintf "port '%s' has conflicting flags: %A" port flags
    | OffGridCoordinate (axis, v, cell) ->
        sprintf "cell '%s': coordinate %M on %s axis is off-grid" cell v axis
    | NoSuchCell cell ->
        sprintf "no cell named '%s' in document" cell

// ─── Internal helpers ───────────────────────────────────────────────────

/// Resolve a `.rkt` layer reference to the LEF layer name.
/// `Named("sky130", "met1")` → `Some "met1"`. Unknown PDKs and
/// `Unknown(n, d)` return `None` (caller turns into `UnknownLayer`).
let internal layerToLefName (layer: Layer) : string option =
    match layer with
    | Named ("sky130", name) -> Some name
    | Named (_, _) -> None
    | Unknown (_, _) -> None

/// Find the cell-level bbox property (a `(props (bbox …))` inside
/// the cell), if any. Returns the 4-int tuple in `.rkt` DBU.
let internal findBbox (cell: Cell) : (int64 * int64 * int64 * int64) option =
    cell.Elements
    |> List.tryPick (fun e ->
        match e with
        | PropsEl props ->
            props.Items
            |> List.tryPick (fun p ->
                if p.Key = "bbox" then
                    match p.Value with
                    | PvTuple [ PvInt a; PvInt b; PvInt c; PvInt d ] ->
                        Some (a, b, c, d)
                    | _ -> None
                else None)
        | _ -> None)

/// Read the cell's `(props (description "…"))` string, if any.
let internal findDescription (cell: Cell) : string option =
    cell.Elements
    |> List.tryPick (fun e ->
        match e with
        | PropsEl props ->
            props.Items
            |> List.tryPick (fun p ->
                if p.Key = "description" then
                    match p.Value with
                    | PvString s -> Some s
                    | PvAtom s -> Some s
                    | _ -> None
                else None)
        | _ -> None)

/// DBU → microns. `dbuNm` is nm per DBU, `uuUm` is µm per UU. Since
/// the `.rkt` storage is integer DBU and we want a decimal µm count
/// suitable for LEF output, the conversion is exact when
/// `dbuNm * 1000 / uuUm` divides evenly (always true for sky130
/// defaults where dbuNm=1, uuUm=1: 1 DBU = 1 nm = 0.001 µm).
let internal dbuToUm (units: Units) (v: int64) : decimal =
    // 1 DBU = dbuNm nm. 1 µm = 1000 nm. UU is uuUm µm.
    // result = v * dbuNm / (1000 * uuUm)
    let num = decimal v * decimal units.DbuNm
    let den = decimal 1000 * decimal units.UuUm
    num / den

/// Format a decimal µm coordinate for LEF output. With
/// `precision = None`, uses minimal-trim formatting (`70.49`); with
/// `Some n`, emits exactly `n` decimals zero-padded (`70.490` at
/// n=3, matching `lef_generator.py`'s convention).
let internal fmtUm (precision: int option) (v: decimal) : string =
    match precision with
    | None -> v.ToString("0.######", CultureInfo.InvariantCulture)
    | Some n ->
        // Build a "0.000…" pattern with `n` zeros after the dot.
        let pattern =
            if n <= 0 then "0"
            else "0." + String.replicate n "0"
        v.ToString(pattern, CultureInfo.InvariantCulture)

/// Default emission helper — always respects `DecimalPrecision`.
/// Use `fmtUmOriginOpts` at the ORIGIN site when the legacy
/// short-form policy needs to apply.
let internal fmtUmOpts (options: EmitOptions) (v: decimal) : string =
    fmtUm options.DecimalPrecision v

/// Special emission for the ORIGIN clause: when
/// `LegacyZeroShortForm` is set AND the value is exactly zero,
/// emit a bare `0` to match legacy `ORIGIN 0 0 ;` shorthand.
let internal fmtUmOrigin (options: EmitOptions) (v: decimal) : string =
    if options.LegacyZeroShortForm && v = 0m then "0"
    else fmtUm options.DecimalPrecision v

/// Check that a µm coordinate lands on the manufacturing grid (a
/// multiple of `mfgGridUm`). Returns `Ok ()` when on-grid, `Error
/// off-by-magnitude` otherwise.
let internal checkOnGrid (mfgGridUm: decimal) (v: decimal) : Result<unit, decimal> =
    // `v % grid` in decimal: |v / grid - round(v / grid)|
    let ratio = v / mfgGridUm
    let nearest = System.Math.Round(ratio, System.MidpointRounding.ToEven)
    let drift = ratio - nearest
    if abs drift < 0.0000001m then Ok ()
    else Error v

// ─── Pin emission (P1) ──────────────────────────────────────────────────

/// LEF direction string. `Unspecified` returns `None` so the caller
/// can omit the `DIRECTION` clause entirely.
let internal directionToLef (dir: PortDirection) : string option =
    match dir with
    | Input -> Some "INPUT"
    | Output -> Some "OUTPUT"
    | Inout -> Some "INOUT"
    | Unspecified -> None

/// Classify a port's `Flags` set into `(USE, CLASS)`. Mutually
/// exclusive `power/ground/clock/analog/signal` flags collapse to a
/// single `USE`; `scan` adds `CLASS SCAN` when combined with `signal`
/// or no other flag. Any other combination is a conflict.
let internal classifyFlags
    (portName: string)
    (flags: PortFlag list)
    : Result<string * string option, EmitError> =
    let exclusiveSet = [ Power; Ground; Clock; Analog; Signal ]
    let exclusives = flags |> List.filter (fun f -> List.contains f exclusiveSet)
    let hasScan = List.contains Scan flags
    if exclusives.Length > 1 then
        Error (ConflictingFlags (portName, flags))
    else
        let usePart =
            match exclusives with
            | [ Power ] -> "POWER"
            | [ Ground ] -> "GROUND"
            | [ Clock ] -> "CLOCK"
            | [ Analog ] -> "ANALOG"
            | [ Signal ] -> "SIGNAL"
            | _ -> "SIGNAL"
        if hasScan then
            if usePart = "SIGNAL" then Ok ("SIGNAL", Some "SCAN")
            else Error (ConflictingFlags (portName, flags))
        else
            Ok (usePart, None)

/// Apply the `PinCase` policy to a port name.
let internal applyPinCase (case: PinCase) (name: string) : string =
    match case with
    | Verbatim -> name
    | Uppercase -> name.ToUpperInvariant()

/// Shift a DBU coordinate into the LEF-local frame (where (0,0)
/// corresponds to the cell's bbox lower-left), convert to µm, and
/// enforce the on-grid invariant. Returns the formatted string on
/// success.
let internal coordToLef
    (options: EmitOptions)
    (units: Units)
    (cellName: string)
    (axisLabel: string)
    (originDbu: int64)
    (valueDbu: int64)
    : Result<string, EmitError> =
    let shiftedDbu = valueDbu - originDbu
    let um = dbuToUm units shiftedDbu
    match checkOnGrid options.MfgGridUm um with
    | Ok () -> Ok (fmtUmOpts options um)
    | Error v -> Error (OffGridCoordinate (axisLabel, v, cellName))

/// Emit a single PORT block (one geometry on one layer). LEF's PIN
/// allows multiple PORT blocks per pin (for pins with disjoint
/// shapes); we currently emit one PORT per port — sufficient for
/// `.rkt`'s one-shape-per-port model.
let internal emitPortBlock
    (options: EmitOptions)
    (units: Units)
    (cell: Cell)
    (bboxX0: int64) (bboxY0: int64)
    (port: Port)
    : Result<string, EmitError> =
    match layerToLefName port.Layer with
    | None -> Error (UnknownLayer (port.Layer, cell.Name))
    | Some lefLayer ->
        match classifyFlags port.Name port.Flags with
        | Error e -> Error e
        | Ok (usePart, classPart) ->
            let pinName = applyPinCase options.PinCase port.Name
            // Geometry: shift every coord by (-bboxX0, -bboxY0),
            // convert DBU → µm, enforce on-grid.
            let toXStr v = coordToLef options units cell.Name "port-x" bboxX0 v
            let toYStr v = coordToLef options units cell.Name "port-y" bboxY0 v
            let shapeRes : Result<string, EmitError> =
                match port.Shape with
                | RectShape (x1, y1, x2, y2) ->
                    match toXStr x1, toYStr y1, toXStr x2, toYStr y2 with
                    | Ok a, Ok b, Ok c, Ok d ->
                        Ok (sprintf "            RECT %s %s %s %s ;\n" a b c d)
                    | Error e, _, _, _ -> Error e
                    | _, Error e, _, _ -> Error e
                    | _, _, Error e, _ -> Error e
                    | _, _, _, Error e -> Error e
                | PolyShape pts ->
                    if pts.IsEmpty then
                        Error (UnsupportedPortShape (port.Name, cell.Name))
                    else
                        let rec walk acc = function
                            | [] -> Ok (List.rev acc)
                            | (p: Point) :: rest ->
                                match toXStr p.X, toYStr p.Y with
                                | Ok x, Ok y -> walk (y :: x :: acc) rest
                                | Error e, _ -> Error e
                                | _, Error e -> Error e
                        match walk [] pts with
                        | Error e -> Error e
                        | Ok flat ->
                            let inner = String.concat " " flat
                            Ok (sprintf "            POLYGON %s ;\n" inner)
            match shapeRes with
            | Error e -> Error e
            | Ok shapeStr ->
                let sb = StringBuilder()
                sb.Append(sprintf "    PIN %s\n" pinName) |> ignore
                match directionToLef port.Direction with
                | Some d -> sb.Append(sprintf "        DIRECTION %s ;\n" d) |> ignore
                | None -> ()
                sb.Append(sprintf "        USE %s ;\n" usePart) |> ignore
                match classPart with
                | Some c -> sb.Append(sprintf "        CLASS %s ;\n" c) |> ignore
                | None -> ()
                // Optional `SHAPE ABUTMENT ;` for POWER / GROUND
                // pins — matches the legacy PDN-abutment convention
                // when consumers need it.
                if options.EmitAbutmentShape
                   && (List.contains Power port.Flags
                       || List.contains Ground port.Flags) then
                    sb.Append "        SHAPE ABUTMENT ;\n" |> ignore
                sb.Append "        PORT\n" |> ignore
                sb.Append(sprintf "            LAYER %s ;\n" lefLayer) |> ignore
                sb.Append shapeStr |> ignore
                sb.Append "        END\n" |> ignore
                sb.Append(sprintf "    END %s\n" pinName) |> ignore
                Ok (sb.ToString())

/// Emit every `PortEl` in a cell, in declaration order. First error
/// short-circuits.
let internal emitPorts
    (options: EmitOptions)
    (units: Units)
    (cell: Cell)
    (bboxX0: int64) (bboxY0: int64)
    : Result<string, EmitError> =
    let rec walk acc = function
        | [] -> Ok (List.rev acc |> String.concat "")
        | el :: rest ->
            match el with
            | PortEl p ->
                match emitPortBlock options units cell bboxX0 bboxY0 p with
                | Error e -> Error e
                | Ok block -> walk (block :: acc) rest
            | _ -> walk acc rest
    walk [] cell.Elements

// ─── Obstruction emission (P2) ──────────────────────────────────────────

/// Bounding box of a single geometry element on a specific layer.
/// `None` when the element isn't on `layer` or carries no
/// representable extent (labels, ports — ports are excluded by
/// design since their geometry is already declared via PIN/PORT).
let internal elementBbox
    (layer: string)
    (el: Element)
    : (int64 * int64 * int64 * int64) option =
    let onLayer (l: Layer) =
        match l with
        | Named ("sky130", name) -> name = layer
        | _ -> false
    match el with
    | RectEl r when onLayer r.Layer ->
        Some (min r.X1 r.X2, min r.Y1 r.Y2, max r.X1 r.X2, max r.Y1 r.Y2)
    | PolyEl p when onLayer p.Layer && not p.Points.IsEmpty ->
        let xs = p.Points |> List.map (fun pt -> pt.X)
        let ys = p.Points |> List.map (fun pt -> pt.Y)
        Some (List.min xs, List.min ys, List.max xs, List.max ys)
    | PathEl p when onLayer p.Layer && not p.Points.IsEmpty ->
        // Conservative bbox: extend by half-width on each side.
        let half = p.Width / 2L
        let xs = p.Points |> List.map (fun pt -> pt.X)
        let ys = p.Points |> List.map (fun pt -> pt.Y)
        Some (List.min xs - half, List.min ys - half,
              List.max xs + half, List.max ys + half)
    | _ -> None

/// Union (bbox-union, not polygon-union) of every element's bbox on
/// `layer`. Returns `None` when no geometry exists on that layer in
/// this cell.
///
/// **Approximation note**: this collapses disjoint geometry into a
/// single rectangle. Adequate for v1's "block stdcell placement"
/// goal because P&R tools treat OBS as a forbidden zone — an
/// over-conservative obs is safe (it just blocks more area).
/// Polygon-precise per-component obs is future work.
let internal layerBbox
    (cell: Cell)
    (layer: string)
    : (int64 * int64 * int64 * int64) option =
    cell.Elements
    |> List.choose (elementBbox layer)
    |> function
       | [] -> None
       | boxes ->
           let x0 = boxes |> List.map (fun (a, _, _, _) -> a) |> List.min
           let y0 = boxes |> List.map (fun (_, b, _, _) -> b) |> List.min
           let x1 = boxes |> List.map (fun (_, _, c, _) -> c) |> List.max
           let y1 = boxes |> List.map (fun (_, _, _, d) -> d) |> List.max
           Some (x0, y0, x1, y1)

/// Format an `OBS … END` block per the configured `ObsPolicy`.
let internal emitObsBlock
    (options: EmitOptions)
    (units: Units)
    (cell: Cell)
    (bboxX0: int64) (bboxY0: int64)
    (bboxX1: int64) (bboxY1: int64)
    : Result<string, EmitError> =
    let emitLayerRects (layers: string list) (bboxFn: string -> (int64 * int64 * int64 * int64) option) =
        let rec walk (acc: string list) = function
            | [] -> Ok (List.rev acc |> String.concat "")
            | layer :: rest ->
                match bboxFn layer with
                | None -> walk acc rest
                | Some (x0, y0, x1, y1) ->
                    // Coordinate shift + on-grid check.
                    let toXStr v = coordToLef options units cell.Name "obs-x" bboxX0 v
                    let toYStr v = coordToLef options units cell.Name "obs-y" bboxY0 v
                    match toXStr x0, toYStr y0, toXStr x1, toYStr y1 with
                    | Ok a, Ok b, Ok c, Ok d ->
                        let chunk =
                            sprintf "        LAYER %s ;\n        RECT %s %s %s %s ;\n"
                                layer a b c d
                        walk (chunk :: acc) rest
                    | Error e, _, _, _ -> Error e
                    | _, Error e, _, _ -> Error e
                    | _, _, Error e, _ -> Error e
                    | _, _, _, Error e -> Error e
        walk [] layers
    let emitBandRects (bands: (string * decimal * decimal) list) =
        // Caller supplies band y-extents in µm, already in the
        // LEF-local frame (post bbox-origin shift). X-extent runs
        // the full macro width.
        let widthUm = dbuToUm units (bboxX1 - bboxX0)
        let rec walk (acc: string list) = function
            | [] -> Ok (List.rev acc |> String.concat "")
            | (layer, y0Um, y1Um) :: rest ->
                let onGrid v =
                    match checkOnGrid options.MfgGridUm v with
                    | Ok () -> Ok (fmtUmOpts options v)
                    | Error v -> Error (OffGridCoordinate ("obs-band-y", v, cell.Name))
                let xMaxStr = fmtUmOpts options widthUm
                match onGrid y0Um, onGrid y1Um with
                | Ok y0Str, Ok y1Str ->
                    let chunk =
                        sprintf "        LAYER %s ;\n        RECT %s %s %s %s ;\n"
                            layer (fmtUmOpts options 0m) y0Str xMaxStr y1Str
                    walk (chunk :: acc) rest
                | Error e, _ -> Error e
                | _, Error e -> Error e
        walk [] bands
    let bodyRes : Result<string, EmitError> =
        match options.Obstructions with
        | NoObs -> Ok ""
        | FullSize layers ->
            // Full-bbox rect on every requested layer, no per-layer
            // geometry check. This matches the legacy v2 SRAM policy
            // documented in `lef_generator.py`.
            emitLayerRects layers (fun _ -> Some (bboxX0, bboxY0, bboxX1, bboxY1))
        | DerivedFromGeometry layers ->
            // Per-layer bbox union from the cell's drawn geometry.
            emitLayerRects layers (fun l -> layerBbox cell l)
        | BandExcluding (fullLayers, bandLayers) ->
            // Full-size rects for fullLayers, then per-band rects
            // for bandLayers. Order: full first, bands second —
            // matches the legacy lef_generator.py emit order
            // (met1/met2 full, then met3 band).
            match emitLayerRects fullLayers (fun _ -> Some (bboxX0, bboxY0, bboxX1, bboxY1)) with
            | Error e -> Error e
            | Ok fullStr ->
                match emitBandRects bandLayers with
                | Error e -> Error e
                | Ok bandStr -> Ok (fullStr + bandStr)
    match bodyRes with
    | Error e -> Error e
    | Ok "" -> Ok ""
    | Ok body ->
        let sb = StringBuilder()
        sb.Append "    OBS\n" |> ignore
        sb.Append body |> ignore
        sb.Append "    END\n" |> ignore
        Ok (sb.ToString())

// ─── Macro body emission (P0: header + SIZE/ORIGIN only) ────────────────

/// Build the MACRO block for one cell. Currently P0: SIZE/ORIGIN
/// from the cell's `(props (bbox …))`. Pins and obstructions land in
/// P1 / P2.
let internal emitMacroBlock
    (options: EmitOptions)
    (units: Units)
    (cell: Cell)
    : Result<string, EmitError> =
    match findBbox cell with
    | None -> Error (MissingBboxProp cell.Name)
    | Some (x0, y0, x1, y1) when x1 <= x0 || y1 <= y0 ->
        Error (InvalidBbox (cell.Name, sprintf "non-positive extent (%d %d %d %d)" x0 y0 x1 y1))
    | Some (x0, y0, x1, y1) ->
        let widthUm = dbuToUm units (x1 - x0)
        let heightUm = dbuToUm units (y1 - y0)
        // ORIGIN: the LEF-local frame's (0,0) maps to (-x0, -y0) in
        // the .rkt frame. LEF uses ORIGIN to declare the offset of
        // the LEF coordinate system relative to the cell's geometry.
        let originXUm = dbuToUm units (- x0)
        let originYUm = dbuToUm units (- y0)
        // Off-grid checks on the user-visible quantities (the size
        // and the origin). Catches authoring drift early; LEF
        // consumers downstream (OpenROAD) emit DRT-0416 on off-grid.
        let coords =
            [ "size-x", widthUm
              "size-y", heightUm
              "origin-x", originXUm
              "origin-y", originYUm ]
        let offGrid =
            coords
            |> List.tryPick (fun (axis, v) ->
                match checkOnGrid options.MfgGridUm v with
                | Ok () -> None
                | Error _ -> Some (OffGridCoordinate (axis, v, cell.Name)))
        match offGrid with
        | Some err -> Error err
        | None ->
            match emitPorts options units cell x0 y0 with
            | Error e -> Error e
            | Ok portsBlock ->
                match emitObsBlock options units cell x0 y0 x1 y1 with
                | Error e -> Error e
                | Ok obsBlock ->
                    let macroName =
                        match options.MacroName with
                        | Some n -> n
                        | None -> cell.Name
                    let sb = StringBuilder()
                    if options.EmitDescriptionComment then
                        match findDescription cell with
                        | Some desc ->
                            sb.Append(sprintf "# DESCRIPTION: %s\n" desc) |> ignore
                        | None -> ()
                    sb.Append(sprintf "MACRO %s\n" macroName) |> ignore
                    sb.Append "    CLASS BLOCK ;\n" |> ignore
                    let foreignTail =
                        if options.OmitForeignOffset then ""
                        else " 0 0"
                    sb.Append(sprintf "    FOREIGN %s%s ;\n" macroName foreignTail)
                        |> ignore
                    match options.Symmetry with
                    | Some s -> sb.Append(sprintf "    SYMMETRY %s ;\n" s) |> ignore
                    | None -> ()
                    sb.Append(sprintf "    ORIGIN %s %s ;\n"
                                       (fmtUmOrigin options originXUm)
                                       (fmtUmOrigin options originYUm))
                        |> ignore
                    sb.Append(sprintf "    SIZE %s BY %s ;\n"
                                       (fmtUmOpts options widthUm)
                                       (fmtUmOpts options heightUm))
                        |> ignore
                    sb.Append portsBlock |> ignore
                    sb.Append obsBlock |> ignore
                    sb.Append(sprintf "END %s\n" macroName) |> ignore
                    Ok (sb.ToString())

// ─── LEF header ─────────────────────────────────────────────────────────

/// Canonical LEF 5.7 header. Static across emit calls — every LEF
/// rekolektion ships uses the same VERSION / BUSBITCHARS /
/// DIVIDERCHAR / units configuration. Mirrors the legacy
/// `lef_generator.py` header preamble.
let internal lefHeader () : string =
    "VERSION 5.7 ;\n"
    + "BUSBITCHARS \"[]\" ;\n"
    + "DIVIDERCHAR \"/\" ;\n"
    + "UNITS\n"
    + "    DATABASE MICRONS 1000 ;\n"
    + "END UNITS\n\n"

let internal lefTrailer () : string = "\nEND LIBRARY\n"

// ─── Public API ─────────────────────────────────────────────────────────

/// Emit a single cell as a complete LEF file (header + one MACRO).
let emitCell
    (options: EmitOptions)
    (doc: Document)
    (cellName: string)
    : Result<string, EmitError> =
    match doc.Cells |> List.tryFind (fun c -> c.Name = cellName) with
    | None -> Error (NoSuchCell cellName)
    | Some cell ->
        match emitMacroBlock options doc.Units cell with
        | Error e -> Error e
        | Ok block ->
            Ok (lefHeader() + block + lefTrailer())

/// Emit every cell in the document as a single LEF file. Cells are
/// emitted in declaration order. First error short-circuits and is
/// returned to the caller.
let emitDocument
    (options: EmitOptions)
    (doc: Document)
    : Result<string, EmitError> =
    let rec walk acc = function
        | [] -> Ok (List.rev acc |> String.concat "")
        | (c: Cell) :: rest ->
            match emitMacroBlock options doc.Units c with
            | Error e -> Error e
            | Ok block -> walk (block :: acc) rest
    match walk [] doc.Cells with
    | Error e -> Error e
    | Ok body -> Ok (lefHeader() + body + lefTrailer())
