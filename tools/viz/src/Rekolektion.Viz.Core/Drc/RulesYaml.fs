module Rekolektion.Viz.Core.Drc.RulesYaml

open System
open System.Collections.Generic
open YamlDotNet.Serialization
open YamlDotNet.Serialization.NamingConventions
open Rekolektion.Viz.Core.Drc.Rules

// ADR-0004 — DRC rules in YAML, with a base ruleset shipped in the
// repo and user override files layered on top by strict by-name
// merge. This module owns the schema, parser, serializer, and
// merger. Disk I/O and integration with the live engine are
// separate concerns (callers feed in YAML strings and consume the
// resulting `Rules.Rule` list).
//
// Override file semantics:
//   - A rule with the same `name` as a base rule REPLACES the base
//     entry entirely. No partial-field stitching.
//   - A rule with a new name is added to the effective set.
//   - `disabled: true` on a rule by name removes it from the
//     effective set (whether or not it was in the base).
//
// Provenance: the loader returns a Map<ruleName, sourceFile> so
// the Inspector panel can surface "this met2 spacing came from
// overrides/v1_tapeout.yaml" — see ADR-0004.

// --- YAML schema (POCOs for YamlDotNet) --------------------------------

[<AllowNullLiteral>]
type YamlLayer() =
    member val Number = 0 with get, set
    member val Datatype = 0 with get, set
    member val Name = "" with get, set

[<AllowNullLiteral>]
type YamlRule() =
    /// Magic-compatible rule identifier (e.g. `met1.2`). Used as the
    /// merge key and as `Rules.Rule`'s name field.
    member val Name = "" with get, set
    /// One of: width, spacing, cross-spacing, enclosure, endcap,
    /// min-area, asym-enclosure, boundary-crossing,
    /// implant-outside-well-spacing.
    member val Kind = "" with get, set
    /// When true on an override, removes the rule of this name
    /// from the effective set even if present in the base.
    member val Disabled = false with get, set
    /// Optional per-rule override of the kind's default live
    /// eligibility (ADR-0003). Null = use the kind's default
    /// (Rules.isLiveEligible).
    member val LiveEligible : Nullable<bool> = Nullable() with get, set

    // Layer fields — each rule kind uses a subset. Null on any
    // field the kind doesn't need.
    member val Layer : YamlLayer = null with get, set
    member val LayerA : YamlLayer = null with get, set
    member val LayerB : YamlLayer = null with get, set
    member val Outer : YamlLayer = null with get, set
    member val Inner : YamlLayer = null with get, set
    member val Source : YamlLayer = null with get, set
    member val Reference : YamlLayer = null with get, set
    member val Destination : YamlLayer = null with get, set
    member val Implant : YamlLayer = null with get, set
    member val Active : YamlLayer = null with get, set
    member val Well : YamlLayer = null with get, set
    /// Used by `enclosure-of-intersection`: the "with" layer the
    /// inner is intersected with before the enclosure check.
    /// e.g. nwell.5: inner=psdm, with=diff → check (psdm ∩ diff)
    /// against nwell.
    member val With : YamlLayer = null with get, set

    // Numeric thresholds.
    member val MinUm : Nullable<float> = Nullable() with get, set
    member val MinUm2 : Nullable<float> = Nullable() with get, set
    member val OneDirUm : Nullable<float> = Nullable() with get, set
    member val OtherDirUm : Nullable<float> = Nullable() with get, set

    // InnerCondition strings (kebab-case).
    member val Cond : string = null with get, set
    member val CondA : string = null with get, set
    member val CondB : string = null with get, set

[<AllowNullLiteral>]
type YamlRuleset() =
    member val Version = 1 with get, set
    member val Pdk = "sky130" with get, set
    member val Rules : List<YamlRule> = List() with get, set

// --- InnerCondition <-> string ----------------------------------------

let private condToString (c: InnerCondition) : string =
    match c with
    | Always -> "always"
    | OverlapsDiff -> "overlaps-diff"
    | OverlapsPoly -> "overlaps-poly"
    | PsdmOverlaps -> "psdm-overlaps"
    | NsdmOverlaps -> "nsdm-overlaps"
    | NsdmNotInNwell -> "nsdm-not-in-nwell"

