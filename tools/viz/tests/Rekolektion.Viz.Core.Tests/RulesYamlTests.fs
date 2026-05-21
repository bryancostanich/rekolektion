module Rekolektion.Viz.Core.Tests.RulesYamlTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Drc
open Rekolektion.Viz.Core.Drc.Rules

let private layerOf n dt : LayerKey = { Number = n; DataType = dt }
let private met1 = layerOf 68 20
let private met2 = layerOf 69 20
let private poly = layerOf 66 20
let private diff = layerOf 65 20
let private nwell = layerOf 64 20
let private nsdm = layerOf 93 44
let private li1 = layerOf 67 20
let private licon1 = layerOf 66 44

let private roundtrip (r: Rule) : Rule =
    let yaml = RulesYaml.serialize "sky130" [r]
    let parsed = RulesYaml.parse yaml
    parsed.Errors |> should equal (Map.empty : Map<string, string>)
    parsed.Rules |> List.length |> should equal 1
    parsed.Rules.[0]

// --- Per-kind round-trip ------------------------------------------------

[<Fact>]
let ``Width round-trips`` () =
    let r = Width ("met1.1", met1, 0.14)
    roundtrip r |> should equal r

[<Fact>]
let ``Spacing round-trips`` () =
    let r = Spacing ("met1.2", met1, 0.14)
    roundtrip r |> should equal r

[<Fact>]
let ``CrossSpacing with Always conditions round-trips`` () =
    let r = CrossSpacing ("poly.4", poly, diff, 0.075, Always, Always)
    roundtrip r |> should equal r

[<Fact>]
let ``CrossSpacing with NsdmNotInNwell condition round-trips`` () =
    let r = CrossSpacing ("difftap.9", diff, nwell, 0.34, NsdmNotInNwell, Always)
    roundtrip r |> should equal r

[<Fact>]
let ``Enclosure with OverlapsDiff condition round-trips`` () =
    let r = Enclosure ("licon.5a", li1, licon1, 0.04, OverlapsDiff)
    roundtrip r |> should equal r

[<Fact>]
let ``Endcap round-trips`` () =
    let r = Endcap ("poly.8", poly, diff, 0.13)
    roundtrip r |> should equal r

[<Fact>]
let ``MinArea round-trips (min_um2 distinct from min_um)`` () =
    let r = MinArea ("met1.6", met1, 0.083)
    roundtrip r |> should equal r

[<Fact>]
let ``AsymEnclosure round-trips (one_dir + other_dir + cond)`` () =
    let r = AsymEnclosure ("licon.5c", li1, licon1, 0.08, 0.04, OverlapsDiff)
    roundtrip r |> should equal r

[<Fact>]
let ``BoundaryCrossing round-trips`` () =
    let r = BoundaryCrossing ("nsdm.5", nsdm, nwell, 0.13)
    roundtrip r |> should equal r

[<Fact>]
let ``ImplantOutsideWellSpacing round-trips (three layers)`` () =
    let r = ImplantOutsideWellSpacing ("difftap.9", nsdm, diff, nwell, 0.34)
    roundtrip r |> should equal r

[<Fact>]
let ``Whole Rules.allRules survives serialize → parse without loss`` () =
    let yaml = RulesYaml.serialize "sky130" Rules.allRules
    let parsed = RulesYaml.parse yaml
    parsed.Errors |> should equal (Map.empty : Map<string, string>)
    parsed.Rules |> List.length |> should equal Rules.allRules.Length
    // Spot-check a few specific entries survived intact.
    parsed.Rules
    |> List.exists (fun r -> r = Spacing ("met1.2", met1, 0.14))
    |> should equal true
    parsed.Rules
    |> List.exists (fun r -> r = MinArea ("met1.6", met1, 0.083))
    |> should equal true

// --- Parse error reporting (no-throw) -----------------------------------

[<Fact>]
let ``Parse: missing min_um on a width rule lands in Errors`` () =
    let yaml = """
version: 1
pdk: sky130
rules:
  - name: bad.rule
    kind: width
    layer: { number: 68, datatype: 20, name: met1 }
"""
    let parsed = RulesYaml.parse yaml
    parsed.Rules |> should be Empty
    parsed.Errors.ContainsKey "bad.rule" |> should equal true

[<Fact>]
let ``Parse: unknown kind lands in Errors with the rule name`` () =
    let yaml = """
version: 1
pdk: sky130
rules:
  - name: weird
    kind: surprise
    layer: { number: 68, datatype: 20, name: met1 }
    min_um: 0.1
"""
    let parsed = RulesYaml.parse yaml
    parsed.Errors.ContainsKey "weird" |> should equal true

