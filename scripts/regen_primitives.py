"""Regenerate every primitive `.rkt` under `cell_designs/primitives/`.

Used for track 06 step 5: re-mint each primitive so it picks up the
new `(kind device-terminal)` annotations on FET port labels. The
output is then compared against the pre-regen snapshot via
`scripts/diff_primitives.py` to confirm the diff is tag-only — no
geometric drift.

Reads each primitive's `(meta (generator …) (params …))` block,
dispatches to the matching `gen_*_hv` / `gen_*_01v8` function, and
lets the cache invalidate on its own (the digest now includes
topc/botc, so any file minted before those params were added
auto-misses and re-mints).

Usage:
    .venv/bin/python scripts/regen_primitives.py

By default operates on `cell_designs/primitives/`. Override with
`--primitives-dir`.

Use `--force` to delete each primitive file before calling its
generator. Required when the existing file's digest still
matches the new code path (so the cache would short-circuit) but
you want to pick up cosmetic schema additions like `(kind …)`
tags that don't change the digest.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

from rekolektion.primitives.sky130 import (
    gen_nfet_01v8,
    gen_nfet_hv,
    gen_pfet_01v8,
    gen_pfet_hv,
)


_GENERATOR_RE = re.compile(r'\(generator\s+"([^"]+)"\)')
_PARAMS_RE = re.compile(r"\(params\s+(.*?)\)\s*(?:\(source|\(generated|\(digest|\)\s*\)\s*\))", re.DOTALL)
_PARAM_PAIR_RE = re.compile(r"\(\s*(\w+)\s+([^()\s]+)\s*\)")

# generator string -> (gen_function, supported kwargs)
_GENERATORS = {
    "sky130/nfet_hv": gen_nfet_hv,
    "sky130/pfet_hv": gen_pfet_hv,
    "sky130/nfet_01v8": gen_nfet_01v8,
    "sky130/pfet_01v8": gen_pfet_01v8,
}


def _parse_value(token: str) -> object:
    """Convert a param value token from the .rkt file to a Python
    value. Mirrors the generator's accepted types: float, int, bool
    (encoded as `true`/`false` symbols)."""

    if token in ("true", "false"):
        return token == "true"
    try:
        if "." in token:
            return float(token)
        return int(token)
    except ValueError:
        return token


def _read_meta(path: Path) -> tuple[str, dict[str, object]]:
    """Pull `(generator "...")` and `(params (k v) (k v) ...)` out of
    a primitive's meta block via regex. Returns (generator, params)."""

    text = path.read_text(encoding="utf-8")
    gen_m = _GENERATOR_RE.search(text)
    if not gen_m:
        raise ValueError(f"{path}: no (generator …) found")
    params_m = _PARAMS_RE.search(text)
    if not params_m:
        raise ValueError(f"{path}: no (params …) found")
    params: dict[str, object] = {}
    for pair_m in _PARAM_PAIR_RE.finditer(params_m.group(1)):
        params[pair_m.group(1)] = _parse_value(pair_m.group(2))
    return gen_m.group(1), params


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--primitives-dir",
        type=Path,
        default=Path("cell_designs/primitives"),
        help="directory containing primitive .rkt files (default: ./cell_designs/primitives)",
    )
    parser.add_argument(
        "--force",
        action="store_true",
        help=(
            "delete each primitive file before re-running its generator. "
            "Use when a schema addition (e.g. (kind …)) doesn't change "
            "the digest, so the cache would otherwise short-circuit."
        ),
    )
    args = parser.parse_args(argv)

    files = sorted(args.primitives_dir.glob("*.rkt"))
    if not files:
        print(f"no .rkt files in {args.primitives_dir}", file=sys.stderr)
        return 2

    print(f"regenerating {len(files)} primitives in {args.primitives_dir}")
    for path in files:
        generator, params = _read_meta(path)
        fn = _GENERATORS.get(generator)
        if fn is None:
            print(f"SKIP  {path.name}  (unknown generator '{generator}')")
            continue
        # Translate .rkt param keys to the generator's kwarg names.
        kwargs = {
            "w_um": params["w"],
            "l_um": params["l"],
        }
        if "nf" in params:
            kwargs["nf"] = params["nf"]
        if "m" in params:
            kwargs["m"] = params["m"]
        if "guard" in params:
            kwargs["guard"] = params["guard"]
        if "topc" in params:
            kwargs["topc"] = params["topc"]
        if "botc" in params:
            kwargs["botc"] = params["botc"]
        kwargs["primitives_dir"] = args.primitives_dir
        if args.force:
            # Drop the existing file so the generator's `cache_hit`
            # short-circuit can't fire. The digest may still match
            # the new code path; we want the file rewritten anyway.
            path.unlink()
        name = fn(**kwargs)
        print(f"OK    {name}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