let private condFromString (s: string) : Result<InnerCondition, string> =
    match s with
    | null | "" -> Ok Always
    | "always" -> Ok Always
    | "overlaps-diff" -> Ok OverlapsDiff
    | "overlaps-poly" -> Ok OverlapsPoly
    | "psdm-overlaps" -> Ok PsdmOverlaps
    | "nsdm-overlaps" -> Ok NsdmOverlaps
    | "nsdm-not-in-nwell" -> Ok NsdmNotInNwell
    | other -> Error (sprintf "unknown inner condition '%s'" other)

// --- Rule kind <-> string ----------------------------------------------

let private kindOf (r: Rule) : string =
    match r with
    | Width _ -> "width"
    | Spacing _ -> "spacing"
    | CrossSpacing _ -> "cross-spacing"
    | Enclosure _ -> "enclosure"
    | Endcap _ -> "endcap"
    | MinArea _ -> "min-area"
    | AsymEnclosure _ -> "asym-enclosure"
    | BoundaryCrossing _ -> "boundary-crossing"
    | ImplantOutsideWellSpacing _ -> "implant-outside-well-spacing"
    | EnclosureOfIntersection _ -> "enclosure-of-intersection"

// --- LayerKey conversion -----------------------------------------------

let private layerOf (lk: LayerKey) : YamlLayer =
    let y = YamlLayer()
    y.Number <- lk.Number
    y.Datatype <- lk.DataType
    // Name is informational only — handy for human-readers but the
    // loader keys on (number, datatype). Look up from Layout.Layer
    // when we have a sky130 entry, else leave blank.
    y.Name <-
        match Rekolektion.Viz.Core.Layout.Layer.bySky130Number lk.Number lk.DataType with
        | Some l -> l.Name
        | None -> ""
    y

let private layerKeyOf (y: YamlLayer) : Result<LayerKey, string> =
    if isNull y then Error "missing layer block"
    else Ok { Number = y.Number; DataType = y.Datatype }

// --- Rule → YamlRule ---------------------------------------------------

/// Serialize a Rule into the YAML POCO. The kind drives which
/// optional fields get populated; everything else stays null/zero.
let toYamlRule (r: Rule) : YamlRule =
    let y = YamlRule()
    y.Name <- nameOf r
    y.Kind <- kindOf r
    match r with
    | Width (_, layer, m) ->
        y.Layer <- layerOf layer
        y.MinUm <- Nullable m
    | Spacing (_, layer, m) ->
        y.Layer <- layerOf layer
        y.MinUm <- Nullable m
    | CrossSpacing (_, layerA, layerB, m, condA, condB) ->
        y.LayerA <- layerOf layerA
        y.LayerB <- layerOf layerB
        y.MinUm <- Nullable m
        y.CondA <- condToString condA
        y.CondB <- condToString condB
    | Enclosure (_, outer, inner, m, cond) ->
        y.Outer <- layerOf outer
        y.Inner <- layerOf inner
        y.MinUm <- Nullable m
        y.Cond <- condToString cond
    | Endcap (_, source, reference, m) ->
        y.Source <- layerOf source
        y.Reference <- layerOf reference
        y.MinUm <- Nullable m
    | MinArea (_, layer, m2) ->
        y.Layer <- layerOf layer
        y.MinUm2 <- Nullable m2
    | AsymEnclosure (_, outer, inner, oneDir, otherDir, cond) ->
        y.Outer <- layerOf outer
        y.Inner <- layerOf inner
        y.OneDirUm <- Nullable oneDir
        y.OtherDirUm <- Nullable otherDir
        y.Cond <- condToString cond
    | BoundaryCrossing (_, source, destination, m) ->
        y.Source <- layerOf source
        y.Destination <- layerOf destination
        y.MinUm <- Nullable m
    | ImplantOutsideWellSpacing (_, implant, active, well, m) ->
        y.Implant <- layerOf implant
        y.Active <- layerOf active
        y.Well <- layerOf well
        y.MinUm <- Nullable m
    | EnclosureOfIntersection (_, outer, inner, withL, m) ->
        y.Outer <- layerOf outer
        y.Inner <- layerOf inner
        y.With <- layerOf withL
        y.MinUm <- Nullable m
    y

