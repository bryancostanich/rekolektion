module Rekolektion.Viz.Core.Layout.LayoutLoader

open System.IO
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Rkt.Types

/// Format-agnostic loader returning the canonical `Rkt.Document`
/// model. Switches on file extension:
///   .gds / .gds2 → Gds.Reader.readGds (already Rkt-flavored)
///   .mag         → Layout.MagToLayout.load (Rkt-flavored)
///
/// Returns the document plus any warnings (Mag's unknown-layer log
/// + missing-subcell notices). GDS produces an empty warning list.
///
/// Legacy ReRAM layer numbers (6/0, 8/0, 40/0, …) resolve inside
/// `Layout.Layer.bySky130Number` via the alias table — no separate
/// normalization pass is needed. `Layout.LayerAlias` is retained for
/// the legacy `Gds.Types.Library` path but is no longer in this
/// chain.
let load (path: string) : Document * string list =
    let ext =
        try (Path.GetExtension path).ToLowerInvariant()
        with _ -> ""
    match ext with
    | ".gds" | ".gds2" ->
        Gds.Reader.readGds path, []
    | ".mag" ->
        MagToLayout.load path []
    | ".rkt" ->
        // Single-file load — imports are not resolved at v1; if the
        // file references cells from `(import "...")` siblings, the
        // viz tool will see unresolved SRef targets. Warn so the
        // user knows; full multi-file load resolution is future
        // work.
        match Rkt.Reader.readFile path with
        | Ok (_, doc) ->
            let warnings =
                if doc.Imports |> List.isEmpty then []
                else
                    [ sprintf "rkt file has %d (import ...) form(s); imports are not resolved at v1"
                        doc.Imports.Length ]
            doc, warnings
        | Error e ->
            failwithf "rkt load failed: %s%s"
                (match e.Path with Some p -> p + ": " | None -> "")
                e.Message
    | _ ->
        // Be forgiving: try the GDS reader first (handles a few
        // legacy extensions like .stream), surface a clearer error
        // if it fails outright.
        try Gds.Reader.readGds path, []
        with ex ->
            failwithf "Unsupported layout extension '%s' for file %s (%s)"
                ext path ex.Message

/// Transitional shim — returns the same payload converted back into
/// a `Gds.Types.Library` plus warnings. Kept for callers that still
/// operate on the legacy model. Each call site of this function is a
/// migration candidate; once a consumer takes `Rkt.Document`
/// directly, its call site moves to `load`.
let loadAsLibrary (path: string) : Rekolektion.Viz.Core.Gds.Types.Library * string list =
    let doc, warnings = load path
    Rkt.ToGds.toLibrary doc, warnings
