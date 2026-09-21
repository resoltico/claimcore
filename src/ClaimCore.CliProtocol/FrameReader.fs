namespace ClaimCore.Cli

open System
open System.IO

[<NoEquality; NoComparison; RequireQualifiedAccess>]
type InputFrame =
    | EndOfInput
    | Bytes of byte array
    | Failure of ProtocolFailure

module FrameReader =
    let readDocument maximumBytes (stream: Stream) =
        let buffer = Array.zeroCreate<byte> (maximumBytes + 1)
        let mutable total = 0
        let mutable read = 1

        while read > 0 && total < buffer.Length do
            read <- stream.Read(buffer, total, buffer.Length - total)
            total <- total + read

        if total > maximumBytes then
            InputFrame.Failure(
                ProtocolFailure.create
                    "INPUT_TOO_LARGE"
                    "The JSON input exceeds the configured byte limit."
                    ""
            )
        else
            InputFrame.Bytes(Array.truncate total buffer)

    let readLine maximumBytes (stream: Stream) =
        let buffer = Array.zeroCreate<byte> maximumBytes
        let mutable total = 0
        let mutable next = stream.ReadByte()
        let mutable oversized = false

        while next >= 0 && next <> int '\n' do
            if total < maximumBytes then
                buffer[total] <- byte next
                total <- total + 1
            else
                oversized <- true

            next <- stream.ReadByte()

        if total = 0 && next < 0 then
            InputFrame.EndOfInput
        elif oversized then
            InputFrame.Failure(
                ProtocolFailure.create
                    "FRAME_TOO_LARGE"
                    "The NDJSON frame exceeds the configured byte limit."
                    ""
            )
        elif total = 0 then
            InputFrame.Failure(
                ProtocolFailure.create
                    "BLANK_FRAME"
                    "NDJSON frames must contain one JSON object."
                    ""
            )
        elif buffer[total - 1] = byte '\r' then
            InputFrame.Failure(
                ProtocolFailure.create
                    "CRLF_FORBIDDEN"
                    "NDJSON uses LF as its only frame terminator."
                    ""
            )
        else
            InputFrame.Bytes(Array.truncate total buffer)