// --- YamlRule → Rule ---------------------------------------------------

/// Try to construct a Rule from its YAML POCO. Returns Error with
/// a human-readable diagnostic when fields are missing for the
/// declared kind — better than throwing partway through a load.
let fromYamlRule (yr: YamlRule) : Result<Rule, string> =
    let getMin () =
        if yr.MinUm.HasValue then Ok yr.MinUm.Value
        else Error (sprintf "rule '%s': min_um is required for kind '%s'" yr.Name yr.Kind)
    let getMin2 () =
        if yr.MinUm2.HasValue then Ok yr.MinUm2.Value
        else Error (sprintf "rule '%s': min_um2 is required for kind '%s'" yr.Name yr.Kind)
    let getLayer field y =
        match layerKeyOf y with
        | Ok lk -> Ok lk
        | Error _ -> Error (sprintf "rule '%s': '%s' layer block is required" yr.Name field)
    let bind3 a b c f =
        match a with
        | Error e -> Error e
        | Ok x ->
            match b with
            | Error e -> Error e
            | Ok y ->
                match c with
                | Error e -> Error e
                | Ok z -> f x y z
    match yr.Kind with
    | "width" ->
        bind3 (getLayer "layer" yr.Layer) (getMin ()) (Ok ())
            (fun layer m _ -> Ok (Width (yr.Name, layer, m)))
    | "spacing" ->
        bind3 (getLayer "layer" yr.Layer) (getMin ()) (Ok ())
            (fun layer m _ -> Ok (Spacing (yr.Name, layer, m)))
    | "cross-spacing" ->
        bind3 (getLayer "layer_a" yr.LayerA) (getLayer "layer_b" yr.LayerB) (getMin ())
            (fun la lb m ->
                match condFromString yr.CondA, condFromString yr.CondB with
                | Ok ca, Ok cb -> Ok (CrossSpacing (yr.Name, la, lb, m, ca, cb))
                | Error e, _ | _, Error e -> Error (sprintf "rule '%s': %s" yr.Name e))
    | "enclosure" ->
        bind3 (getLayer "outer" yr.Outer) (getLayer "inner" yr.Inner) (getMin ())
            (fun o i m ->
                match condFromString yr.Cond with
                | Ok c -> Ok (Enclosure (yr.Name, o, i, m, c))
                | Error e -> Error (sprintf "rule '%s': %s" yr.Name e))
    | "endcap" ->
        bind3 (getLayer "source" yr.Source) (getLayer "reference" yr.Reference) (getMin ())
            (fun s r m -> Ok (Endcap (yr.Name, s, r, m)))
    | "min-area" ->
        bind3 (getLayer "layer" yr.Layer) (getMin2 ()) (Ok ())
            (fun layer m2 _ -> Ok (MinArea (yr.Name, layer, m2)))
    | "asym-enclosure" ->
        bind3 (getLayer "outer" yr.Outer) (getLayer "inner" yr.Inner) (Ok ())
            (fun o i _ ->
                let one =
                    if yr.OneDirUm.HasValue then Ok yr.OneDirUm.Value
                    else Error (sprintf "rule '%s': one_dir_um is required" yr.Name)
                let other =
                    if yr.OtherDirUm.HasValue then Ok yr.OtherDirUm.Value
                    else Error (sprintf "rule '%s': other_dir_um is required" yr.Name)
                match one, other, condFromString yr.Cond with
                | Ok a, Ok b, Ok c -> Ok (AsymEnclosure (yr.Name, o, i, a, b, c))
                | Error e, _, _ | _, Error e, _ -> Error e
                | _, _, Error e -> Error (sprintf "rule '%s': %s" yr.Name e))
    | "boundary-crossing" ->
        bind3 (getLayer "source" yr.Source) (getLayer "destination" yr.Destination) (getMin ())
            (fun s d m -> Ok (BoundaryCrossing (yr.Name, s, d, m)))
    | "implant-outside-well-spacing" ->
        bind3 (getLayer "implant" yr.Implant) (getLayer "active" yr.Active) (getMin ())
            (fun i a m ->
                match layerKeyOf yr.Well with
                | Ok w -> Ok (ImplantOutsideWellSpacing (yr.Name, i, a, w, m))
                | Error _ -> Error (sprintf "rule '%s': 'well' layer block is required" yr.Name))
    | "enclosure-of-intersection" ->
        bind3 (getLayer "outer" yr.Outer) (getLayer "inner" yr.Inner) (getMin ())
            (fun o i m ->
                match layerKeyOf yr.With with
                | Ok w -> Ok (EnclosureOfIntersection (yr.Name, o, i, w, m))
                | Error _ -> Error (sprintf "rule '%s': 'with' layer block is required" yr.Name))
    | other ->
        Error (sprintf "rule '%s': unknown kind '%s'" yr.Name other)