[<Fact>]
let ``Parse: unknown inner condition lands in Errors`` () =
    let yaml = """
version: 1
pdk: sky130
rules:
  - name: bad.cond
    kind: enclosure
    outer: { number: 67, datatype: 20, name: li1 }
    inner: { number: 66, datatype: 44, name: licon1 }
    min_um: 0.04
    cond: bogus
"""
    let parsed = RulesYaml.parse yaml
    parsed.Errors.ContainsKey "bad.cond" |> should equal true

// --- Merge semantics ----------------------------------------------------

let private parsedFrom (rules: Rule list) : RulesYaml.ParsedRuleset =
    let yaml = RulesYaml.serialize "sky130" rules
    RulesYaml.parse yaml

[<Fact>]
let ``Merge: override replaces base rule of the same name`` () =
    let base' = parsedFrom [
        Spacing ("met1.2", met1, 0.14)
        Spacing ("met2.2", met2, 0.14)
    ]
    let over = parsedFrom [
        Spacing ("met2.2", met2, 0.18)   // tightened
    ]
    let merged = RulesYaml.merge base' "base/sky130.yaml" over "overrides/v1.yaml"
    let met2Rule =
        merged.Rules
        |> List.find (fun r -> nameOf r = "met2.2")
    met2Rule |> should equal (Spacing ("met2.2", met2, 0.18))
    merged.Provenance.["met2.2"] |> should equal "overrides/v1.yaml"
    merged.Provenance.["met1.2"] |> should equal "base/sky130.yaml"

[<Fact>]
let ``Merge: override-only rule is added to the effective set`` () =
    let base' = parsedFrom [ Spacing ("met1.2", met1, 0.14) ]
    let over = parsedFrom [ Width ("custom.1", met2, 0.20) ]
    let merged = RulesYaml.merge base' "base/sky130.yaml" over "overrides/v1.yaml"
    merged.Rules |> List.length |> should equal 2
    merged.Provenance.["custom.1"] |> should equal "overrides/v1.yaml"
    merged.Provenance.["met1.2"] |> should equal "base/sky130.yaml"

[<Fact>]
let ``Merge: disabled=true override removes the rule from the effective set`` () =
    let base' = parsedFrom [
        Spacing ("met1.2", met1, 0.14)
        MinArea ("met1.6", met1, 0.083)
    ]
    let overYaml = """
version: 1
pdk: sky130
rules:
  - name: met1.6
    kind: min-area
    disabled: true
"""
    let over = RulesYaml.parse overYaml
    let merged = RulesYaml.merge base' "base/sky130.yaml" over "overrides/v1.yaml"
    merged.Rules |> List.length |> should equal 1
    merged.Rules
    |> List.exists (fun r -> nameOf r = "met1.6")
    |> should equal false

[<Fact>]
let ``Merge: rule unique to base stays attributed to base file`` () =
    let base' = parsedFrom [
        Spacing ("met1.2", met1, 0.14)
        Spacing ("met2.2", met2, 0.14)
    ]
    let over = parsedFrom [
        Spacing ("met1.2", met1, 0.18)   // only touches met1
    ]
    let merged = RulesYaml.merge base' "base/sky130.yaml" over "overrides/v1.yaml"
    merged.Provenance.["met2.2"] |> should equal "base/sky130.yaml"

// NOTE: the YAML schema reserves a `live-eligible` field for future
// per-rule override of `Rules.isLiveEligible`. The reader currently
// recognises and ignores it; propagation through the merger is not
// yet wired (see comment in RulesYaml.parse). When that ships, add
// the override test here.

// --- Disk loaders ----------------------------------------------------

open System.IO

let private writeTempYaml (rules: Rule list) : string =
    let path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".yaml")
    File.WriteAllText(path, RulesYaml.serialize "sky130" rules)
    path

[<Fact>]
let ``parseFile reads a YAML file from disk`` () =
    let path = writeTempYaml [ Spacing ("met1.2", met1, 0.14) ]
    try
        let parsed = RulesYaml.parseFile path
        parsed.Rules |> List.length |> should equal 1
        parsed.Rules.[0] |> should equal (Spacing ("met1.2", met1, 0.14))
    finally
        File.Delete path

[<Fact>]
let ``tryParseFile returns None for a missing path`` () =
    let path = Path.Combine(Path.GetTempPath(), "definitely-not-there.yaml")
    if File.Exists path then File.Delete path
    RulesYaml.tryParseFile path |> should equal (None : RulesYaml.ParsedRuleset option)

