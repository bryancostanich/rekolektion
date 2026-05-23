module Rekolektion.Viz.Core.Sidecar.Types

type NetClass = Power | Ground | Signal | Clock

type PolygonRef = {
    Structure: string
    Layer    : int
    DataType : int
    Index    : int       // ordinal within structure's element list
    /// Top-cell SRef / ARef index this polygon descends from. `None`
    /// when the polygon is authored directly in the top cell.
    /// Disambiguates physical instances that share the same source
    /// (Structure, Index) — without it, a single PolyId collides
    /// across instances and net claims merge incorrectly.
    TopInstanceIndex: int option
}

type NetEntry = {
    Name    : string
    Class   : NetClass
    Polygons: PolygonRef list
    /// The polygons that directly contain a label for this net —
    /// the SEED polygons of LabelFlood, before any contact-flood
    /// extended the claim. Used by `Obstacles.buildNetIndex` to
    /// give DIRECT-label claims priority over flooded claims, so
    /// a polygon labeled drn_R doesn't get reclassified as
    /// mag_drain_4 just because mag_drain_4's flood reached it
    /// via contacts. Empty list when sidecar source predates the
    /// field (legacy compat).
    SeedPolygons: PolygonRef list
}

type Sidecar = {
    Version: int        // = 1
    Macro  : string
    Nets   : Map<string, NetEntry>
}
