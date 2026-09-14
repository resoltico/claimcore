namespace ClaimCore.Protocol

open System
open System.Buffers
open System.Text.Json

/// Bound committed bytes exactly and temporary writer reservation to a fixed multiple of the budget.
/// Utf8JsonWriter can reserve its worst-case escaped-string space before advancing actual bytes.
type private BoundedJsonBuffer(maximum: int) =
    let buffer = ArrayBufferWriter<byte>()
    let reservationLimit = max 4096L (6L * int64 maximum + 4096L)

    let requireReservation count =
        if int64 buffer.WrittenCount + int64 count > reservationLimit then
            JsonInput.reject "LIMIT_EXCEEDED"

    member _.Bytes = buffer.WrittenSpan.ToArray()

    interface IBufferWriter<byte> with
        member _.Advance(count) =
            if int64 buffer.WrittenCount + int64 count > int64 maximum then
                JsonInput.reject "LIMIT_EXCEEDED"

            buffer.Advance(count)

        member _.GetMemory(sizeHint) =
            requireReservation sizeHint
            buffer.GetMemory(sizeHint)

        member _.GetSpan(sizeHint) =
            requireReservation sizeHint
            buffer.GetSpan(sizeHint)

/// Writers are private; a public codec checks both the authored value and the final JSON.
module internal JsonWrite =
    let text (writer: Utf8JsonWriter) (value: string) =
        JsonInput.unicode value |> writer.WriteStringValue

    let integer (writer: Utf8JsonWriter) (value: int64) = writer.WriteNumberValue(value)
    let boolean (writer: Utf8JsonWriter) (value: bool) = writer.WriteBooleanValue(value)
    let nullValue (writer: Utf8JsonWriter) () = writer.WriteNullValue()

    let literal expected write writer value =
        if value <> expected then
            JsonInput.reject "INVALID_VALUE"

        write writer value

    let property name write (writer: Utf8JsonWriter) value =
        writer.WritePropertyName(name: string)
        write writer value

    let optional name write writer value =
        value |> Option.iter (property name write writer)

    let nullable write writer value =
        match value with
        | None -> nullValue writer ()
        | Some item -> write writer item

    let array write (writer: Utf8JsonWriter) values =
        writer.WriteStartArray()
        values |> List.iter (write writer)
        writer.WriteEndArray()

    let dictionary write (writer: Utf8JsonWriter) values =
        writer.WriteStartObject()

        for KeyValue(name, value) in values do
            JsonInput.unicode name |> writer.WritePropertyName
            write writer value

        writer.WriteEndObject()

    let encode maximumBytes read write value =
        if maximumBytes < 1 then
            invalidArg (nameof maximumBytes) "A positive byte budget is required."

        JsonInput.protect (fun () ->
            let buffer = BoundedJsonBuffer(maximumBytes)
            use writer = new Utf8JsonWriter(buffer :> IBufferWriter<byte>)
            write writer value
            writer.Flush()

            let bytes = buffer.Bytes

            match JsonInput.decode maximumBytes read bytes with
            | Error problem -> JsonInput.reject problem.Code
            | Ok _ -> bytes)
