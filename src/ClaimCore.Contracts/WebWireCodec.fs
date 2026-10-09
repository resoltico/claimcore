namespace ClaimCore.Contracts

open System.Buffers
open System.Text.Json
open ClaimCore.Application

/// Pure deterministic HTTP-v3 JSON codec. Web supplies status/content type and never constructs
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
        result endpoint (fun writer ->
            WebWireDiscovery.session writer authenticated antiforgeryToken)

    let description value =
        result "definition" (fun writer -> WebWireDiscovery.description writer value)

    let definition value =
        result "definition" (fun writer -> WebWireDiscovery.definition writer value)

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

    let submit value =
        result "command.execute" (fun writer -> WebWireSubmission.write writer value)

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

    let management endpoint value =
        result endpoint (fun writer -> WebWireManagement.write writer value)

    let signerApproval value =
        result "authority.approveCopySigner" (fun writer ->
            WebWireSignerApproval.write writer value)

    let copyDeletionApproval value =
        result "authority.approveCopyDeletion" (fun writer ->
            WebWireCopyDeletionApproval.write writer value)

    let copyAdoptionApproval value =
        result "authority.approveCopyAdoption" (fun writer ->
            WebWireCopyAdoptionApproval.write writer value)

    let writerHandoffApproval value =
        result "authority.approveWriterHandoff" (fun writer ->
            WebWireWriterHandoffApproval.write writer value)

    let realDataActivationReview value =
        result "authority.reviewRealDataActivation" (fun writer ->
            WebWireRealDataActivation.review writer value)

    let realDataActivationApproval value =
        result "authority.approveRealDataActivation" (fun writer ->
            WebWireRealDataActivation.approval writer value)

    let lifecycleReview value =
        result "lifecycle.review" (fun writer -> WebWireLifecycle.review writer value)

    let lifecycleWrite endpoint value =
        result endpoint (fun writer -> WebWireLifecycle.write writer value)

    let tombstoneReview value =
        result "tombstone.review" (fun writer -> WebWireTombstone.review writer value)

    let tombstoneWrite endpoint value =
        result endpoint (fun writer -> WebWireTombstone.write writer value)
