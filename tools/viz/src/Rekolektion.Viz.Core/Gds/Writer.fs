/// GDS II binary format writer.
/// Reference: https://boolean.klamath.net/devref/gadsr7.html
///
/// Round-trip philosophy: the writer regenerates a full GDS file
/// from the in-memory `Library` rather than splicing records from
/// the source binary. The resulting bytes won't match the source
/// byte-for-byte (different writers order optional records
/// differently and we don't preserve the file's original ordering
/// once parsed), but the LOGICAL content — every structure,
/// element, transform, layer — round-trips cleanly. Sufficient
/// for the editor's "save and reopen" loop.
module Rekolektion.Viz.Core.Gds.Writer

open System
open System.IO
open System.Text
open Rekolektion.Viz.Core.Gds.Types

let private REC_HEADER     = 0x0002us
let private REC_BGNLIB     = 0x0102us
let private REC_LIBNAME    = 0x0206us
let private REC_UNITS      = 0x0305us
let private REC_ENDLIB     = 0x0400us
let private REC_BGNSTR     = 0x0502us
let private REC_STRNAME    = 0x0606us
let private REC_ENDSTR     = 0x0700us
let private REC_BOUNDARY   = 0x0800us
let private REC_PATH       = 0x0900us
let private REC_SREF       = 0x0A00us
let private REC_AREF       = 0x0B00us
let private REC_TEXT       = 0x0C00us
let private REC_LAYER      = 0x0D02us
let private REC_DATATYPE   = 0x0E02us
let private REC_WIDTH      = 0x0F03us
let private REC_XY         = 0x1003us
let private REC_ENDEL      = 0x1100us
let private REC_SNAME      = 0x1206us
let private REC_COLROW     = 0x1302us
let private REC_TEXTTYPE   = 0x1602us
let private REC_STRING     = 0x1906us
let private REC_STRANS     = 0x1A01us
let private REC_MAG        = 0x1B05us
let private REC_ANGLE      = 0x1C05us

let private writeUInt16BE (w: BinaryWriter) (v: uint16) =
    w.Write(byte (v >>> 8))
    w.Write(byte (v &&& 0xFFus))

let private writeInt16BE (w: BinaryWriter) (v: int16) =
    let u = uint16 v
    writeUInt16BE w u

let private writeInt32BE (w: BinaryWriter) (v: int) =
    w.Write(byte ((v >>> 24) &&& 0xFF))
    w.Write(byte ((v >>> 16) &&& 0xFF))
    w.Write(byte ((v >>> 8)  &&& 0xFF))
    w.Write(byte (v          &&& 0xFF))

/// Encode an IEEE 754 double into the GDS II 8-byte excess-64
/// hex-mantissa float used by UNITS / MAG / ANGLE records.
/// Inverse of Gds.Reader.readReal8.
let private writeReal8 (w: BinaryWriter) (value: float) =
    if value = 0.0 then
        for _ in 1 .. 8 do w.Write(0uy)
    else
        let sign = if value < 0.0 then 0x80uy else 0x00uy
        let absV = abs value
        // Find the smallest exponent e such that |v| / 16^(e-14) < 1.
        // Mantissa is then |v| * 16^(14 - e), stored as 7 base-256
        // bytes (i.e. a 56-bit fraction).
        let mutable e = 0
        let mutable m = absV
        while m >= 1.0 do
            m <- m / 16.0
            e <- e + 1
        while m > 0.0 && m < 1.0 / 16.0 do
            m <- m * 16.0
            e <- e - 1
        // Bias the exponent by 64 (the "excess-64" part of the
        // format) and pack the sign bit into the high bit of the
        // exponent byte.
        let exponentByte = byte (e + 64) ||| sign
        w.Write(exponentByte)
        // Mantissa: 7 bytes, MSB-first base-256 digits of the
        // fractional value m (in [1/16, 1)). Each iteration
        // peels off the next base-256 digit.
        let mutable mant = m
        for _ in 1 .. 7 do
            mant <- mant * 256.0
            let digit = int mant
            w.Write(byte digit)
            mant <- mant - float digit

