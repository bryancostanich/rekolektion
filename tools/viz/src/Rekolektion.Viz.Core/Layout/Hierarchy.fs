module Rekolektion.Viz.Core.Layout.Hierarchy

open Rekolektion.Viz.Core.Gds.Types

type BlockRole =
    | Top
    | Array
    | Precharge
    | ColumnMux
    | SenseAmp
    | WriteDriver
    | WordlineDriver
    | Decoder
    | Control
    | Bitcell
    | Other

type Block = {
    Name      : string
    Role      : BlockRole
    Children  : string list
}

let private roleOfName (n: string) : BlockRole =
    let lower = n.ToLowerInvariant()
    if lower.Contains "sram_array"             then Array
    elif lower.Contains "precharge"            then Precharge
    elif lower.Contains "col_mux"              then ColumnMux
    elif lower.Contains "column_mux"           then ColumnMux
    elif lower.Contains "sense_amp"            then SenseAmp
    elif lower.Contains "write_driver"         then WriteDriver
    elif lower.Contains "wd_row"               then WriteDriver
    elif lower.Contains "wl_driver"            then WordlineDriver
    elif lower.Contains "decoder"              then Decoder
    elif lower.Contains "ctrl"                 then Control
    elif lower.Contains "bitcell"              then Bitcell
    elif lower.Contains "macro" && lower.Contains "top" then Top
    elif lower.EndsWith "_top"                 then Top
    else Other

let private childrenOf (s: Structure) : string list =
    s.Elements
    |> List.choose (function
        | SRef sr -> Some sr.StructureName
        | ARef ar -> Some ar.StructureName
        | _ -> None)
    |> List.distinct

/// Build a flat list of blocks for every structure in the library,
/// skipping `Other` blocks unless they reference children. An `Other`
/// with no children is almost certainly a leaf cell — bitcell, std
/// cell instance — and isn't useful in the block tree.
let detect (lib: Library) : Block list =
    lib.Structures
    |> List.choose (fun s ->
        let role = roleOfName s.Name
        let children = childrenOf s
        match role, children with
        | Other, [] -> None
        | _ -> Some { Name = s.Name; Role = role; Children = children })
