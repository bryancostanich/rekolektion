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
    /// Polygons flood-reachable from this net's labels that share
    /// the same SRef instance as a label-seeded poly. Used by
    /// `Obstacles.isOurs` to extend "ours" to the pin's full
    /// cross-layer stack (li1 pin + licon + diff under the label)
    /// while excluding polys reached via shared rails into other
    /// SRefs. Empty list when sidecar source predates the field.
    SeedPolygons: PolygonRef list
    /// Polygons whose interior strictly contains a label for this
    /// net — the RAW seeds before any flood expansion. Used by
    /// `Obstacles.isOurs` to give direct-label authority priority
    /// over flood claims: a poly directly labeled `mag_drain_3` is
    /// foreign to `drn_R` even if drn_R's contact-flood reached
    /// it (the labels say they're different nets, label intent
    /// wins). Empty when sidecar source predates the field.
    DirectLabelPolys: PolygonRef list
}

type Sidecar = {
    Version: int        // = 1
    Macro  : string
    Nets   : Map<string, NetEntry>
}
