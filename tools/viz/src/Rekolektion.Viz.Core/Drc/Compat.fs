module Rekolektion.Viz.Core.Drc.Compat

/// Which authority's DRC rules to evaluate against.
///
/// `Klayout` is the default and primary target. `Magic` is the
/// permanent supported alternate — preserved so the long-invested
/// Magic-tuned ruleset stays accessible for parity debugging,
/// regression bisection, and any case where Magic's interpretation
/// matters.
///
/// This is the SAME word as Python `verify_drc(compat="klayout"|"magic")`.
/// It reads "which authority's rules am I checking equivalence with?"
/// — NOT "which engine am I invoking." External-tool invocation is a
/// separate orthogonal concern (Python `external=True`).
///
/// See `khalkulo/conductor/projects/silicon_correct/tracks/02_drc_klayout_primary/plan.md`.
type Compat =
    | Klayout
    | Magic

/// Default compat target. KLayout from Track 02 onward.
let defaultCompat : Compat = Klayout

/// Stringify for CLI / JSON / logs. Mirrors Python's lowercase
/// enum values exactly so cross-language tooling can compare
/// strings without case-folding.
let toString (c: Compat) : string =
    match c with
    | Klayout -> "klayout"
    | Magic   -> "magic"

/// Inverse of `toString`. Case-insensitive — accepts the common
/// `Klayout`/`KLayout`/`klayout`/`KLAYOUT` variations. Returns
/// `None` on unknown input rather than throwing — callers can
/// then decide whether to default or fail loudly.
let parse (s: string) : Compat option =
    match (if isNull s then "" else s.ToLowerInvariant()) with
    | "klayout" -> Some Klayout
    | "magic"   -> Some Magic
    | _         -> None
