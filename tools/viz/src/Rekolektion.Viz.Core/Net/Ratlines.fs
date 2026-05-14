module Rekolektion.Viz.Core.Net.Ratlines

open Rekolektion.Viz.Core.Rkt.Types

/// One pin of a net on a specific top-level instance — the
/// world-DBU centroid of every label of that net that descends
/// from that instance, anchored to the polygon that the label
/// sits on. Used as a ratline endpoint in 2D and 3D.
type Pin = {
    /// Top-cell element index. None when the label was authored
    /// directly in the top cell (rare but possible).
    TopInstanceIndex : int option
    /// Centroid X/Y of the contributing polygons (each label finds
    /// its containing poly and contributes that poly's bbox center;
    /// labels with no containing poly fall back to the label
    /// origin). Stays in flat-DBU world coords.
    Position         : Point
    /// Z height in micrometers at the top of the contributing
    /// polygon's layer (StackZ + Thickness). Averaged when multiple
    /// contributing polys span layers. The 3D canvas uses this
    /// directly so endpoints land on the metal stack instead of
    /// floating at a fixed height. The 2D canvas ignores Z.
    ZUm              : float
    /// Number of labels contributing to this pin's centroid.
    /// Useful for tie-breaking or pruning trivially-short hops.
    LabelCount       : int
}

/// One edge of a net's spanning tree. `From` and `To` are indices
/// into `NetRoute.Pins`. Order is arbitrary — edges are undirected.
type NetEdge = {
    From : int
    To   : int
}

/// One ratline endpoint set for a net. `Pins` has at least one
/// entry — nets that don't show up at all are dropped before
/// reaching this type. A net with `Pins.Length < 2` has nothing
/// to connect across the layout and is rendered as a no-op.
///
/// `Mst` is the rectilinear minimum-spanning-tree over `Pins`, with
/// exactly `Pins.Length - 1` edges (or empty when there's a single
/// pin). Pre-computed at `compute` time so the renderer is purely
/// visual.
///
/// `IsPower` flags nets whose name matches a power/ground pattern
/// (`VDD`, `VSS`, `VPWR`, `GND`, …). Power nets typically have so
/// many pins that even an MST is a hairball, so the "all on" master
/// toggle excludes them by default — see `Visibility.HidePowerRatlines`.
type NetRoute = {
    Name    : string
    Pins    : Pin array
    Mst     : NetEdge array
    IsPower : bool
}

/// Manhattan (rectilinear) distance between two pins. Matches how
/// signals actually route on chip, and gives MST edges that hint at
/// "this net wants to span this much wire."
let private manhattan (a: Pin) (b: Pin) : int64 =
    abs (a.Position.X - b.Position.X)
    + abs (a.Position.Y - b.Position.Y)

/// Rectilinear MST via Prim's. O(N²) time, O(N) space. Plenty fast
/// for signal nets (tens of pins) and acceptable for power nets
/// (thousands of pins — ~10M ops, sub-second).
let mstOf (pins: Pin array) : NetEdge array =
    let n = pins.Length
    if n < 2 then [||] else
    let inTree = Array.zeroCreate<bool> n
    let minCost = Array.create n System.Int64.MaxValue
    let parent = Array.create n -1
    inTree.[0] <- true
    for i in 1 .. n - 1 do
        minCost.[i] <- manhattan pins.[0] pins.[i]
        parent.[i] <- 0
    let edges = ResizeArray<NetEdge>()
    for _ in 1 .. n - 1 do
        // Find the unvisited node with the smallest min-cost-into-tree.
        let mutable best = -1
        let mutable bestCost = System.Int64.MaxValue
        for i in 0 .. n - 1 do
            if not inTree.[i] && minCost.[i] < bestCost then
                best <- i
                bestCost <- minCost.[i]
        if best >= 0 then
            inTree.[best] <- true
            edges.Add({ From = parent.[best]; To = best })
            // Relax: every unvisited node may now have a shorter
            // path through `best`.
            for i in 0 .. n - 1 do
                if not inTree.[i] then
                    let cost = manhattan pins.[best] pins.[i]
                    if cost < minCost.[i] then
                        minCost.[i] <- cost
                        parent.[i] <- best
    edges.ToArray()

/// Heuristic: does this net name look like a power or ground rail?
/// Matches the SKY130 conventions (`VPWR`, `VGND`, `VPB`, `VNB`)
/// plus the common cross-vendor names (`VDD`, `VSS`, `GND`, `VCC`,
/// `VEE`) and mixed-signal split variants (`VDDA`, `VSSD`, …).
///
/// Case-insensitive. False on borderline cases — better to under-
/// classify (user sees the net) than misclassify a signal as power.
let isLikelyPowerNet (name: string) : bool =
    if System.String.IsNullOrWhiteSpace name then false else
    let upper = name.ToUpperInvariant()
    let isExact =
        match upper with
        | "VDD" | "VSS" | "GND" | "VCC" | "VEE"
        | "VPWR" | "VGND" | "VPB" | "VNB"
        | "AVDD" | "AVSS" | "DVDD" | "DVSS"
        | "VBAT" | "VREF" -> true
        | _ -> false
    if isExact then true else
    // Common prefix patterns: VDD_*, VSS_*, GND_*, VPWR_*, VGND_*.
    let prefixes = [| "VDD"; "VSS"; "GND"; "VPWR"; "VGND"; "VCC"; "VEE" |]
    prefixes
    |> Array.exists (fun p ->
        upper.StartsWith p
        && upper.Length > p.Length
        && (let c = upper.[p.Length] in c = '_' || c = ':' || c = '/'))

