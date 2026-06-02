/// Tick-math for the viewport rulers (top + left gutters around
/// the 2D canvas). Pure functions on (worldRange, pixelsPerUm) —
/// no Avalonia or SkiaSharp dependencies — so the controls'
/// render pass is a thin shell over a fully-unit-testable core.
///
/// Conventions:
///   * All world coords are in micrometers (µm). Callers convert
///     DBU → µm at the boundary using `units.DbuNm * 1e-3`.
///   * Major step is chosen from a 1-2-5 × 10^n sequence so the
///     visible spacing is round-number-friendly at every zoom
///     level (Photoshop / Illustrator convention).
///   * Minor step = major / 5 always (4 minor ticks between
///     adjacent majors). Cleaner subdivisions than a /2 fallback
///     and stays consistent across the 1/2/5 leading-digit cases.
module Rekolektion.Viz.Core.Layout.RulerTicks

/// One tick on a ruler. `PxOffset` is the distance from the
/// gutter's start in pixels; `Um` is the world µm coord; `IsMajor`
/// flags labelled ticks vs unlabelled minor subdivisions.
type Tick = {
    PxOffset : float
    Um       : float
    IsMajor  : bool
}

/// Pre-computed 1-2-5 × 10^n step ladder covering 1 nm (1e-3 µm)
/// to 100 mm (1e5 µm). Held as a sorted array so step selection
/// is a single linear scan — the ladder has ~30 entries so
/// scanning is faster than a binary search and avoids edge-case
/// rounding when the target sits between two candidates.
let private stepLadderUm : float[] =
    [|
        for n in -3 .. 5 do
            let m = 10.0 ** float n
            yield 1.0 * m
            yield 2.0 * m
            yield 5.0 * m
    |]

/// Pick the smallest 1-2-5 step (in µm) whose pixel pitch is at
/// least `minPxPerMajor`. The pitch threshold is the minimum
/// spacing the renderer needs between major ticks for the labels
/// to read clearly without overlapping — pick `minPxPerMajor`
/// based on the label font size + a small margin.
///
/// Returns the ladder's top entry when no step is large enough
/// (extreme zoom-out). Returns `nan` when `pxPerUm <= 0`.
let pickMajorStepUm (pxPerUm: float) (minPxPerMajor: float) : float =
    if pxPerUm <= 0.0 || System.Double.IsNaN pxPerUm then nan
    else
        let targetUm = minPxPerMajor / pxPerUm
        let mutable picked = stepLadderUm.[stepLadderUm.Length - 1]
        let mutable i = 0
        let mutable doneScan = false
        while not doneScan && i < stepLadderUm.Length do
            if stepLadderUm.[i] >= targetUm then
                picked <- stepLadderUm.[i]
                doneScan <- true
            i <- i + 1
        picked

/// Minor step = major / 5. Yields 4 minor ticks between adjacent
/// majors — clean for both 1/2 and 5 leading digits (1µm major
/// → 0.2µm minor; 2µm major → 0.4µm minor; 5µm major → 1µm minor).
let pickMinorStepUm (majorStepUm: float) : float =
    majorStepUm / 5.0

/// First multiple of `step` at or above `start`. Pulled out so
/// the major / minor first-tick computation stays readable and
/// the rounding bias is consistent across both passes.
let private firstAtOrAfter (start: float) (step: float) : float =
    System.Math.Ceiling(start / step) * step

/// Build the tick list for one ruler span. `startUm` / `endUm`
/// are the world µm coords at the gutter's start / end pixel;
/// `pxPerUm` converts µm offsets back into screen pixels;
/// `minPxPerMajor` gates the major-step choice (larger → coarser
/// labels, fewer overlap risks). Returns an empty list when the
/// span is degenerate or pxPerUm is non-positive.
///
/// Output is sorted ascending by µm. Majors and minors that share
/// a position de-dupe in favour of the major.
let ticks
        (startUm: float) (endUm: float)
        (pxPerUm: float) (minPxPerMajor: float)
        : Tick list =
    if endUm <= startUm || pxPerUm <= 0.0
       || System.Double.IsNaN startUm || System.Double.IsNaN endUm
       || System.Double.IsNaN pxPerUm then []
    else
        let major = pickMajorStepUm pxPerUm minPxPerMajor
        let minor = pickMinorStepUm major
        // Tolerance for "is this minor coincident with a major"
        // — millions-of-decimal-places below the major step. Any
        // legitimate same-position match snaps within this.
        let coincidenceTol = major * 1e-6
        let mkSet (step: float) (isMajor: bool) : Tick list =
            let mutable v = firstAtOrAfter startUm step
            let acc = ResizeArray<Tick>()
            while v < endUm do
                acc.Add { PxOffset = (v - startUm) * pxPerUm
                          Um = v
                          IsMajor = isMajor }
                v <- v + step
            List.ofSeq acc
        let majors = mkSet major true
        let minorsAll = mkSet minor false
        let minorsClean =
            minorsAll
            |> List.filter (fun m ->
                not (majors |> List.exists (fun maj ->
                    abs (m.Um - maj.Um) < coincidenceTol)))
        (majors @ minorsClean) |> List.sortBy (fun t -> t.Um)

/// Convert a viewport-center world coord (in DBU) plus a gutter
/// span (in pixels) into the `(startUm, endUm)` range the gutter
/// covers. Pulled out so the controls don't reimplement the
/// camera math.
///
/// `centerDbu` — world DBU at the gutter's center pixel.
/// `pxPerDbu` — pixels per DBU at the current zoom.
/// `spanPx`   — gutter length in pixels.
/// `dbuNm`    — nm per DBU (from `Units.DbuNm`).
let gutterRangeUm
        (centerDbu: float) (pxPerDbu: float)
        (spanPx: float) (dbuNm: int) : float * float =
    if pxPerDbu <= 0.0 || dbuNm <= 0 then 0.0, 0.0
    else
        let halfDbu = spanPx / 2.0 / pxPerDbu
        let umPerDbu = float dbuNm * 1e-3
        (centerDbu - halfDbu) * umPerDbu,
        (centerDbu + halfDbu) * umPerDbu

/// Pixels per micrometer at the current zoom. Convenience for
/// the ruler controls so they don't repeat `pxPerDbu * dbuNm * 1e3`
/// at every call site.
let pxPerUm (pxPerDbu: float) (dbuNm: int) : float =
    if dbuNm <= 0 then 0.0
    else pxPerDbu * (1.0 / (float dbuNm * 1e-3))

/// Format a µm label so it renders compactly at every step size.
///   * step >= 1 µm → integer ("12")
///   * step >= 0.1 µm → 1 decimal ("0.5")
///   * step >= 0.01 µm → 2 decimals ("0.05")
///   * step < 0.01 µm → 3 decimals ("0.002")
/// Negative values keep the minus sign; the zero point reads as
/// "0" regardless of step (cleaner than "0.0" / "0.00" / "0.000").
let formatLabel (umValue: float) (majorStepUm: float) : string =
    if abs umValue < majorStepUm * 1e-6 then "0"
    else
        let fmt =
            if majorStepUm >= 1.0 then "F0"
            elif majorStepUm >= 0.1 then "F1"
            elif majorStepUm >= 0.01 then "F2"
            else "F3"
        umValue.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture)
