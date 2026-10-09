namespace ClaimCore.Contracts

open System.Text.Json
open ClaimCore.Application
open WebWireQueries

module internal WebWireDiscovery =
    let private currentWebFingerprint =
        lazy
            (ContractProjection.current ()
             |> ContractRenderers.webFingerprint
             |> WebWireContractFingerprint.value)

    let session (writer: Utf8JsonWriter) (authenticated: bool) (antiforgeryToken: string option) =
        outcome writer "SNAPSHOT" (fun () ->
            writer.WriteStartObject()
            writer.WriteBoolean("authenticated", authenticated)
            writer.WriteString("webFingerprint", currentWebFingerprint.Value)

            match antiforgeryToken with
            | Some value -> writer.WriteString("antiforgeryToken", value)
            | None -> writer.WriteNull("antiforgeryToken")

            writer.WriteEndObject())

    let description (writer: Utf8JsonWriter) (value: CoreDescription) =
        outcome writer "DESCRIBED" (fun () ->
            writer.WriteStartObject()

            writer.WriteString(
                "semanticFingerprint",
                value.SemanticFingerprint |> SemanticCoreFingerprint.value
            )

            let projection = ContractProjection.create value.Contract

            writer.WriteString(
                "webFingerprint",
                ContractRenderers.webFingerprint projection |> WebWireContractFingerprint.value
            )

            writer.WritePropertyName("runtime")
            writer.WriteStartObject()
            writer.WriteString("productVersion", value.Runtime.ProductVersion)

            writer.WriteString(
                "effectiveBusinessDate",
                value.Runtime.EffectiveBusinessDate.ToString("O")
            )

            writer.WriteString("timeZoneId", value.Runtime.TimeZoneId)
            writer.WriteEndObject()
            writer.WritePropertyName("definition")
            WebWireValues.semanticDefinition writer value
            writer.WriteEndObject())

    let definition (writer: Utf8JsonWriter) (value: QueryOutcome<CoreDescription>) =
        match value with
        | QueryOutcome.Succeeded value -> description writer value
        | QueryOutcome.Rejected rejection ->
            outcome writer "REJECTED" (fun () -> CliWireValues.rejection writer rejection)
        | QueryOutcome.Failed fault ->
            outcome writer "FAILED" (fun () -> CliWireValues.fault writer fault)
        | QueryOutcome.Cancelled -> outcome writer "CANCELLED" writer.WriteNullValue