let private writeRecord (w: BinaryWriter) (recType: uint16) (payload: byte[]) =
    let len = 4 + payload.Length
    if len > 0xFFFF then
        failwithf "GDS record too large (%d bytes) for type %04X" len recType
    writeUInt16BE w (uint16 len)
    writeUInt16BE w recType
    if payload.Length > 0 then w.Write payload

let private writeEmptyRecord (w: BinaryWriter) (recType: uint16) =
    writeRecord w recType [||]

let private padded (s: string) : byte[] =
    let raw = Encoding.ASCII.GetBytes s
    // GDS ASCII records must be even-length; pad with a trailing
    // null when the string has an odd byte count.
    if raw.Length % 2 = 0 then raw
    else Array.append raw [| 0uy |]

let private bytesInt16BE (v: int16) : byte[] =
    let u = uint16 v
    [| byte (u >>> 8); byte (u &&& 0xFFus) |]

let private bytesInt32BE (v: int) : byte[] =
    [| byte ((v >>> 24) &&& 0xFF)
       byte ((v >>> 16) &&& 0xFF)
       byte ((v >>> 8)  &&& 0xFF)
       byte (v          &&& 0xFF) |]

let private bytesReal8 (value: float) : byte[] =
    use ms = new MemoryStream()
    use w = new BinaryWriter(ms)
    writeReal8 w value
    ms.ToArray()

let private writeShortRecord (w: BinaryWriter) (recType: uint16) (data: byte[]) =
    writeRecord w recType data

let private writeXY (w: BinaryWriter) (points: Point list) =
    let buf = System.Collections.Generic.List<byte>(points.Length * 8)
    for p in points do
        buf.AddRange(bytesInt32BE (int p.X))
        buf.AddRange(bytesInt32BE (int p.Y))
    writeRecord w REC_XY (buf.ToArray())

let private writeXYArray (w: BinaryWriter) (points: Point[]) =
    let buf = System.Collections.Generic.List<byte>(points.Length * 8)
    for p in points do
        buf.AddRange(bytesInt32BE (int p.X))
        buf.AddRange(bytesInt32BE (int p.Y))
    writeRecord w REC_XY (buf.ToArray())

let private writeStrans (w: BinaryWriter) (reflected: bool) (mag: float) (angle: float) =
    // STRANS bit15 = reflect-about-X. All other transformation
    // bits (abs-mag / abs-angle) stay 0 for the canonical
    // round-trip; the reader only checks bit 15.
    let strans : uint16 = if reflected then 0x8000us else 0x0000us
    writeShortRecord w REC_STRANS
        [| byte (strans >>> 8); byte (strans &&& 0xFFus) |]
    // MAG and ANGLE are optional in the spec — emit only when
    // they differ from the identity defaults so unrelated SRefs
    // stay byte-light.
    if mag <> 1.0 then
        writeShortRecord w REC_MAG (bytesReal8 mag)
    if angle <> 0.0 then
        writeShortRecord w REC_ANGLE (bytesReal8 angle)

let private writeBoundary (w: BinaryWriter) (b: Boundary) =
    writeEmptyRecord w REC_BOUNDARY
    writeShortRecord w REC_LAYER (bytesInt16BE (int16 b.Layer))
    writeShortRecord w REC_DATATYPE (bytesInt16BE (int16 b.DataType))
    writeXY w b.Points
    writeEmptyRecord w REC_ENDEL

let private writePath (w: BinaryWriter) (p: Path) =
    writeEmptyRecord w REC_PATH
    writeShortRecord w REC_LAYER (bytesInt16BE (int16 p.Layer))
    writeShortRecord w REC_DATATYPE (bytesInt16BE (int16 p.DataType))
    if p.Width <> 0 then
        writeShortRecord w REC_WIDTH (bytesInt32BE p.Width)
    writeXY w p.Points
    writeEmptyRecord w REC_ENDEL

let private writeSref (w: BinaryWriter) (s: SRef) =
    writeEmptyRecord w REC_SREF
    writeShortRecord w REC_SNAME (padded s.StructureName)
    writeStrans w s.Reflected s.Mag s.Angle
    writeXYArray w [| s.Origin |]
    writeEmptyRecord w REC_ENDEL

