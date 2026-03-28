/// GDS II binary format reader.
/// Reference: https://boolean.klamath.net/devref/gadsr7.html
module Viz.Gds.Reader

open System
open System.IO
open System.Text
open Viz.Gds.Units
open Viz.Gds.Types

// GDS II record types
[<RequireQualifiedAccess>]
module RecordType =
    let HEADER     = 0x0002us
    let BGNLIB     = 0x0102us
    let LIBNAME    = 0x0206us
    let UNITS      = 0x0305us
    let ENDLIB     = 0x0400us
    let BGNSTR     = 0x0502us
    let STRNAME    = 0x0606us
    let ENDSTR     = 0x0700us
    let BOUNDARY   = 0x0800us
    let PATH       = 0x0900us
    let SREF       = 0x0A00us
    let AREF       = 0x0B00us
    let TEXT       = 0x0C00us
    let LAYER      = 0x0D02us
    let DATATYPE   = 0x0E02us
    let WIDTH      = 0x0F03us
    let XY         = 0x1003us
    let ENDEL      = 0x1100us
    let SNAME      = 0x1206us
    let COLROW     = 0x1302us
    let NODE       = 0x1500us
    let TEXTTYPE   = 0x1602us
    let STRANS     = 0x1A01us
    let MAG        = 0x1B05us
    let ANGLE      = 0x1C05us
    let PATHTYPE   = 0x2102us
    let BOX        = 0x2D00us
    let BOXTYPE    = 0x2E02us

// Data type tags (lower byte of record type word)
[<RequireQualifiedAccess>]
module DataTag =
    let NoData    = 0x00uy
    let BitArray  = 0x01uy
    let Int16     = 0x02uy
    let Int32     = 0x03uy
    let Real4     = 0x04uy
    let Real8     = 0x05uy
    let AsciiStr  = 0x06uy

/// A raw GDS record: type word + payload bytes.
type RawRecord = {
    RecordType: uint16
    Data: byte[]
}

/// Read a big-endian int16 from a byte array at offset.
let private readInt16BE (data: byte[]) (offset: int) : int16 =
    int16 (int data.[offset] <<< 8 ||| int data.[offset + 1])

/// Read a big-endian int32 from a byte array at offset.
let private readInt32BE (data: byte[]) (offset: int) : int =
    (int data.[offset] <<< 24) |||
    (int data.[offset + 1] <<< 16) |||
    (int data.[offset + 2] <<< 8) |||
    int data.[offset + 3]

/// Decode a GDS II 8-byte real (excess-64 floating point).
let private readReal8 (data: byte[]) (offset: int) : float =
    let sign = if data.[offset] &&& 0x80uy <> 0uy then -1.0 else 1.0
    let exponent = int (data.[offset] &&& 0x7Fuy) - 64
    let mutable mantissa = 0.0
    for i in 1 .. 7 do
        mantissa <- mantissa * 256.0 + float data.[offset + i]
    sign * mantissa * Math.Pow(16.0, float exponent - 14.0)

/// Read all records from a GDS binary stream.
let private readRecords (stream: Stream) : RawRecord list =
    let reader = new BinaryReader(stream)
    let records = System.Collections.Generic.List<RawRecord>()
    while stream.Position < stream.Length do
        let lenBytes = reader.ReadBytes(2)
        if lenBytes.Length < 2 then () // EOF
        else
            let len = int lenBytes.[0] <<< 8 ||| int lenBytes.[1]
            let typeBytes = reader.ReadBytes(2)
            let recType = uint16 (int typeBytes.[0] <<< 8 ||| int typeBytes.[1])
            let dataLen = len - 4
            let data = if dataLen > 0 then reader.ReadBytes(dataLen) else [||]
            records.Add({ RecordType = recType; Data = data })
    records |> Seq.toList

/// Extract ASCII string from record data (strip trailing nulls/padding).
let private readString (data: byte[]) : string =
    let s = Encoding.ASCII.GetString(data)
    s.TrimEnd('\000', ' ')

/// Parse XY record data into a list of GdsPoints.
let private parseXY (data: byte[]) (dbUnitNm: float) : GdsPoint list =
    let count = data.Length / 8
    [ for i in 0 .. count - 1 do
        let x = readInt32BE data (i * 8)
        let y = readInt32BE data (i * 8 + 4)
        { X = LanguagePrimitives.Int32WithMeasure<nm>(int (float x * dbUnitNm))
          Y = LanguagePrimitives.Int32WithMeasure<nm>(int (float y * dbUnitNm)) } ]

