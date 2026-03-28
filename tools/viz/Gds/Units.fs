/// Units of measure for type-safe geometry operations.
/// The compiler will reject mixing nm with um or px without explicit conversion.
module Viz.Gds.Units

[<Measure>] type nm   // nanometers — GDS database units
[<Measure>] type um   // micrometers — layout coordinates
[<Measure>] type px   // pixels — rendering coordinates
[<Measure>] type mm   // millimeters — physical die size

let nmPerUm = 1000.0<nm/um>
let umPerMm = 1000.0<um/mm>

let nmToUm (v: float<nm>) : float<um> = v / nmPerUm
let umToNm (v: float<um>) : float<nm> = v * nmPerUm
let umToMm (v: float<um>) : float<mm> = v / umPerMm
let mmToUm (v: float<mm>) : float<um> = v * umPerMm
