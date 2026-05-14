module Rekolektion.Viz.Render.Skia.DimensionOverlay

open SkiaSharp
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout
open Rekolektion.Viz.Render.Skia

/// Settings for the dimension overlay. `MaxArrows` caps the total
/// number of arrows drawn for one selected → neighbor pair so the
/// canvas stays legible on dense layouts; the smallest-gap pairs
/// always win.
type Settings = {
    MaxArrowsPerPair : int
}

let defaultSettings : Settings = {
    MaxArrowsPerPair = 10
}

type private Side = | Right | Left | Top | Bottom

type private Candidate = {
    Layer : int * int
    Side  : Side
    Gap   : int64
    SelBb : int64 * int64 * int64 * int64
    NbBb  : int64 * int64 * int64 * int64
}

/// Classify how `b` sits relative to `a` (axis-aligned bboxes).
/// Returns Some Side iff `b` is purely on one cardinal side of
/// `a` — projections overlap on the perpendicular axis and are
/// disjoint on the parallel axis. Diagonal pairs (no axis-aligned
/// facing) yield None and are skipped — orthogonal dims only.
let private classifySide
        ((ax1, ay1, ax2, ay2): int64 * int64 * int64 * int64)
        ((bx1, by1, bx2, by2): int64 * int64 * int64 * int64)
        : Side option =
    let yOverlap = (min ay2 by2) > (max ay1 by1)
    let xOverlap = (min ax2 bx2) > (max ax1 bx1)
    if yOverlap && bx1 >= ax2 then Some Right
    elif yOverlap && bx2 <= ax1 then Some Left
    elif xOverlap && by1 >= ay2 then Some Top
    elif xOverlap && by2 <= ay1 then Some Bottom
    else None

let private gapAlong
        (side: Side)
        ((ax1, ay1, ax2, ay2): int64 * int64 * int64 * int64)
        ((bx1, by1, bx2, by2): int64 * int64 * int64 * int64)
        : int64 =
    match side with
    | Right  -> bx1 - ax2
    | Left   -> ax1 - bx2
    | Top    -> by1 - ay2
    | Bottom -> ay1 - by2

let private orthEndpoints
        (side: Side)
        ((ax1, ay1, ax2, ay2): int64 * int64 * int64 * int64)
        ((bx1, by1, bx2, by2): int64 * int64 * int64 * int64)
        : (int64 * int64) * (int64 * int64) =
    match side with
    | Right ->
        let yMid = (max ay1 by1 + min ay2 by2) / 2L
        (ax2, yMid), (bx1, yMid)
    | Left ->
        let yMid = (max ay1 by1 + min ay2 by2) / 2L
        (ax1, yMid), (bx2, yMid)
    | Top ->
        let xMid = (max ax1 bx1 + min ax2 bx2) / 2L
        (xMid, ay2), (xMid, by1)
    | Bottom ->
        let xMid = (max ax1 bx1 + min ax2 bx2) / 2L
        (xMid, ay1), (xMid, by2)

let private worldToScreen (vb: LayerPainter.ViewBox) (x: float) (y: float) : SKPoint =
    let dx = float (vb.MaxX - vb.MinX) |> max 1.0
    let dy = float (vb.MaxY - vb.MinY) |> max 1.0
    let sx = (x - float vb.MinX) / dx * float vb.PixelW
    let sy = float vb.PixelH - ((y - float vb.MinY) / dy * float vb.PixelH)
    SKPoint(float32 sx, float32 sy)

let private formatUm (umPerDbu: float) (dbu: int64) : string =
    // ASCII "um" rather than "µm" — Skia's default SKPaint typeface
    // doesn't carry the MICRO SIGN (U+00B5) glyph and renders tofu.
    sprintf "%.3f um" (float dbu * umPerDbu)

let private layerLabelOf (layer: int) (dt: int) : string =
    match Layout.Layer.bySky130Number layer dt with
    | Some l -> l.Name
    | None -> sprintf "L%d/D%d" layer dt

