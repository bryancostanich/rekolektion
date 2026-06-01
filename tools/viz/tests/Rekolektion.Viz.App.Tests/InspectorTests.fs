module Rekolektion.Viz.App.Tests.InspectorTests

// Unit tests for the Inspector's DRC-violation clipboard formatter.
// The clipboard call itself is an Avalonia side-effect we don't
// exercise; the formatter that produces the string is a pure
// function over (Model, Violation), perfect for tests.

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core
open Rekolektion.Viz.App.Model
open Rekolektion.Viz.App.View

let private violation (rule: string) : Drc.Check.Violation = {
    Rule = rule
    LayerNumber = 68
    LayerType = 20
    LimitDbu = 140L
    MeasuredDbu = 100L
    BboxA = (0L, 0L, 200L, 200L)
    BboxB = None
}

let private modelWithRule (r: Drc.Rules.Rule) : Model.Model =
    let view : Drc.Rules.RulesetView = {
        Rules = [ r ]
        Provenance = Map.empty
    }
    { Model.empty with DrcView = view }

[<Fact>]
let ``formatViolationForClipboard always starts with "DRC Violation" header`` () =
    let v = violation "met1.2"
    let s = Inspector.formatViolationForClipboard Model.empty v
    s.StartsWith "DRC Violation\n" |> should equal true

[<Fact>]
let ``formatViolationForClipboard surfaces rule + kind from DrcView`` () =
    let met1 : Drc.Rules.LayerKey = { Number = 68; DataType = 20 }
    let rule = Drc.Rules.Spacing ("met1.2", met1, 0.140)
    let model = modelWithRule rule
    let v = violation "met1.2"
    let s = Inspector.formatViolationForClipboard model v
    s |> should haveSubstring "rule: met1.2 (Spacing)"

[<Fact>]
let ``formatViolationForClipboard falls back to (rule) when rule not in view`` () =
    let v = violation "unknown.42"
    let s = Inspector.formatViolationForClipboard Model.empty v
    s |> should haveSubstring "rule: unknown.42 ((rule))"

[<Fact>]
let ``formatViolationForClipboard converts DBU to µm at the model's DbuNm scale`` () =
    // Model.empty has no active macro → formatter uses 1 nm/DBU
    // fallback, so a 140 DBU limit shows as 0.140 µm.
    let v = violation "met1.2"
    let s = Inspector.formatViolationForClipboard Model.empty v
    s |> should haveSubstring "limit: 0.140 µm"
    s |> should haveSubstring "measured: 0.100 < 0.140 µm"

[<Fact>]
let ``formatViolationForClipboard includes layer name + GDS pair`` () =
    let v = violation "met1.2"
    let s = Inspector.formatViolationForClipboard Model.empty v
    s |> should haveSubstring "layer: met1 (68/20)"

[<Fact>]
let ``formatViolationForClipboard includes source provenance when available`` () =
    let met1 : Drc.Rules.LayerKey = { Number = 68; DataType = 20 }
    let view : Drc.Rules.RulesetView = {
        Rules = [ Drc.Rules.Spacing ("met1.2", met1, 0.140) ]
        Provenance = Map.ofList [ "met1.2", "/abs/path/overrides/v1.yaml" ]
    }
    let model = { Model.empty with DrcView = view }
    let s = Inspector.formatViolationForClipboard model (violation "met1.2")
    s |> should haveSubstring "source: v1.yaml"

[<Fact>]
let ``formatViolationForClipboard omits source line when provenance has no entry`` () =
    let s = Inspector.formatViolationForClipboard Model.empty (violation "met1.2")
    s.Contains "source:" |> should equal false

[<Fact>]
let ``formatViolationForClipboard always emits bbox A and only emits bbox B when present`` () =
    let v1 = violation "met1.2"
    let s1 = Inspector.formatViolationForClipboard Model.empty v1
    s1 |> should haveSubstring "bbox A:"
    s1.Contains "bbox B:" |> should equal false
    let v2 =
        { v1 with BboxB = Some (300L, 0L, 500L, 200L) }
    let s2 = Inspector.formatViolationForClipboard Model.empty v2
    s2 |> should haveSubstring "bbox A:"
    s2 |> should haveSubstring "bbox B:"