/// Parse a GDS II binary file into a GdsLibrary.
let readGds (path: string) : GdsLibrary =
    use stream = File.OpenRead(path)
    let records = readRecords stream

    let mutable libName = ""
    let mutable dbUnitsPerUser = 0.001  // default: 1 user unit = 1 um
    let mutable dbUnitsMeters = 1e-9    // default: 1 db unit = 1 nm
    let mutable dbUnitNm = 1.0          // conversion factor: db units to nm

    let structures = System.Collections.Generic.List<GdsStructure>()
    let mutable currentStructName = ""
    let mutable currentElements = System.Collections.Generic.List<GdsElement>()

    // Element parsing state
    let mutable elemKind = ""   // "BOUNDARY", "PATH", "SREF", "AREF", "TEXT", "BOX", "NODE"
    let mutable elemLayer = 0
    let mutable elemDatatype = 0
    let mutable elemWidth = 0
    let mutable elemPoints : GdsPoint list = []
    let mutable elemSName = ""
    let mutable elemStrans = 0us
    let mutable elemMag = 1.0
    let mutable elemAngle = 0.0
    let mutable elemColRow = (1, 1)

    let resetElem () =
        elemKind <- ""
        elemLayer <- 0
        elemDatatype <- 0
        elemWidth <- 0
        elemPoints <- []
        elemSName <- ""
        elemStrans <- 0us
        elemMag <- 1.0
        elemAngle <- 0.0
        elemColRow <- (1, 1)

    for record in records do
        let rt = record.RecordType
        let d = record.Data

        if rt = RecordType.LIBNAME then
            libName <- readString d

        elif rt = RecordType.UNITS then
            dbUnitsPerUser <- readReal8 d 0
            dbUnitsMeters <- readReal8 d 8
            // Compute: how many nm is one database unit?
            dbUnitNm <- dbUnitsMeters * 1e9

        elif rt = RecordType.BGNSTR then
            currentStructName <- ""
            currentElements.Clear()

        elif rt = RecordType.STRNAME then
            currentStructName <- readString d

        elif rt = RecordType.ENDSTR then
            structures.Add({
                Name = currentStructName
                Elements = currentElements |> Seq.toList
            })

        elif rt = RecordType.BOUNDARY then
            resetElem ()
            elemKind <- "BOUNDARY"

        elif rt = RecordType.PATH then
            resetElem ()
            elemKind <- "PATH"

        elif rt = RecordType.SREF then
            resetElem ()
            elemKind <- "SREF"

        elif rt = RecordType.AREF then
            resetElem ()
            elemKind <- "AREF"

        elif rt = RecordType.TEXT then
            resetElem ()
            elemKind <- "TEXT"

        elif rt = RecordType.BOX then
            resetElem ()
            elemKind <- "BOX"

        elif rt = RecordType.NODE then
            resetElem ()
            elemKind <- "NODE"

        elif rt = RecordType.LAYER then
            elemLayer <- int (readInt16BE d 0)

        elif rt = RecordType.DATATYPE || rt = RecordType.TEXTTYPE || rt = RecordType.BOXTYPE then
            elemDatatype <- int (readInt16BE d 0)

        elif rt = RecordType.WIDTH then
            elemWidth <- readInt32BE d 0

        elif rt = RecordType.SNAME then
            elemSName <- readString d

        elif rt = RecordType.STRANS then
            elemStrans <- uint16 (int d.[0] <<< 8 ||| int d.[1])

        elif rt = RecordType.MAG then
            elemMag <- readReal8 d 0

        elif rt = RecordType.ANGLE then
            elemAngle <- readReal8 d 0

        elif rt = RecordType.COLROW then
            let cols = int (readInt16BE d 0)
            let rows = int (readInt16BE d 2)
            elemColRow <- (cols, rows)

        elif rt = RecordType.XY then
            elemPoints <- parseXY d dbUnitNm

        elif rt = RecordType.ENDEL then
            match elemKind with
            | "BOUNDARY" ->
                currentElements.Add(
                    Boundary {
                        Layer = elemLayer
                        Datatype = elemDatatype
                        Points = elemPoints
                    })
            | "PATH" ->
                currentElements.Add(
                    Path {
                        Layer = elemLayer
                        Datatype = elemDatatype
                        Width = LanguagePrimitives.Int32WithMeasure<nm>(int (float elemWidth * dbUnitNm))
                        Points = elemPoints
                    })
            | "SREF" ->
                let origin = if elemPoints.Length > 0 then elemPoints.[0] else { X = 0<nm>; Y = 0<nm> }
                let reflected = elemStrans &&& 0x8000us <> 0us
                currentElements.Add(
                    SRef {
                        StructureName = elemSName
                        Origin = origin
                        Angle = elemAngle
                        Reflected = reflected
                        Magnification = elemMag
                    })
            | "AREF" ->
                let origin = if elemPoints.Length > 0 then elemPoints.[0] else { X = 0<nm>; Y = 0<nm> }
                let colVec = if elemPoints.Length > 1 then elemPoints.[1] else { X = 0<nm>; Y = 0<nm> }
                let rowVec = if elemPoints.Length > 2 then elemPoints.[2] else { X = 0<nm>; Y = 0<nm> }
                let reflected = elemStrans &&& 0x8000us <> 0us
                let cols, rows = elemColRow
                currentElements.Add(
                    ARef {
                        StructureName = elemSName
                        Origin = origin
                        Columns = cols
                        Rows = rows
                        ColumnVector = colVec
                        RowVector = rowVec
                        Angle = elemAngle
                        Reflected = reflected
                        Magnification = elemMag
                    })
            | "BOX" ->
                // Treat BOX as BOUNDARY
                currentElements.Add(
                    Boundary {
                        Layer = elemLayer
                        Datatype = elemDatatype
                        Points = elemPoints
                    })
            | _ -> () // TEXT, NODE — skip
            resetElem ()
        else
            () // Unknown record — skip

    {
        Name = libName
        DbUnitsPerUserUnit = dbUnitsPerUser
        DbUnitsInMeters = dbUnitsMeters
        Structures = structures |> Seq.toList
    }
