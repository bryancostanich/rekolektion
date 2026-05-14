"""sky130 primitive generators.

Each function in this subpackage mints (or fetches cached) a single
PDK-generated primitive cell, returning the cell name. The
generated `.rkt` lives at `cell_designs/primitives/<name>.rkt` with
a `(meta ...)` block carrying provenance — generator, params,
digest, source.

Generators delegate layout to the PDK's own Magic TCL procs
(`sky130::sky130_fd_pr__nfet_g5v0d10v5_draw`, etc.); we never
reimplement device geometry. The transit format is GDS-via-Magic-CIF,
which we then translate to canonical `.rkt`.
"""

from rekolektion.primitives.sky130.fet import gen_nfet_hv, gen_pfet_hv

__all__ = ["gen_nfet_hv", "gen_pfet_hv"]
