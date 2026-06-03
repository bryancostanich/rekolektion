module Rekolektion.Viz.Core.Tests.DrcStaticCacheTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout.Flatten
open Rekolektion.Viz.Core.Drc

// Track 02 follow-up #1 — the canvas's static-layout DRC cache
// used to invalidate only on (flat identity ∨ disabled-rule set
// change), missing compat / view changes from the Mode-menu
// DRC-compat toggle.  Flipping Magic ↔ KLayout in a running
// session returned stale violations from the first DRC pass.
//
// Fix lives in `Drc.StaticCache`; these tests pin the
// invariant: every input that changes the engine's output MUST
// invalidate the cache.

let private rect (x1, y1, x2, y2) (layer, dt) : FlatPolygon =
    { Layer = layer
      DataType = dt
      Points = [|
        { X = int64 x1; Y = int64 y1 }
        { X = int64 x2; Y = int64 y1 }
        { X = int64 x2; Y = int64 y2 }
        { X = int64 x1; Y = int64 y2 }
        { X = int64 x1; Y = int64 y1 }
      |]
      SourceStructure = "test"
      SourceIndex = 0
      TopInstanceIndex = None }

let private units1nm : Units = { DbuNm = 1; UuUm = 1 }

/// Two met1 polys 139 nm apart — violates 0.14 µm `m1.2`/`met1.2`
/// under both compats, so every cache call returns a non-empty
/// array.  Same coords, same shape; the violations differ only
/// by which rule list the engine iterates.
let private violatingFlat () : FlatPolygon array = [|
    rect (0L, 0L, 200L, 200L) (68, 20)
    rect (339L, 0L, 539L, 200L) (68, 20)
|]

// ─────────────────────────────────────────────────────────────────
// Cache HIT semantics — identical inputs reuse the same array
// instance.  Reference equality is the contract: callers can
// short-circuit downstream work by checking `obj.ReferenceEquals`.
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``identical inputs return the same array reference (cache hit)`` () =
    let cache = StaticCache.empty ()
    let flat = violatingFlat ()
    let v1 =
        StaticCache.get cache Compat.Klayout
            Rules.Klayout.defaultView units1nm flat Set.empty
    let v2 =
        StaticCache.get cache Compat.Klayout
            Rules.Klayout.defaultView units1nm flat Set.empty
    obj.ReferenceEquals(v1, v2) |> should equal true

// ─────────────────────────────────────────────────────────────────
// Cache MISS semantics — every input that the engine reads must
// trigger a fresh compute.  Each test pins ONE of the four
// validity-tuple inputs as the only thing that changes.
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``new flat array identity invalidates the cache`` () =
    let cache = StaticCache.empty ()
    let flat1 = violatingFlat ()
    let flat2 = violatingFlat ()           // same content, different identity
    let v1 =
        StaticCache.get cache Compat.Klayout
            Rules.Klayout.defaultView units1nm flat1 Set.empty
    let v2 =
        StaticCache.get cache Compat.Klayout
            Rules.Klayout.defaultView units1nm flat2 Set.empty
    obj.ReferenceEquals(v1, v2) |> should equal false

[<Fact>]
let ``different disabled-rule set invalidates the cache`` () =
    let cache = StaticCache.empty ()
    let flat = violatingFlat ()
    let v1 =
        StaticCache.get cache Compat.Klayout
            Rules.Klayout.defaultView units1nm flat Set.empty
    let v2 =
        StaticCache.get cache Compat.Klayout
            Rules.Klayout.defaultView units1nm flat (Set.singleton "m1.2")
    obj.ReferenceEquals(v1, v2) |> should equal false

[<Fact>]
let ``compat change invalidates the cache (the Mode-menu toggle case)`` () =
    // The headline bug.  Same flat, same disabled set, same view
    // instance — only `compat` flips.  Pre-fix, the cache returned
    // the original array; post-fix, the second `get` recomputes.
    let cache = StaticCache.empty ()
    let flat = violatingFlat ()
    let v1 =
        StaticCache.get cache Compat.Klayout
            Rules.Klayout.defaultView units1nm flat Set.empty
    let v2 =
        StaticCache.get cache Compat.Magic
            Rules.Klayout.defaultView units1nm flat Set.empty
    obj.ReferenceEquals(v1, v2) |> should equal false

[<Fact>]
let ``view identity change invalidates the cache`` () =
    // Matches the production data flow: `SetDrcCompat` reloads
    // the view via `RulesYaml.loadEffectiveOrDefault`, which
    // always returns a fresh `RulesetView` instance — so identity
    // mismatch (cheap) fires the invalidation without an Equals
    // walk over the rule list.
    let cache = StaticCache.empty ()
    let flat = violatingFlat ()
    let v1 =
        StaticCache.get cache Compat.Klayout
            Rules.Klayout.defaultView units1nm flat Set.empty
    let v2 =
        StaticCache.get cache Compat.Klayout
            Rules.Magic.defaultView units1nm flat Set.empty
    obj.ReferenceEquals(v1, v2) |> should equal false

// ─────────────────────────────────────────────────────────────────
// Output correctness — the cached array must equal what
// `Check.checkWithToggles` would have returned for the same
// inputs.  Without this, a future cache-key shortcut that returns
// a stale array could pass the cache-MISS tests above while
// silently shipping wrong violations.
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``cached violations match a fresh checkWithToggles call`` () =
    let cache = StaticCache.empty ()
    let flat = violatingFlat ()
    let cached =
        StaticCache.get cache Compat.Klayout
            Rules.Klayout.defaultView units1nm flat Set.empty
    let fresh =
        Check.checkWithToggles
            Compat.Klayout true Rules.Klayout.defaultView
            units1nm flat Set.empty
    cached
    |> Array.map (fun v -> v.Rule, v.MeasuredDbu)
    |> should equal (fresh |> Array.map (fun v -> v.Rule, v.MeasuredDbu))

[<Fact>]
let ``compat-aware output: Klayout view sees m1.2, Magic view sees met1.2`` () =
    // The rule names differ between compats (Klayout deck uses
    // `m1.2`; F# Magic uses `met1.2`).  This pins that the cache
    // doesn't accidentally serve one compat's names under the
    // other's compat key — i.e. the recompute on compat change
    // actually does flow through to the rule list, not just
    // reset the cache slot.
    let cache = StaticCache.empty ()
    let flat = violatingFlat ()
    let kVs =
        StaticCache.get cache Compat.Klayout
            Rules.Klayout.defaultView units1nm flat Set.empty
    kVs
    |> Array.exists (fun v -> v.Rule = "m1.2")
    |> should equal true
    let mVs =
        StaticCache.get cache Compat.Magic
            Rules.Magic.defaultView units1nm flat Set.empty
    mVs
    |> Array.exists (fun v -> v.Rule = "met1.2")
    |> should equal true
