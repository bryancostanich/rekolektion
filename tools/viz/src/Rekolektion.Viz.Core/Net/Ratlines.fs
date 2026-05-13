module Rekolektion.Viz.Core.Net.Ratlines

open Rekolektion.Viz.Core.Rkt.Types

/// One pin of a net on a specific top-level instance — the
/// world-DBU centroid of every label of that net that descends
/// from that instance. Used as a ratline endpoint.
type Pin = {
    /// Top-cell element index. None when the label was authored
    /// directly in the top cell (rare but possible).
    TopInstanceIndex : int option
    Position         : Point
    /// Number of labels contributing to this pin's centroid.
    /// Useful for tie-breaking or pruning trivially-short hops.
    LabelCount       : int
}

/// One ratline endpoint set for a net. `Pins` has at least one
/// entry — nets that don't show up at all are dropped before
/// reaching this type. A net with `Pins.Length < 2` has nothing
/// to connect across the layout and is rendered as a no-op.
type NetRoute = {
    Name : string
    Pins : Pin array
}

/// Compute per-net per-instance pin centroids from the labels
/// reachable through the hierarchy. Pins for the same net on the
/// same top-instance are collapsed into one centroid; same-net
/// labels in DIFFERENT top-instances yield separate pins, which
/// are what we draw lines between.
let compute (doc: Document) : NetRoute array =
    let tagged = Rekolektion.Viz.Core.Layout.Flatten.flattenLabelsTagged doc
    // (net, instance) -> running sum (sumX, sumY, count)
    let acc =
        System.Collections.Generic.Dictionary<string * int option, int64 * int64 * int>()
    for (topIdx, label) in tagged do
        if label.Text <> "" then
            let key = (label.Text, topIdx)
            match acc.TryGetValue key with
            | true, (sx, sy, n) ->
                acc.[key] <- (sx + label.Origin.X, sy + label.Origin.Y, n + 1)
            | _ ->
                acc.[key] <- (label.Origin.X, label.Origin.Y, 1)
    acc
    |> Seq.map (fun kv ->
        let (name, topIdx) = kv.Key
        let (sx, sy, n) = kv.Value
        let pin : Pin = {
            TopInstanceIndex = topIdx
            Position = { X = sx / int64 n; Y = sy / int64 n }
            LabelCount = n
        }
        name, pin)
    |> Seq.groupBy fst
    |> Seq.map (fun (name, pairs) ->
        let pins = pairs |> Seq.map snd |> Seq.toArray
        { Name = name; Pins = pins })
    |> Seq.filter (fun route -> route.Pins.Length >= 2)
    |> Seq.toArray