/// Per-flat-poly bbox + layer-Z. Computed once and indexed by
/// layer-number so per-label lookup walks only same-layer-number
/// candidates instead of every flat poly. The label/poly datatypes
/// usually differ in SKY130 (e.g. met1 polys = 68/20, met1 labels
/// = 68/5), so the index key is layer NUMBER only.
type private FlatPolyAnchor = {
    XMin   : int64
    YMin   : int64
    XMax   : int64
    YMax   : int64
    /// Z in micrometers at the top of this layer.
    TopZUm : float
}

let private buildPolyIndex
        (flat: Rekolektion.Viz.Core.Layout.Flatten.FlatPolygon array)
        : System.Collections.Generic.Dictionary<int, ResizeArray<FlatPolyAnchor>> =
    let dict = System.Collections.Generic.Dictionary<int, ResizeArray<FlatPolyAnchor>>()
    for poly in flat do
        if poly.Points.Length > 0 then
            let topZ =
                Rekolektion.Viz.Core.Layout.Layer.bySky130Number poly.Layer poly.DataType
                |> Option.map (fun l -> l.StackZ + l.Thickness)
                |> Option.defaultValue 0.0
            let mutable xMin = System.Int64.MaxValue
            let mutable yMin = System.Int64.MaxValue
            let mutable xMax = System.Int64.MinValue
            let mutable yMax = System.Int64.MinValue
            for p in poly.Points do
                if p.X < xMin then xMin <- p.X
                if p.X > xMax then xMax <- p.X
                if p.Y < yMin then yMin <- p.Y
                if p.Y > yMax then yMax <- p.Y
            let anchor =
                { XMin = xMin; YMin = yMin
                  XMax = xMax; YMax = yMax
                  TopZUm = topZ }
            match dict.TryGetValue poly.Layer with
            | true, list -> list.Add anchor
            | _ ->
                let list = ResizeArray<FlatPolyAnchor>()
                list.Add anchor
                dict.[poly.Layer] <- list
    dict

/// Find the smallest containing-bbox poly on the same layer-number
/// for a label origin. "Smallest" so a label sitting on a met1
/// stripe inside a wider areaid box anchors to the stripe, not the
/// box. Returns None when no same-layer poly contains the label.
let private anchorForLabel
        (idx: System.Collections.Generic.Dictionary<int, ResizeArray<FlatPolyAnchor>>)
        (label: Rekolektion.Viz.Core.Layout.Flatten.FlatLabel)
        : FlatPolyAnchor option =
    match idx.TryGetValue label.Layer with
    | false, _ -> None
    | true, list ->
        let mutable best : FlatPolyAnchor voption = ValueNone
        let mutable bestArea = System.Int64.MaxValue
        for a in list do
            if label.Origin.X >= a.XMin && label.Origin.X <= a.XMax
               && label.Origin.Y >= a.YMin && label.Origin.Y <= a.YMax then
                let area = (a.XMax - a.XMin) * (a.YMax - a.YMin)
                if area < bestArea then
                    bestArea <- area
                    best <- ValueSome a
        match best with
        | ValueNone -> None
        | ValueSome a -> Some a

/// Compute per-net per-instance pin centroids from the labels
/// reachable through the hierarchy. Each label is anchored to the
/// smallest same-layer polygon that contains its origin: the pin
/// X/Y is the bbox center of that polygon, the pin Z is the top of
/// that polygon's layer in micrometers. Labels with no containing
/// poly fall back to the label's own origin and a Z of 0
/// (substrate plane — visually obvious "no anchor found"). Pins for
/// the same net on the same top-instance are collapsed by averaging
/// every contributing anchor; labels in DIFFERENT top-instances
/// yield separate pins, which are what we draw lines between.
let compute
        (doc: Document)
        (flat: Rekolektion.Viz.Core.Layout.Flatten.FlatPolygon array)
        : NetRoute array =
    let tagged = Rekolektion.Viz.Core.Layout.Flatten.flattenLabelsTagged doc
    let polyIdx = buildPolyIndex flat
    // (net, instance) -> running sum (sumX, sumY, sumZ, count)
    let acc =
        System.Collections.Generic.Dictionary<string * int option, int64 * int64 * float * int>()
    for (topIdx, label) in tagged do
        if label.Text <> "" then
            let (anchorX, anchorY, anchorZ) =
                match anchorForLabel polyIdx label with
                | Some a ->
                    let cx = (a.XMin + a.XMax) / 2L
                    let cy = (a.YMin + a.YMax) / 2L
                    cx, cy, a.TopZUm
                | None ->
                    label.Origin.X, label.Origin.Y, 0.0
            let key = (label.Text, topIdx)
            match acc.TryGetValue key with
            | true, (sx, sy, sz, n) ->
                acc.[key] <- (sx + anchorX, sy + anchorY, sz + anchorZ, n + 1)
            | _ ->
                acc.[key] <- (anchorX, anchorY, anchorZ, 1)
    acc
    |> Seq.map (fun kv ->
        let (name, topIdx) = kv.Key
        let (sx, sy, sz, n) = kv.Value
        let pin : Pin = {
            TopInstanceIndex = topIdx
            Position = { X = sx / int64 n; Y = sy / int64 n }
            ZUm = sz / float n
            LabelCount = n
        }
        name, pin)
    |> Seq.groupBy fst
    |> Seq.map (fun (name, pairs) ->
        let pins = pairs |> Seq.map snd |> Seq.toArray
        { Name = name
          Pins = pins
          Mst = mstOf pins
          IsPower = isLikelyPowerNet name })
    |> Seq.filter (fun route -> route.Pins.Length >= 2)
    |> Seq.toArray
