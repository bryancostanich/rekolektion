# viz DRC: thin-step detection for same-layer Spacing

## Problem

viz's live-DRC under-reports `nwell.2a` (and any other same-layer
`Spacing` rule) when the violation is a **thin step in the
merged region's outline**, not a gap between two distinct
regions.

Confirmed reproducer: `cim_reram_drv_phaseA_srcmux.rkt`.

Magic reports 2 `nwell.2a` violations; viz reports 0. The 22 nm
gap exists in the source layout at `build_cim_reram_drv_phaseA_srcmux.py:117-119`:

```python
extra_paint.append(rkt.Rect(layer="nwell",
    x1=NWELL_X1, y1=-10396, x2=INVP_CUT_X1, y2=-6161))  # Strip B-west
```

`y2=-6161` ends 22 nm below the `nand_S`'s pfet tub top at
`y=-6139` (after Magic snap: 20 nm gap). The source layout
needs the strip's `y2` raised to `-6139` (or above) to abut
the tub top cleanly. **That fix is on the user side.**

The viz-side issue: viz's `Spacing` rule (`Drc/Check.fs:342-424`)
flags only the **classical "two distinct regions with a gap
between them"** case. It can't see the step because:

1. Strip B-west and the tub bbox-overlap on both axes (strip
   X 22550–30880 contains tub X 23775–29085; strip Y -10396…-6161
   overlaps tub Y -10385…-6139 strictly).
2. 4-connectivity DSU correctly unions them into one component.
3. The current per-pair `bboxOrthoGapAndRegion` only fires for
   pairs in **different** components.
4. So the inward step in the merged outline is never examined.

Magic's tile-based DRC analyses every facing-edge pair on the
merged region's outline. viz needs an equivalent.

## Algorithm

For each connected component of same-layer rects, examine every
pair `(A, B)` and check whether they form an outline step in any
of four directions:

```
For each component (DSU root):
  for each pair (A, B), A.idx < B.idx, A and B in this component:
    # Top–top step (both edges face UP)
    if X-overlap(A, B) > 0:
      let dY = |A.yMax - B.yMax|
      if 0 < dY < limit
         and topOnOutline(A)
         and topOnOutline(B):
        emit violation in the step's gap region

    # Bottom–bottom step (both face DOWN) — same shape with yMin
    # Left–left step (both face LEFT) — symmetric on Y axis
    # Right–right step (both face RIGHT) — symmetric on Y axis
```

### `topOnOutline(rect_i)` — rect_i's top edge is on the merged
outline iff at some `X` in `[rect_i.xMin, rect_i.xMax]`, no other
rect in the same component covers above `rect_i.yMax` at that X.
Equivalently: rect_i is the topmost rect of the component at
some X column.

```fsharp
let topOnOutline (i: int) (comp: int) : bool =
    let (_, (ixMin, _, ixMax, iyMax), _) = polys.[i]
    // Build the union of X intervals covered "above" by other
    // members of the component (i.e., other rects whose Y range
    // covers iyMax AND whose yMax is strictly greater).
    let covers =
        [ for j in 0 .. n - 1 do
            if j <> i && find j = comp then
              let (_, (jxMin, jyMin, jxMax, jyMax), _) = polys.[j]
              if jyMin <= iyMax && jyMax > iyMax then
                yield (max jxMin ixMin, min jxMax ixMax) ]
        |> List.filter (fun (a, b) -> a < b)
    // Subtract the covers from [ixMin, ixMax].  Top is on
    // outline iff the remainder is non-empty.
    intervalSubtract (ixMin, ixMax) covers |> List.isEmpty |> not
```

`intervalSubtract`: take an interval and subtract a set of
sub-intervals, returning the residual sub-intervals. Standard
1D-interval-arithmetic.

Mirror functions for `bottomOnOutline`, `leftOnOutline`,
`rightOnOutline`. The direction-specific predicates differ only
in which axes / extremes participate.

### Violation bbox

For a top–top step between A (lower top) and B (higher top):
- X range = X-overlap of A and B
- Y range = `[min(A.yMax, B.yMax), max(A.yMax, B.yMax)]`

