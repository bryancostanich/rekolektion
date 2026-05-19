# viz DRC vs Magic — fidelity status

The in-process DRC engine in `Drc.Check.checkWithToggles` covers
the rule set ported from `src/rekolektion/tech/sky130.py` plus
implant-aware extensions (diff/tap.9). It uses a slab-decomposed
`Region` geometry library for booleans + sizing, Magic-style
COREID + foundry-cell waivers, and post-pass per-rule clustering.

## Goal vs reality

**Goal:** Match Magic's per-rule violation reports as closely as
possible without building a full polygon engine or netlist
extractor.

**Reality:** Real bug categories match (every `met1.6`,
`diff/tap.9`, `nwell.2a` violation Magic finds, viz also finds).
Counts differ for known structural reasons documented below.

## Validation on `cim_reram_drv_phaseA_srcmux`

| Rule | Magic | viz | Match? |
|---|---|---|---|
| `met1.6` | 11 tiles → 2 clusters | 2 | ✓ matches cluster count |
| `diff/tap.9` | 8 tiles → 1 cluster | 1 | ✓ matches cluster count |
| `nwell.2a` | 3 | 16 | over-counts (see "gap bbox positioning") |
| `nwell.5` | 0 | 0 | ✓ after `OverlapsDiff` filter landed |

The remaining `nwell.2a` over-count comes from per-pair gap-bbox
positioning: pair violations at slightly different X-overlaps
don't always cluster cleanly. Closing the gap would require
either per-tile decomposition (matches Magic exactly but
fragments other rules) or a smarter cluster-overlap test.

## Validation across other cells

```
bias_gen.rkt          Magic: 0    viz: diff/tap.9 × 2
nand2_inv_lv.rkt      Magic: 0    viz: nwell.2a × 2
lshift_1v8_to_3v3.rkt Magic: 0    viz: nwell.2a × 2, nsdm.2 × 2, psdm.2 × 2
```

All viz extras on these "clean" cells trace to known limitations
listed below.

## Known limitations / architectural gaps

### Same-net well + implant relaxation

SKY130's `nwell.2a` is 1.27 µm between **different-net** nwells
but only 0.60 µm between **same-net** nwells (sky130.py
`NWELL_TO_NWELL_SAME`). Magic uses netlist extraction to know
which nwells are on the same net (typically all VDD) and applies
the relaxed limit. viz has no net awareness and applies the
stricter 1.27 µm uniformly — over-reports nwell.2a in cells with
multiple same-net nwells.

Closing this requires either:
- Netlist extraction (large feature).
- Per-cell annotation of "all nwells in this cell are same-net"
  (lightweight but per-cell manual work).

### Magic's per-tile reporting

Magic emits one violation per failing **tile** in its corner-
stitched tile plane. viz emits one violation per spatial cluster
(connected component of failing tiles). For met1.6 with 2
spatial clusters, Magic emits 11 tiles; viz emits 2. Per-cluster
is better UX (one marker per logical violation), but counts
differ from Magic by a factor of ~tile-count-per-cluster.

Matching exactly would require porting Magic's tile algorithm
(corner-stitched maximal strips). Different from viz's slab
decomposition.

### Implant-aware rule scoping

Some Magic rules have nuanced scope — e.g., `nsdm.2` only
applies to NSDM markers that satisfy specific implant context.
viz uses a generic `InnerCondition` (Always / OverlapsDiff /
PsdmOverlaps / NsdmOverlaps / NsdmNotInNwell) which covers
common cases but misses subtler Magic-deck conditions.

### Foundry-primitive cell waiver

viz's foundry waiver uses **name-based heuristics**
(`pfet_`, `nfet_`, `sky130_fd_*` prefixes) plus a per-rule
waiver list (the contact-packing / implant-spacing rules
Magic implicitly waives inside foundry primitives). User cells
that happen to follow the prefix convention would be wrongly
treated as foundry; the inverse risk is also real for
foundry cells with non-conforming names.

A more principled fix: explicit `(foundry yes)` annotation
in `.rkt` cell definitions, or a path-based check
(`imported from .../primitives/`).

## Running the diff

```bash
cd tools/viz
dotnet build src/Rekolektion.Viz.Core/Rekolektion.Viz.Core.fsproj
scripts/drc_golden_diff.sh path/to/file.rkt
```

Output is two sorted rule-count lists (viz, Magic). Eyeball-diff
to see which rules over/under-fire on a given file.
