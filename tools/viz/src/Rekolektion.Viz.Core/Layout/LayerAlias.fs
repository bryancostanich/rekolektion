module Rekolektion.Viz.Core.Layout.LayerAlias

open Rekolektion.Viz.Core.Gds.Types

/// Some SkyWater sky130_fd_pr_reram cells ship with non-standard,
/// low-numbered GDS layer IDs that don't appear in the SKY130 PDK
/// stream layer map. Magic translates them at gds-read time via
/// the cifinput rules in `sky130B.tech`; rekolektion-viz needs
/// the equivalent translation so those cells aren't blank.
///
/// Verified affected cells:
///   - sky130_fd_pr_reram/cells/reram_cell/sky130_fd_pr_reram__reram_cell.gds
///   - sky130_fd_pr_reram/cells/reram_inst/sky130_fd_pr_reram__reram_inst.gds
///
/// Mappings come from inspection of those files + the brief's
/// summary of what each ID is used for. Long-term we should parse
/// $PDK_ROOT/sky130B/libs.tech/magic/sky130B.tech's cifinput block
/// and build this table dynamically; the cifinput grammar is
/// non-trivial enough that a hardcoded stopgap is the right move
/// now.
let private aliases : Map<int * int, int * int> =
    [
        (6, 0),    (65, 20)        // diff.drawing
        (6, 251),  (65, 20)        // diff.pin (merged into drawing for viz visibility)
        (7, 0),    (66, 44)        // licon (poly contact)
        (8, 0),    (68, 20)        // met1.drawing
        (8, 251),  (68, 20)        // met1.pin (merged into drawing)
        (40, 0),   (201, 20)       // reram body — sky130 reram is normally 201/20
    ]
    |> Map.ofList

/// Apply a single layer/datatype translation. Non-aliased pairs
/// pass through unchanged.
let translate (layer: int) (dataType: int) : int * int =
    match Map.tryFind (layer, dataType) aliases with
    | Some (l, d) -> (l, d)
    | None -> (layer, dataType)

/// Walk every Element in a Library and rewrite its layer / datatype
/// fields through `translate`. Boundary, Path, and Text are the
/// three element variants that carry a layer; SRef and ARef pass
/// through. Returns a new Library — the input is not mutated.
let normalize (lib: Library) : Library =
    let mapElement (el: Element) : Element =
        match el with
        | Boundary b ->
            let (l, d) = translate b.Layer b.DataType
            if l = b.Layer && d = b.DataType then el
            else Boundary { b with Layer = l; DataType = d }
        | Path p ->
            let (l, d) = translate p.Layer p.DataType
            if l = p.Layer && d = p.DataType then el
            else Path { p with Layer = l; DataType = d }
        | Text t ->
            let (l, d) = translate t.Layer t.TextType
            if l = t.Layer && d = t.TextType then el
            else Text { t with Layer = l; TextType = d }
        | SRef _ | ARef _ -> el
    let mapStructure (s: Structure) : Structure =
        { s with Elements = s.Elements |> List.map mapElement }
    { lib with Structures = lib.Structures |> List.map mapStructure }