let private drawAxialArrow
        (canvas: SKCanvas)
        (vb: LayerPainter.ViewBox)
        (umPerDbu: float)
        (paintLine: SKPaint)
        (paintText: SKPaint)
        (paintTextBg: SKPaint)
        (side: Side)
        (label: string)
        ((p1x, p1y): int64 * int64)
        ((p2x, p2y): int64 * int64) =
    let s1 = worldToScreen vb (float p1x) (float p1y)
    let s2 = worldToScreen vb (float p2x) (float p2y)
    let lenPx =
        let dx = float (s2.X - s1.X)
        let dy = float (s2.Y - s1.Y)
        sqrt (dx * dx + dy * dy)
    if lenPx < 4.0 then ()
    else
        canvas.DrawLine(s1, s2, paintLine)
        let head = 6.0f
        let drawHead (tip: SKPoint) (signX: float32) (signY: float32) =
            let path = new SKPath()
            match side with
            | Right | Left ->
                path.MoveTo tip
                path.LineTo (SKPoint(tip.X + signX * head, tip.Y - head * 0.5f))
                path.LineTo (SKPoint(tip.X + signX * head, tip.Y + head * 0.5f))
            | Top | Bottom ->
                path.MoveTo tip
                path.LineTo (SKPoint(tip.X - head * 0.5f, tip.Y + signY * head))
                path.LineTo (SKPoint(tip.X + head * 0.5f, tip.Y + signY * head))
            path.Close()
            canvas.DrawPath(path, paintLine)
            path.Dispose()
        match side with
        | Right ->  drawHead s1  1.0f 0.0f; drawHead s2 -1.0f 0.0f
        | Left ->   drawHead s1 -1.0f 0.0f; drawHead s2  1.0f 0.0f
        | Top ->    drawHead s1  0.0f 1.0f; drawHead s2  0.0f -1.0f
        | Bottom -> drawHead s1  0.0f -1.0f; drawHead s2 0.0f  1.0f
        let mid = SKPoint((s1.X + s2.X) * 0.5f, (s1.Y + s2.Y) * 0.5f)
        let mutable bounds = SKRect()
        paintText.MeasureText(label, &bounds) |> ignore
        let padX = 4.0f
        let padY = 2.0f
        // For horizontal (X-axis) dims, lift the label above the
        // arrow so it doesn't sit on top of the line. Vertical
        // (Y-axis) labels stay centered — they sit beside the line.
        let labelOffsetY =
            match side with
            | Right | Left -> -(bounds.Height + padY * 2.0f + 2.0f)
            | Top | Bottom -> 0.0f
        let labelMid = SKPoint(mid.X, mid.Y + labelOffsetY)
        let bgRect =
            SKRect(
                labelMid.X - bounds.Width  * 0.5f - padX,
                labelMid.Y - bounds.Height * 0.5f - padY,
                labelMid.X + bounds.Width  * 0.5f + padX,
                labelMid.Y + bounds.Height * 0.5f + padY)
        canvas.DrawRect(bgRect, paintTextBg)
        let textX = labelMid.X - bounds.Width * 0.5f
        let textY = labelMid.Y + bounds.Height * 0.5f - 1.0f
        canvas.DrawText(label, textX, textY, paintText)