// --- Serialize / parse YAML strings ------------------------------------

let private serializer =
    SerializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .DisableAliases()
        // Skip null fields and unset Nullables — every Rule kind
        // uses only a subset of the YamlRule's optional layer/value
        // slots, and emitting them as `field:` (empty) blows the
        // file up with 25+ noise lines per rule.
        .ConfigureDefaultValuesHandling(
            DefaultValuesHandling.OmitNull
            ||| DefaultValuesHandling.OmitEmptyCollections
            ||| DefaultValuesHandling.OmitDefaults)
        .Build()

let private deserializer =
    DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build()

/// Serialize a list of Rules to a YAML string with `version`, `pdk`,
/// and `rules` keys. Stable ordering: preserves the input list order.
let serialize (pdk: string) (rules: Rule list) : string =
    let rs = YamlRuleset()
    rs.Pdk <- pdk
    rs.Version <- 1
    rs.Rules <- List(rules |> List.map toYamlRule)
    serializer.Serialize(rs)

/// Parsed-and-validated payload from one YAML source. `Errors`
/// collects per-rule diagnostics so the caller can present them
/// without aborting on the first bad rule.
type ParsedRuleset = {
    Pdk : string
    /// Rules that loaded cleanly, in source order.
    Rules : Rule list
    /// Rules the override file explicitly disabled (by-name). These
    /// suppress same-named base rules in the merger.
    DisabledNames : Set<string>
    /// Per-rule parse errors keyed by rule name (or a synthetic
    /// `<rule#N>` when the name itself was missing).
    Errors : Map<string, string>
}

/// Parse a YAML document into rules + diagnostics. Never throws on
/// rule-content errors — they're collected into `Errors`. Throws
/// only on YAML syntax errors (malformed document).
let parse (yaml: string) : ParsedRuleset =
    let rs =
        match deserializer.Deserialize<YamlRuleset>(yaml) with
        | null -> YamlRuleset()
        | r -> r
    let rules = ResizeArray<Rule>()
    let disabled = HashSet<string>()
    let errors = Dictionary<string, string>()
    let mutable i = 0
    for yr in rs.Rules do
        let key =
            if String.IsNullOrEmpty yr.Name then sprintf "<rule#%d>" i
            else yr.Name
        if yr.Disabled then
            disabled.Add key |> ignore
        else
            match fromYamlRule yr with
            | Ok r -> rules.Add r
            | Error e -> errors.[key] <- e
        i <- i + 1
    // NOTE: the `live-eligible` schema field is accepted by the
    // YAML reader but does NOT yet propagate through the merger.
    // YamlDotNet's `Nullable<bool>` deserialisation needs a custom
    // converter to distinguish "field absent" from "field=false";
    // until that's wired, per-rule live-eligibility falls back to
    // the kind-based default in `Rules.isLiveEligible`.
    {
        Pdk = if isNull rs.Pdk then "sky130" else rs.Pdk
        Rules = List.ofSeq rules
        DisabledNames = Set.ofSeq disabled
        Errors = Map.ofSeq (Seq.map (fun (kvp: KeyValuePair<_,_>) -> kvp.Key, kvp.Value) errors)
    }

