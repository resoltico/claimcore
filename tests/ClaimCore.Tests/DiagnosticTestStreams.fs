module ClaimCore.Tests.DiagnosticTestStreams

open System
open System.IO
open System.Text
open System.Text.Json

let bytes (value: string) = Encoding.UTF8.GetBytes value

let parsed (stream: MemoryStream) =
    use document = JsonDocument.Parse(ReadOnlyMemory<byte>(stream.ToArray()))
    document.RootElement.Clone()

let diagnosticId (value: JsonElement) =
    value.GetProperty("diagnostic").GetProperty("id").GetString()

let text (stream: MemoryStream) =
    Encoding.UTF8.GetString(stream.ToArray())

// Write once, possibly leaving a prefix behind. A transport must never append a second frame.
type BrokenOutput(failFlush: bool) =
    inherit MemoryStream()
    let mutable writes = 0
    member _.Writes = writes

    override this.Write(buffer, offset, count) =
        writes <- writes + 1

        if failFlush then
            base.Write(buffer, offset, count)
        else
            base.Write(buffer, offset, min count 7)
            raise (IOException("PRIVATE-OUTPUT-MARKER"))

    override _.Flush() =
        if failFlush then
            raise (IOException("PRIVATE-FLUSH-MARKER"))

type EndReadFailure(source: byte array) =
    inherit MemoryStream(source)

    override this.ReadByte() =
        if this.Position = this.Length then
            raise (IOException("PRIVATE-INPUT-MARKER"))

        base.ReadByte()