let private writeAref (w: BinaryWriter) (a: ARef) =
    writeEmptyRecord w REC_AREF
    writeShortRecord w REC_SNAME (padded a.StructureName)
    writeStrans w a.Reflected a.Mag a.Angle
    // COLROW: cols, rows — both int16 in record order.
    let colRowBytes =
        Array.append (bytesInt16BE (int16 a.Cols)) (bytesInt16BE (int16 a.Rows))
    writeShortRecord w REC_COLROW colRowBytes
    // ARef XY is exactly three points: origin, col-end, row-end.
    writeXYArray w [| a.Origin; a.ColPitch; a.RowPitch |]
    writeEmptyRecord w REC_ENDEL

let private writeText (w: BinaryWriter) (t: TextLabel) =
    writeEmptyRecord w REC_TEXT
    writeShortRecord w REC_LAYER (bytesInt16BE (int16 t.Layer))
    writeShortRecord w REC_TEXTTYPE (bytesInt16BE (int16 t.TextType))
    writeXYArray w [| t.Origin |]
    writeShortRecord w REC_STRING (padded t.Text)
    writeEmptyRecord w REC_ENDEL

let private writeElement (w: BinaryWriter) (el: Element) =
    match el with
    | Boundary b -> writeBoundary w b
    | Path p -> writePath w p
    | SRef sr -> writeSref w sr
    | ARef ar -> writeAref w ar
    | Text t -> writeText w t

let private writeStructure (w: BinaryWriter) (now: DateTime) (s: Structure) =
    // BGNSTR has a 12-int16 timestamp payload: creation date
    // followed by last-modify date, each as (year, month, day,
    // hour, minute, second). Use `now` for both so a re-saved
    // file has a single coherent timestamp set.
    let yr = int16 now.Year
    let mo = int16 now.Month
    let dy = int16 now.Day
    let hr = int16 now.Hour
    let mn = int16 now.Minute
    let sc = int16 now.Second
    let ts = [| yr; mo; dy; hr; mn; sc; yr; mo; dy; hr; mn; sc |]
    let payload =
        ts
        |> Array.collect bytesInt16BE
    writeShortRecord w REC_BGNSTR payload
    writeShortRecord w REC_STRNAME (padded s.Name)
    for el in s.Elements do
        writeElement w el
    writeEmptyRecord w REC_ENDSTR

/// Serialise `lib` to a GDSII binary at `targetPath`. Atomic via
/// a sibling `.tmp` + rename so a crash mid-write doesn't leave a
/// half-written file in place of the user's previous save.
let writeGds (targetPath: string) (lib: Library) : unit =
    let tmp = targetPath + ".tmp"
    do
        use stream = File.Create(tmp)
        use w = new BinaryWriter(stream)

        // HEADER: version. Use v600 like most modern writers — the
        // reader doesn't check.
        writeShortRecord w REC_HEADER (bytesInt16BE 600s)

        // BGNLIB: 12 int16 timestamps (lib-mod, lib-access).
        let now = DateTime.Now
        let yr = int16 now.Year
        let mo = int16 now.Month
        let dy = int16 now.Day
        let hr = int16 now.Hour
        let mn = int16 now.Minute
        let sc = int16 now.Second
        let ts = [| yr; mo; dy; hr; mn; sc; yr; mo; dy; hr; mn; sc |]
        writeShortRecord w REC_BGNLIB (ts |> Array.collect bytesInt16BE)

        let libName =
            if String.IsNullOrEmpty lib.Name then "viz" else lib.Name
        writeShortRecord w REC_LIBNAME (padded libName)

        // UNITS: two real8s — user units per DB unit, then meters
        // per DB unit. Reader reads them in this order.
        let unitsPayload =
            Array.append
                (bytesReal8 lib.UserUnitsPerDbUnit)
                (bytesReal8 lib.DbUnitsInMeters)
        writeShortRecord w REC_UNITS unitsPayload

        for s in lib.Structures do
            writeStructure w now s

        writeEmptyRecord w REC_ENDLIB

    if File.Exists targetPath then File.Delete targetPath
    File.Move(tmp, targetPath)
