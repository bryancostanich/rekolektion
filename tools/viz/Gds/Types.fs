/// GDS II AST types.
module Viz.Gds.Types

open Viz.Gds.Units

/// A point in GDS database units (nanometers).
type GdsPoint = { X: int<nm>; Y: int<nm> }

/// A polygon boundary on a specific layer/datatype.
type GdsBoundary = {
    Layer: int
    Datatype: int
    Points: GdsPoint list
}

/// A path (centerline + width) on a specific layer/datatype.
type GdsPath = {
    Layer: int
    Datatype: int
    Width: int<nm>
    Points: GdsPoint list
}

/// A structure reference (cell instance).
type GdsSRef = {
    StructureName: string
    Origin: GdsPoint
    /// Rotation in degrees (0 if none).
    Angle: float
    /// Mirror about X axis before rotation.
    Reflected: bool
    /// Magnification factor (1.0 if none).
    Magnification: float
}

/// An array reference (cell instance array).
type GdsARef = {
    StructureName: string
    Origin: GdsPoint
    Columns: int
    Rows: int
    /// Corner points defining the array spacing.
    ColumnVector: GdsPoint
    RowVector: GdsPoint
    Angle: float
    Reflected: bool
    Magnification: float
}

/// An element within a GDS structure.
type GdsElement =
    | Boundary of GdsBoundary
    | Path of GdsPath
    | SRef of GdsSRef
    | ARef of GdsARef

/// A named GDS structure (cell).
type GdsStructure = {
    Name: string
    Elements: GdsElement list
}

/// A GDS library containing structures.
type GdsLibrary = {
    Name: string
    /// Database units per user unit (e.g., 0.001 means 1 user unit = 1 um, 1 db unit = 1 nm).
    DbUnitsPerUserUnit: float
    /// Database units in meters (e.g., 1e-9 for nanometers).
    DbUnitsInMeters: float
    Structures: GdsStructure list
}
