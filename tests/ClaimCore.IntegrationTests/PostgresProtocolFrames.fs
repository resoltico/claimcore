module internal ClaimCore.IntegrationTests.PostgresProtocolFrames

open System
open System.Buffers.Binary
open System.Text
open System.Threading

/// Normal typed protocol messages only; the relay arms after startup is complete.
/// Keep one bounded frame while TCP reads split or combine PostgreSQL messages.
type Parser() =
    let header = Array.zeroCreate<byte> 5
    let mutable headerCount = 0
    let mutable frame = Array.empty<byte>
    let mutable frameCount = 0

    member _.Feed(bytes: byte array, count: int) =
        let frames = ResizeArray<byte array>()
        let mutable position = 0

        while position < count do
            if headerCount < 5 then
                let take = min (5 - headerCount) (count - position)
                Array.Copy(bytes, position, header, headerCount, take)
                position <- position + take
                headerCount <- headerCount + take

                if headerCount = 5 then
                    let length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1, 4))

                    if length < 4 || length > 2 * 1024 * 1024 then
                        invalidOp "Synthetic protocol frame exceeds its allowance."

                    frame <- Array.zeroCreate (length + 1)
                    Array.Copy(header, frame, 5)
                    frameCount <- 5

            if headerCount = 5 then
                let take = min (frame.Length - frameCount) (count - position)
                Array.Copy(bytes, position, frame, frameCount, take)
                position <- position + take
                frameCount <- frameCount + take

                if frameCount = frame.Length then
                    frames.Add(frame)
                    frame <- Array.empty
                    frameCount <- 0
                    headerCount <- 0

        frames.ToArray()

let private textAt (frame: byte array) offset =
    let finish = Array.IndexOf(frame, 0uy, offset)

    if finish < offset then
        invalidOp "Synthetic protocol text is not terminated."

    Encoding.UTF8.GetString(frame, offset, finish - offset)

let query (frame: byte array) =
    match char frame[0] with
    | 'Q' -> Some(textAt frame 5)
    | 'P' ->
        let nameEnd = Array.IndexOf(frame, 0uy, 5)

        if nameEnd < 5 then
            invalidOp "Synthetic parse statement is not terminated."

        Some(textAt frame (nameEnd + 1))
    | _ -> None

let commandTag (frame: byte array) = textAt frame 5


/// Correlate one selected SQL message with its ordered parse or command reply.
type ReplySelection(statement: string) =
    let mutable requests = 0
    let mutable responses = 0
    let mutable selectedParse = 0
    let mutable selectedTag = ""
    let mutable matched = false

    member _.Observe(frame: byte array) =
        if char frame[0] = 'P' then
            requests <- requests + 1

        match query frame with
        | Some sql when not matched && sql.Contains(statement, StringComparison.Ordinal) ->
            matched <- true

            if char frame[0] = 'P' then
                Volatile.Write(&selectedParse, requests)
            else
                Volatile.Write(&selectedTag, (sql.TrimStart().Split(' ').[0]).TrimEnd(';'))
        | _ -> ()

    member _.Matches(frame: byte array) =
        let kind = char frame[0]

        if kind = '1' then
            responses <- responses + 1

        (kind = '1' && responses = Volatile.Read(&selectedParse))
        || (kind = 'C'
            && Volatile.Read(&selectedTag) <> ""
            && commandTag(frame).Split(' ')[0] = Volatile.Read(&selectedTag))