/// Render polygon-to-polygon orthogonal dimension arrows. For each
/// (selected, neighbor) pair, on each shared physical layer, walk
/// every polygon in the selected cell and find its closest
/// orthogonally-facing polygon in the neighbor — at most one
/// arrow per source polygon. Diagonal pairs (no axis overlap) are
/// skipped per the locked decision "orthogonal dims only".
///
/// All candidate arrows are sorted by gap ascending and the first
/// `MaxArrowsPerPair` are drawn — the smallest gaps win on dense
/// layouts so the user sees DRC-relevant spacings first.
///
/// `instancePolyBboxes` maps top-instance index → per-(layer,
/// datatype) → polygon bbox array. Recomputed every frame from the
/// active library so arrows track drag edits live.
let render
        (canvas: SKCanvas)
        (vb: LayerPainter.ViewBox)
        (doc: Document)
        (instances: Instances.Instance array)
        (selected: Set<int>)
        (instancePolyBboxes:
            Map<int, Map<int * int, (int64 * int64 * int64 * int64) array>>)
        (settings: Settings) =
    if selected.IsEmpty || instances.Length = 0 then () else
    // X-axis dims (Right / Left) draw in cyan; Y-axis dims (Top /
    // Bottom) draw in amber. Different colors make the axis read at
    // a glance instead of trying to discriminate from arrowhead
    // orientation alone.
    use paintLineX =
        new SKPaint(
            Style = SKPaintStyle.Stroke,
            Color = SKColor(0x40uy, 0xE0uy, 0xFFuy, 0xFFuy),
            StrokeWidth = 1.5f,
            IsAntialias = true)
    use paintLineY =
        new SKPaint(
            Style = SKPaintStyle.Stroke,
            Color = SKColor(0xFFuy, 0xC8uy, 0x00uy, 0xFFuy),
            StrokeWidth = 1.5f,
            IsAntialias = true)
    use paintText =
        new SKPaint(
            Style = SKPaintStyle.Fill,
            Color = SKColors.White,
            IsAntialias = true,
            TextSize = 11.0f)
    use paintTextBg =
        new SKPaint(
            Style = SKPaintStyle.Fill,
            Color = SKColor(0x00uy, 0x00uy, 0x00uy, 0xC0uy),
            IsAntialias = true)
    let umPerDbu = float doc.Units.DbuNm * 1.0e-3

    let isPhysical (layer: int) (dt: int) =
        not (Layout.Layer.isNonPhysical layer dt)

    // Collect every candidate arrow (selected, neighbor, layer,
    // sel-poly, nb-poly, side, gap), then keep only the smallest-
    // gap entries up to the per-pair cap.
    let candidatesPerPair =
        System.Collections.Generic.Dictionary<int * int, ResizeArray<Candidate>>()

    let getOrCreate key =
        match candidatesPerPair.TryGetValue key with
        | true, lst -> lst
        | _ ->
            let lst = ResizeArray<Candidate>()
            candidatesPerPair.[key] <- lst
            lst

    // For an orthogonally-facing pair, the perpendicular-axis
    // overlap interval — used to cluster candidates into "approach
    // bands" where the cells get close to each other. Two
    // candidates with overlapping perpendicular intervals belong
    // to the same band.
    let perpOverlap
            (side: Side)
            ((ax1, ay1, ax2, ay2): int64 * int64 * int64 * int64)
            ((bx1, by1, bx2, by2): int64 * int64 * int64 * int64)
            : int64 * int64 =
        match side with
        | Right | Left ->
            (max ay1 by1), (min ay2 by2)
        | Top | Bottom ->
            (max ax1 bx1), (min ax2 bx2)

    // Collect every orthogonally-facing polygon pair across all
    // shared physical layers — no shadow filter, no per-polygon
    // dedup. Banding (below) handles the "outermost" semantics by
    // picking the smallest-gap candidate per spatial band: the
    // outermost faces always have the smallest gap.
    for selIdx in selected do
        match Map.tryFind selIdx instancePolyBboxes with
        | None -> ()
        | Some selLayers ->
            for nbKv in instancePolyBboxes do
                let nbIdx = nbKv.Key
                if nbIdx <> selIdx && not (selected.Contains nbIdx) then
                    let nbLayers = nbKv.Value
                    let lst = getOrCreate (selIdx, nbIdx)
                    for layerKv in selLayers do
                        let key = layerKv.Key
                        let (l, dt) = key
                        if isPhysical l dt then
                            match Map.tryFind key nbLayers with
                            | None -> ()
                            | Some nbArr ->
                                let selArr = layerKv.Value
                                for sBb in selArr do
                                    for nBb in nbArr do
                                        match classifySide sBb nBb with
                                        | Some side ->
                                            let g = gapAlong side sBb nBb
                                            if g >= 0L then
                                                lst.Add {
                                                    Layer = key
                                                    Side  = side
                                                    Gap   = g
                                                    SelBb = sBb
                                                    NbBb  = nBb }
                                        | None -> ()

    // Debug dump (gated on env var so production runs are silent).
    // Set REKOLEKTION_DIM_DEBUG=1 before launching the app to see
    // per-pair candidate counts on stderr.
    if System.Environment.GetEnvironmentVariable("REKOLEKTION_DIM_DEBUG") = "1" then
        for kv in candidatesPerPair do
            let (selIdx, nbIdx) = kv.Key
            let bySide =
                kv.Value.ToArray()
                |> Array.groupBy (fun c -> c.Side)
                |> Array.map (fun (s, arr) ->
                    let minGap =
                        if arr.Length = 0 then 0L
                        else arr |> Array.minBy (fun c -> c.Gap) |> (fun c -> c.Gap)
                    sprintf "%A=%d(min=%d)" s arr.Length minGap)
                |> String.concat " "
            System.Console.Error.WriteLine
                (sprintf "[dim] pair sel=%d nb=%d  %s" selIdx nbIdx bySide)

    // Per pair: split candidates by Side, then within each Side
    // group them into "approach bands" where the perpendicular-axis
    // overlap intervals merge into a connected range. Only the
    // smallest-gap candidate per band is drawn — that pair is the
    // outermost on both faces (smaller gap implies an outer-front
    // shape on each side). One arrow per band per direction; cap
    // applied across all kept candidates.
    let isDebug = System.Environment.GetEnvironmentVariable("REKOLEKTION_DIM_DEBUG") = "1"
    for kv in candidatesPerPair do
        let all = kv.Value.ToArray()
        let sides = [| Right; Left; Top; Bottom |]
        let kept = ResizeArray<Candidate>()
        for side in sides do
            let group =
                all
                |> Array.filter (fun c -> c.Side = side)
            // Annotate each candidate with its perpendicular-axis
            // overlap interval, then sort by interval start so a
            // single forward sweep merges connected bands.
            let withPerp =
                group
                |> Array.map (fun c ->
                    let lo, hi = perpOverlap c.Side c.SelBb c.NbBb
                    c, lo, hi)
                |> Array.sortBy (fun (_, lo, _) -> lo)
            let mutable bandHi : int64 = System.Int64.MinValue
            let mutable bandBest : Candidate option = None
            let flush () =
                match bandBest with
                | Some c -> kept.Add c
                | None -> ()
                bandBest <- None
                bandHi <- System.Int64.MinValue
            for (c, lo, hi) in withPerp do
                if lo > bandHi then
                    flush ()
                    bandBest <- Some c
                    bandHi <- hi
                else
                    let extended = max bandHi hi
                    bandHi <- extended
                    match bandBest with
                    | Some prev when c.Gap < prev.Gap -> bandBest <- Some c
                    | None -> bandBest <- Some c
                    | _ -> ()
            flush ()
            if isDebug then
                let keptForSide =
                    kept
                    |> Seq.filter (fun c -> c.Side = side)
                    |> Seq.length
                System.Console.Error.WriteLine
                    (sprintf "[dim]   side %A: %d candidates -> %d bands kept (cap=%d)"
                        side group.Length keptForSide settings.MaxArrowsPerPair)
        // Across all bands & directions for this pair, keep the
        // tightest gaps up to the cap.
        let final =
            kept.ToArray()
            |> Array.sortBy (fun c -> c.Gap)
        let n = min settings.MaxArrowsPerPair final.Length
        for i in 0 .. n - 1 do
            let c = final.[i]
            let p1, p2 = orthEndpoints c.Side c.SelBb c.NbBb
            let (l, dt) = c.Layer
            // Layer name omitted — color already tells you axis
            // (cyan = X, amber = Y) and the user wanted the
            // labels to stay out of the way of the arrow line.
            let label = formatUm umPerDbu c.Gap
            let paintLine =
                match c.Side with
                | Right | Left -> paintLineX
                | Top | Bottom -> paintLineY
            if isDebug then
                let (px1, py1) = p1
                let (px2, py2) = p2
                System.Console.Error.WriteLine
                    (sprintf "[dim]   draw side=%A gap=%d p1=(%d,%d) p2=(%d,%d)"
                        c.Side c.Gap px1 py1 px2 py2)
            drawAxialArrow
                canvas vb umPerDbu paintLine paintText paintTextBg
                c.Side label p1 p2
