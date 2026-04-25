module Rekolektion.Viz.Core.Gds.Types

/// GDS coordinates are integer database units (DBU). Conversion to
/// micrometers happens at display time using Library.DbUnitsPerUserUnit.
type Point = { X: int64; Y: int64 }

type Boundary = {
    Layer: int
    DataType: int
    Points: Point list      // closed polygon, first = last
}

type Path = {
    Layer: int
    DataType: int
    Width: int               // DBU
    Points: Point list
}

type SRef = {
    StructureName: string
    Origin: Point
    Mag: float
    Angle: float             // degrees, CCW
    Reflected: bool          // reflect about X axis before rotation
}

type ARef = {
    StructureName: string
    Origin: Point
    Cols: int
    Rows: int
    ColPitch: Point          // vector from origin to next column anchor
    RowPitch: Point
    Mag: float
    Angle: float
    Reflected: bool
}

type TextLabel = {
    Layer: int
    TextType: int
    Origin: Point
    Text: string
}

type Element =
    | Boundary of Boundary
    | Path of Path
    | SRef of SRef
    | ARef of ARef
    | Text of TextLabel

type Structure = {
    Name: string
    Elements: Element list
}

type Library = {
    Name: string
    DbUnitsPerUserUnit: float    // user units per DB unit (e.g. 0.001 μm/DBU)
    DbUnitsInMeters: float       // meters per DB unit (e.g. 1e-9)
    Structures: Structure list
}
