"""PDK primitive generators.

Each subpackage (e.g. `sky130`) exposes functions like
`gen_nfet_hv(W, L, guard)` that return a cell name and ensure the
matching `.rkt` exists on disk under
`cell_designs/primitives/<name>.rkt`.

The generators are deterministic: same `(generator, params)` always
yields the same cell name and (modulo PDK/Magic version) the same
geometry. The cache short-circuits a re-mint when the cell already
exists. The downstream block layer SRefs the resulting cell by name.

We don't reimplement the PDK device generators in Python. Instead,
we shell out to Magic with a small TCL stub that calls the PDK's
own `sky130::mos_draw` (and friends), then read back the GDS Magic
produces and persist it as `.rkt` with a `(meta ...)` provenance
block. Keeping the PDK as the source of truth for primitive layout
avoids forking foundry code and means new PDK releases automatically
flow through.
"""