This is the inward strip the step traces. Clusters similarly to
existing `bboxOrthoGapAndRegion` violations so the renderer
displays it as one red box per step boundary.

### Complexity

`O(N²)` pairs per component plus `O(N)` per outline check =
`O(N³)` per component. Same shape as the existing connectivity
DSU. For typical viz macros (≤ 100 rects per layer per
component) negligible. Cache the topmost-X intervals per rect
to bring it down to `O(N²)` if needed.

## Edge cases & false-positive risk

| Geometry | Outcome | Why |
|---|---|---|
| Single rectangle | No pair → no fire | ✓ |
| Two **disjoint** rects (different components) | Existing logic catches gap | ✓ |
| Nested rect (small fully inside big) | `topOnOutline(small) = false` | ✓ |
| Strip + tub (the user's case) | Both `topOnOutline = true`, ΔY 22 nm < 1270 → fire | ✓ |
| Two abutting rects with equal yMax (single flat top) | ΔY = 0, gated by `0 < dY` | ✓ |
| Two rects forming an "L" (vertical bar on top of horizontal bar) | The vertical bar's top is on outline; the horizontal bar's top is on outline only where the vertical doesn't cover; ΔY = bar height → fire if < limit | Correct — a 10 nm "T-bar" head genuinely violates 1.27 µm spacing |

The only false-positive risk is if a rect's `topOnOutline` ends
up `true` due to a small fragment not covered, but the geometry
near that fragment is one solid neck. Mitigate by raising the
threshold for "step contributes to outline" — e.g., require the
exposed X span to be ≥ some small floor (1 nm = always true,
larger = ignore single-DBU corners). v1: no floor; revisit if
false positives in test corpus.

## Implementation

`tools/viz/src/Rekolektion.Viz.Core/Drc/Check.fs`:
- Inside the `Rules.Spacing` arm, **after** the existing per-pair
  gap-check loop, add a second loop that walks same-component
  pairs and runs the four-direction step check.
- Helper module-private functions `topOnOutline`, `bottomOnOutline`,
  `leftOnOutline`, `rightOnOutline` + an `intervalSubtract`
  utility.
- The step-violation `Violation` record uses the existing
  shape (`Rule`, `LayerNumber`, `LayerType`, `LimitDbu`,
  `MeasuredDbu`, `BboxA`, `BboxB = None`). No type changes.

CLI hook:
- The existing `rekolektion-viz drc` subcommand picks up
  whatever `Drc.Check.check` returns, so the new violations
  surface automatically.

## Test plan

1. **Unit tests** in `tools/viz/tests/Rekolektion.Viz.Core.Tests/DrcCheckTests.fs`:
   - Two disjoint rects with sub-limit gap → 1 violation (existing).
   - Two overlapping rects forming flat top → 0 violations.
   - Strip + tub fixture (the reproducer geometry, simplified) →
     2 violations (left step + right step).
   - Nested rect inside larger rect → 0 violations.
   - "L" with thin top bar → 1 violation per outline step.
   - 4-direction sanity: rotate each of the above 90° and verify
     left/right step detection mirrors top/bottom.

2. **Integration**: run `rekolektion-viz drc cim_reram_drv_phaseA_srcmux.rkt`.
   Expected new output: 2 `nwell.2a` violations matching Magic's
   coordinates (after the user fixes line 117–119 in the build
   script, both go away).

3. **Regression**: run the existing live-DRC tests + DRC
   integration tests. None should change because the existing
   classical-spacing path is untouched.

## Out of scope

- Diagonal edges / non-rectilinear geometry. viz's flat polys
  are rectilinear so this never comes up.
- 3D / Z-direction step detection (irrelevant for 2D DRC).
- Cross-layer step detection (`CrossSpacing`). Same algorithm
  would apply but kept separate for v1 to limit blast radius.

## References

- `tools/viz/src/Rekolektion.Viz.Core/Drc/Check.fs:342-424`
  (existing `Spacing` rule eval).
- Magic violation reproducer:
  `scripts/build_cim_reram_drv_phaseA_srcmux.py:117-119`.
- Side-by-side viz vs Magic on this cell:
  conversation log "DRC comparison" thread.
