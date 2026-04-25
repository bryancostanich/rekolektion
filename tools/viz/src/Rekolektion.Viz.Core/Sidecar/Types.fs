module Rekolektion.Viz.Core.Sidecar.Types

type NetClass = Power | Ground | Signal | Clock

type PolygonRef = {
    Structure: string
    Layer    : int
    DataType : int
    Index    : int       // ordinal within structure's element list
}

type NetEntry = {
    Name    : string
    Class   : NetClass
    Polygons: PolygonRef list
}

type Sidecar = {
    Version: int        // = 1
    Macro  : string
    Nets   : Map<string, NetEntry>
}
