module Rekolektion.Viz.Render.Mesh.Picking

/// Encode a polygon ID into an RGB triplet for GPU color-buffer picking.
/// IDs must fit in 24 bits; 0xFFFFFF is reserved as "background".
let encodeId (id: int) : byte * byte * byte =
    if id < 0 || id >= 16777215 then
        raise (System.ArgumentException $"id {id} out of 24-bit range")
    byte ((id >>> 16) &&& 0xff),
    byte ((id >>>  8) &&& 0xff),
    byte ( id         &&& 0xff)

let decodeId (rgb: byte * byte * byte) : int =
    let r, g, b = rgb
    (int r <<< 16) ||| (int g <<< 8) ||| int b

let backgroundId : int = 16777215
