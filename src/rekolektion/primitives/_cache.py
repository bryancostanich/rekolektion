"""On-disk cache for primitive `.rkt` files.

Generators call `cache_path(generator, params)` to get the canonical
location of the primitive. If the file is already there, the
generator returns the cell name without re-running Magic. Otherwise
it mints the primitive, writes it to that path, and the next call
will short-circuit.

The cache key is the digest of `(generator, sorted_params)`. The
cell name is human-readable and deterministic from params (e.g.
`nfet_hv_W1p2_L1p0_core`). The digest is for cache validation —
embedded in the `.rkt`'s `(meta (digest ...))` block — so a stale
file with the same name but different param hash is detectable.
"""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

from rekolektion.io import rkt


def _normalize_param_value(value: object) -> object:
    """Reduce a param value to something `json.dumps` can hash
    deterministically. Numbers stay numeric (so 1 and 1.0 hash
    differently the way they round-trip differently in `.rkt`).
    """

    if isinstance(value, rkt.Symbol):
        return {"_atom": value.text}
    if isinstance(value, bool):  # bool is a subclass of int; check first
        return {"_atom": "true" if value else "false"}
    if isinstance(value, (int, float, str)):
        return value
    raise TypeError(
        f"unhashable param value {value!r} of type {type(value).__name__}"
    )


def compute_digest(
    generator: str,
    params: list[rkt.Property],
    *,
    generator_version: int = 1,
) -> str:
    """Stable digest of a generator call. Identical params → identical
    digest. Param order doesn't matter (we sort by key first).

    `generator_version` is a manually-bumped integer per generator
    (e.g., when the generator's emit-side semantics change — a new
    label-tagging pass, a new strap pattern, etc.). Bumping it
    invalidates every cached entry that predates the bump, forcing
    a re-mint on the next call. Without this, generators whose body
    changes (but whose param surface stays identical) silently leave
    stale .rkt files on disk.

    We picked the version-constant approach over hashing the
    generator's source file because (a) it's an explicit, reviewable
    signal of "this change requires a re-mint" — incidental code
    edits (docstrings, refactors, renames) don't churn the digest;
    and (b) source-file hashing is fragile across editor formatting,
    encoding, and import-graph changes. The cost is one line of
    bookkeeping per intentional emit-side change.
    """

    sorted_params = sorted(params, key=lambda p: p.key)
    payload = {
        "generator": generator,
        "generator_version": int(generator_version),
        "params": [
            (p.key, _normalize_param_value(p.value)) for p in sorted_params
        ],
    }
    blob = json.dumps(payload, sort_keys=True, separators=(",", ":"))
    h = hashlib.sha256(blob.encode("utf-8")).hexdigest()
    return f"sha256:{h}"


def cache_path(
    cell_name: str,
    primitives_dir: Path | None = None,
) -> Path:
    """Resolve the on-disk path for a primitive cell.

    Default location: `<repo>/cell_designs/primitives/<name>.rkt`.
    Tests / off-repo workflows can pass `primitives_dir` explicitly.
    The directory is created lazily by the writer.
    """

    if primitives_dir is None:
        # Walk up from this file to find a `cell_designs/` ancestor;
        # if none, fall back to the current working dir's cell_designs.
        here = Path(__file__).resolve().parent
        for ancestor in [here, *here.parents]:
            candidate = ancestor / "cell_designs" / "primitives"
            if candidate.parent.is_dir():
                primitives_dir = candidate
                break
        if primitives_dir is None:
            primitives_dir = Path.cwd() / "cell_designs" / "primitives"
    return primitives_dir / f"{cell_name}.rkt"


def cache_hit(
    cell_name: str,
    digest: str,
    primitives_dir: Path | None = None,
) -> Path | None:
    """If a cached `.rkt` exists at the canonical path and its
    `(meta (digest ...))` matches `digest`, return the path. Otherwise
    return `None`. A name collision with a different digest is NOT a
    hit — the caller will re-mint and overwrite.

    This is a cheap text-grep check; we deliberately don't parse the
    whole file. The digest line is the cache key, full stop.
    """

    path = cache_path(cell_name, primitives_dir)
    if not path.is_file():
        return None
    try:
        text = path.read_text(encoding="utf-8")
    except OSError:
        return None
    needle = f'(digest "{digest}")'
    return path if needle in text else None