[<Fact>]
let ``tryParseFile returns Some for an existing path`` () =
    let path = writeTempYaml [ Width ("met1.1", met1, 0.14) ]
    try
        match RulesYaml.tryParseFile path with
        | Some p -> p.Rules |> List.length |> should equal 1
        | None -> failwith "expected Some for an existing file"
    finally
        File.Delete path

[<Fact>]
let ``loadEffective with no override returns base rules unchanged`` () =
    let basePath =
        writeTempYaml [
            Spacing ("met1.2", met1, 0.14)
            Spacing ("met2.2", met2, 0.14)
        ]
    try
        let merged = RulesYaml.loadEffective basePath None
        merged.Rules |> List.length |> should equal 2
        merged.Provenance.["met1.2"] |> should equal basePath
        merged.Provenance.["met2.2"] |> should equal basePath
    finally
        File.Delete basePath

[<Fact>]
let ``loadEffective merges base + override from disk`` () =
    let basePath = writeTempYaml [ Spacing ("met1.2", met1, 0.14) ]
    let overPath = writeTempYaml [ Spacing ("met1.2", met1, 0.18) ]
    try
        let merged = RulesYaml.loadEffective basePath (Some overPath)
        merged.Rules
        |> List.find (fun r -> nameOf r = "met1.2")
        |> should equal (Spacing ("met1.2", met1, 0.18))
        merged.Provenance.["met1.2"] |> should equal overPath
    finally
        File.Delete basePath
        File.Delete overPath

[<Fact>]
let ``loadEffective with override path pointing at missing file falls back to base`` () =
    let basePath = writeTempYaml [ Spacing ("met1.2", met1, 0.14) ]
    let missingOver = Path.Combine(Path.GetTempPath(), "not-here.yaml")
    if File.Exists missingOver then File.Delete missingOver
    try
        let merged = RulesYaml.loadEffective basePath (Some missingOver)
        merged.Rules |> List.length |> should equal 1
        merged.Provenance.["met1.2"] |> should equal basePath
    finally
        File.Delete basePath

// --- Bundled sky130.yaml ---------------------------------------------

[<Fact>]
let ``Bundled drc/base/sky130.yaml stays in sync with Rules.allRules`` () =
    // The bundled YAML is a serialised snapshot of Rules.allRules.
    // When the F# table changes, regenerate the bundle with:
    //   dotnet fsi tools/viz/scripts/dump_drc_yaml.fsx
    // (or any equivalent driver that calls
    //  RulesYaml.serialize "sky130" Rules.allRules).
    // This test catches drift before it ships.
    let bundledPath =
        Path.Combine(
            Path.GetDirectoryName(typeof<RulesYaml.ParsedRuleset>.Assembly.Location),
            "drc", "base", "sky130.yaml")
    File.Exists bundledPath |> should equal true
    let expected = RulesYaml.serialize "sky130" Rules.allRules
    let actual = File.ReadAllText bundledPath
    if expected <> actual then
        // Print the first divergence point so the dev can spot it.
        let pair =
            Seq.zip (expected.Split('\n')) (actual.Split('\n'))
            |> Seq.indexed
            |> Seq.tryFind (fun (_, (e, a)) -> e <> a)
        let hint =
            match pair with
            | Some (i, (e, a)) ->
                sprintf "first divergence at line %d:\n  expected: %s\n  actual:   %s" i e a
            | None -> "files differ in length only"
        failwithf "Bundled sky130.yaml is out of date. %s\nRegenerate with `dotnet fsi tools/viz/scripts/dump_drc_yaml.fsx`." hint

[<Fact>]
let ``Bundled drc/base/sky130.yaml round-trips back to Rules.allRules content`` () =
    // `Rules.allRules` legitimately re-uses some Magic rule names
    // across kinds (e.g. `poly.9` appears as two CrossSpacings AND
    // a Spacing). The parser preserves list order, so the right
    // comparison is element-wise — not by-name (which would collapse
    // duplicates).
    let bundledPath =
        Path.Combine(
            Path.GetDirectoryName(typeof<RulesYaml.ParsedRuleset>.Assembly.Location),
            "drc", "base", "sky130.yaml")
    let parsed = RulesYaml.parseFile bundledPath
    parsed.Errors |> should equal (Map.empty : Map<string, string>)
    parsed.Rules |> List.length |> should equal Rules.allRules.Length
    List.zip parsed.Rules Rules.allRules
    |> List.iter (fun (b, a) -> b |> should equal a)