// --- Merge ------------------------------------------------------------

/// Outcome of merging a base ruleset with an override ruleset.
/// `Provenance` records which file each effective rule came from.
type MergedRuleset = {
    Rules : Rule list
    Provenance : Map<string, string>
}

/// Strict by-name merge: override rules replace base rules of the
/// same name; override `disabled: true` removes the rule entirely;
/// rules only in one of the files pass through. Provenance is
/// attributed to the file the effective rule came from.
let merge
        (baseParsed: ParsedRuleset)
        (baseSource: string)
        (overrideParsed: ParsedRuleset)
        (overrideSource: string)
        : MergedRuleset =
    let overrideByName =
        overrideParsed.Rules
        |> List.map (fun r -> nameOf r, r)
        |> Map.ofList
    let effective = ResizeArray<Rule>()
    let provenance = Dictionary<string, string>()
    // Pass 1: base rules, unless the override disabled or replaced them.
    for r in baseParsed.Rules do
        let n = nameOf r
        if overrideParsed.DisabledNames.Contains n then ()
        elif Map.containsKey n overrideByName then ()      // emitted in pass 2
        else
            effective.Add r
            provenance.[n] <- baseSource
    // Pass 2: override rules (replacements + brand-new).
    for r in overrideParsed.Rules do
        let n = nameOf r
        // An override entry that says `disabled: true` doesn't yield
        // a rule object — handled in the parser already.
        effective.Add r
        provenance.[n] <- overrideSource
    {
        Rules = List.ofSeq effective
        Provenance = Map.ofSeq (Seq.map (fun (kvp: KeyValuePair<_,_>) -> kvp.Key, kvp.Value) provenance)
    }

// --- Disk loaders -------------------------------------------------------

/// Read a YAML file and parse it. Throws on missing file or YAML
/// syntax errors; per-rule content errors stay in `ParsedRuleset.Errors`.
let parseFile (path: string) : ParsedRuleset =
    System.IO.File.ReadAllText path
    |> parse

/// Read a YAML file when it exists; return `None` when it doesn't.
/// Use for the override slot — `None` means "no overrides, use the
/// base ruleset unmodified".
let tryParseFile (path: string) : ParsedRuleset option =
    if System.IO.File.Exists path then
        Some (parseFile path)
    else
        None

/// Convenience top-level loader: read the base file, optionally read
/// an override file (skipping cleanly when the override path is
/// `None` or the file doesn't exist), and merge them. Returns the
/// effective `Rule` list with per-rule provenance attribution.
///
/// `overridePath = None` reproduces the base ruleset 1:1 with
/// provenance pointing at the base file for every rule.
let loadEffective
        (basePath: string)
        (overridePath: string option)
        : MergedRuleset =
    let base' = parseFile basePath
    let over, overSource =
        match overridePath |> Option.bind tryParseFile, overridePath with
        | Some p, Some src -> p, src
        | _ ->
            { Pdk = base'.Pdk
              Rules = []
              DisabledNames = Set.empty
              Errors = Map.empty },
            "<no-override>"
    merge base' basePath over overSource

/// Convert a merged ruleset into a `Rules.RulesetView` for the DRC
/// engine. Wraps `Rules.viewOf` so consumers don't need to touch
/// the underlying record shape.
let toView (m: MergedRuleset) : RulesetView =
    viewOf m.Rules m.Provenance

/// App-boot entry: locate the base YAML for `pdk` (e.g. `"sky130"`)
/// via `Rules.tryLocateBaseYaml`, layer the optional override on
/// top, and return the resulting `RulesetView`. Falls back to
/// `Rules.defaultView` (the F#-coded rule table) when no base
/// YAML is on disk — keeps the app working in environments where
/// the bundled file got stripped.
let loadEffectiveOrDefault
        (pdk: string)
        (overridePath: string option)
        : RulesetView =
    match tryLocateBaseYaml pdk with
    | None -> defaultView
    | Some basePath ->
        try
            loadEffective basePath overridePath |> toView
        with _ ->
            // Disk-load failure (corrupt YAML, IO error) must not
            // break the editor — fall back to defaults.
            defaultView
