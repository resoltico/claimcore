namespace ClaimCore.Cli

open System.IO

[<RequireQualifiedAccess>]
type InputFrame =
    | EndOfInput
    | Bytes of byte array
    | TooLarge

module FrameReader =
    [<Literal>]
    let MaximumBytes = ClaimCore.Wire.Limits.MaximumInvocationBytes

    let readDocument (stream: Stream) =
        let buffer = Array.zeroCreate<byte>(MaximumBytes + 1)
        let mutable total = 0
        let mutable read = 1

        while read > 0 && total < buffer.Length do
            read <- stream.Read(buffer, total, buffer.Length - total)
            total <- total + read

        if total > MaximumBytes then
            InputFrame.TooLarge
        else
            InputFrame.Bytes(Array.truncate total buffer)

    let readLine (stream: Stream) =
        let buffer = Array.zeroCreate<byte> MaximumBytes
        let mutable total = 0
        let mutable next = stream.ReadByte()
        let mutable oversized = false

        while next >= 0 && next <> int '\n' && not oversized do
            if total < buffer.Length then
                buffer[total] <- byte next
                total <- total + 1
            else
                oversized <- true

            if not oversized then
                next <- stream.ReadByte()

        if total = 0 && next < 0 then InputFrame.EndOfInput
        elif oversized then InputFrame.TooLarge
        else InputFrame.Bytes(Array.truncate total buffer)
