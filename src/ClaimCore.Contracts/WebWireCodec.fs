namespace ClaimCore.Contracts

open System.Buffers
open System.Text.Json
open ClaimCore.Application

/// Pure deterministic HTTP-v2 JSON codec. Web supplies status/content type and never constructs
/// response objects independently from these bytes.
[<RequireQualifiedAccess>]
module WebWireCodec =
    let private encode write =
        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = false))
        write writer
        writer.Flush()
        buffer.WrittenSpan.ToArray()

    let private result (endpoint: string) (writeOutcome: Utf8JsonWriter -> unit) =
        encode (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("endpoint", endpoint)
            writer.WritePropertyName("outcome")
            writeOutcome writer
            writer.WriteEndObject())

    let hostFailure (reason: WebHostFailure) =
        encode (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("kind", "HOST_FAILURE")
            writer.WriteString("code", WebHostFailures.code reason)
            writer.WriteNumber("status", WebHostFailures.status reason)
            writer.WriteString("message", WebHostFailures.render reason)
            writer.WritePropertyName("diagnostic")
            writer.WriteStartObject()
            writer.WriteString("id", WebHostFailures.token reason)
            writer.WritePropertyName("parameters")
            writer.WriteStartObject()
            writer.WriteEndObject()
            writer.WriteEndObject()

            match WebHostFailures.phase reason with
            | Some value -> writer.WriteString("executionPhase", value)
            | None -> writer.WriteNull("executionPhase")

            writer.WriteEndObject())

    let liveness =
        encode (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("status", "ok")
            writer.WriteEndObject())

    let session endpoint (authenticated: bool) (antiforgeryToken: string option) =
        result endpoint (fun writer -> WebWireQueries.session writer authenticated antiforgeryToken)

    let description value =
        result "definition" (fun writer -> WebWireQueries.description writer value)

    let get value =
        result "case.get" (fun writer -> WebWireQueries.currentCase writer value)

    let list value =
        result "case.list" (fun writer -> WebWireQueries.casePage writer value)

    let history value =
        result "case.history" (fun writer -> WebWireQueries.history writer value)

    let observe value =
        result "operation.observe" (fun writer -> WebWireQueries.operation writer value)

    let prepare value =
        result "command.prepare" (fun writer -> WebWireMutations.prepare writer value)

    let resolve endpoint value =
        result endpoint (fun writer -> WebWireMutations.resolve writer value)

    let recoveryList value =
        result "recovery.list" (fun writer -> WebWireQueries.recoveryPage writer value)

    let recoveryInspect value =
        result "recovery.inspect" (fun writer -> WebWireQueries.recoveryDetails writer value)

    let recoveryDismiss value =
        result "recovery.dismiss" (fun writer -> WebWireMutations.dismiss writer value)

    let recoveryExport value =
        result "recovery.export" (fun writer -> WebWireQueries.recoveryExport writer value)

    let importPreview endpoint value =
        result endpoint (fun writer -> WebWireQueries.importPreview writer value)

    let importRetain endpoint value =
        result endpoint (fun writer -> WebWireMutations.importRetain writer value)
