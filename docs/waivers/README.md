# Tapeout sign-off waivers

This directory holds disclosure documents for known-but-accepted issues that must be signed off before any tapeout consuming these macros.

The trust audit (`audit/`) classifies each item P0/P1/P2. This directory holds the written waiver for every P1 that is YELLOW on the sign-off gate (`audit/signoff_gate.md`).

## Index

| Waiver | Item | Severity | Status |
|--------|------|----------|--------|
| [`nwell_bias_disclosure.md`](nwell_bias_disclosure.md) | T4.4-A — bitcell N-well biased via subsurface conduction (no metal path) | P1 | Awaiting sign-off |
| [`liberty_timing_analytical.md`](liberty_timing_analytical.md) | T1.7-A + T4.1-DIVERGENT-A — Liberty timing arcs are analytical, not SPICE-measured | P1 | Awaiting sign-off (or supersede with task #24) |

## Process

For tapeout sign-off:

1. Each waiver document must be signed by the designer of record + the chip-integration owner before the GDS is taped out.
2. Signed waivers should be committed to this directory (with the actual signatures, dates, and any notes the signer wants on the record).
3. If a P1 transitions to RESOLVED before tapeout (e.g., task #24 completes), strike through the waiver and reference the resolution commit. Do NOT delete signed waivers — they're audit trail.

## What is NOT in this directory

- **P0 silicon defects:** these were resolved in code, not waived. See `audit/smoking_guns.md` for resolution history.
- **P2 cleanups:** dead caches, docstring drift, etc. Tracked as ordinary task list items; no waiver required.
- **#64 LVS pin-resolver artifact:** netgen positional-alignment quirk; manual port verification substitutes. Not silicon-impacting; not waiver-class. Tracked as task #64 for future investigation.
