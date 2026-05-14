module Rekolektion.Viz.Core.Layout.Hierarchy

open Rekolektion.Viz.Core.Rkt.Types

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

let private childrenOf (c: Cell) : string list =
    c.Elements
    |> List.choose (function
        | SRefEl s -> Some s.Cell
        | ARefEl a -> Some a.Cell
        | _ -> None)
    |> List.distinct

/// Transitive set of cell names reachable from `rootName` via SRef
/// + ARef edges (rootName itself is always included). Used by the
/// "Isolate block" feature so a single block click can hide every
/// polygon outside that block's subtree.
let closure (doc: Document) (rootName: string) : Set<string> =
    let byName =
        doc.Cells |> List.map (fun c -> c.Name, c) |> Map.ofList
    let visited = System.Collections.Generic.HashSet<string>()
    visited.Add rootName |> ignore
    let queue = System.Collections.Generic.Queue<string>()
    queue.Enqueue rootName
    while queue.Count > 0 do
        let name = queue.Dequeue()
        match Map.tryFind name byName with
        | None -> ()
        | Some c ->
            for el in c.Elements do
                match el with
                | SRefEl s when visited.Add s.Cell ->
                    queue.Enqueue s.Cell
                | ARefEl a when visited.Add a.Cell ->
                    queue.Enqueue a.Cell
                | _ -> ()
    Set.ofSeq visited

/// Build a flat list of blocks for every cell in the document,
/// skipping `Other` blocks unless they reference children. An `Other`
/// with no children is almost certainly a leaf cell — bitcell, std
/// cell instance — and isn't useful in the block tree.
let detect (doc: Document) : Block list =
    doc.Cells
    |> List.choose (fun c ->
        let role = roleOfName c.Name
        let children = childrenOf c
        match role, children with
        | Other, [] -> None
        | _ -> Some { Name = c.Name; Role = role; Children = children })
