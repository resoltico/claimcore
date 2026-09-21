namespace ClaimCore.Contracts

open System
open System.Buffers
open System.Text
open System.Text.Json
open ClaimCore.Application

/// Database-free CLI-v3 discovery payloads. These are wire shapes, so Contracts owns them for the
/// same reason it owns every endpoint codec: an adapter that composed them itself would fork the
/// generated contract without the schema, corpus, or fingerprint noticing.
[<RequireQualifiedAccess>]
module CliDiscovery =
    let private options = JsonWriterOptions(Indented = false, SkipValidation = false)

    let private document (write: Utf8JsonWriter -> unit) =
        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer, options)
        write writer
        writer.Flush()
        let bytes = Array.append (buffer.WrittenSpan.ToArray()) [| byte '\n' |]

        {
            Text = Encoding.UTF8.GetString(bytes)
            Bytes = bytes
        }

    let private slice (source: CanonicalContract) collection selector =
        use parsed = JsonDocument.Parse(CanonicalContract.bytes source)
        let property = parsed.RootElement.GetProperty(collection: string)

        match selector property with
        | Some(value: JsonElement) -> document (fun writer -> value.WriteTo(writer))
        | None -> document (fun writer -> writer.WriteNullValue())

    let private named (collection: JsonElement) property value =
        collection.EnumerateArray()
        |> Seq.tryFind (fun item -> item.GetProperty(property: string).GetString() = value)

    let summary (projection: ContractModel) =
        let semanticFingerprint =
            ContractRenderers.semanticFingerprint projection
            |> SemanticCoreFingerprint.value

        let cliFingerprint =
            ContractRenderers.cliFingerprint projection |> CliWireContractFingerprint.value

        document (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("application", projection.Semantic.Application)
            writer.WriteString("productVersion", BuildIdentity.current.Version)
            writer.WriteString("scope", projection.Semantic.Scope)
            writer.WriteNumber("businessFieldCount", projection.Semantic.Fields.Length)
            writer.WriteNumber("cliProtocolVersion", 3)
            writer.WriteString("semanticCoreFingerprint", semanticFingerprint)
            writer.WriteString("cliWireContractFingerprint", cliFingerprint)
            writer.WriteNumber("maximumPageSize", projection.Semantic.MaximumPageSize)
            writer.WriteEndObject())

    let recovery (projection: ContractModel) =
        document (fun writer ->
            writer.WriteStartObject()

            writer.WriteNumber(
                "recoveryEnvelopeFormat",
                projection.Semantic.RecoveryEnvelopeFormat
            )

            writer.WriteStartArray("endpoints")

            projection.CliEndpoints
            |> List.filter (fun item ->
                item.Identifier.StartsWith("recovery.", StringComparison.Ordinal))
            |> List.iter (fun item -> writer.WriteStringValue(item.Identifier))

            writer.WriteEndArray()
            writer.WriteEndObject())

    let fields (projection: ContractModel) =
        slice (ContractRenderers.semantic projection) "fields" Some

    let field (projection: ContractModel) name =
        slice (ContractRenderers.semantic projection) "fields" (fun items ->
            named items "name" name)

    let commands (projection: ContractModel) =
        slice (ContractRenderers.semantic projection) "commands" Some

    let command (projection: ContractModel) kind =
        slice (ContractRenderers.semantic projection) "commands" (fun items ->
            named items "kind" kind)

    let endpoints (projection: ContractModel) =
        slice (ContractRenderers.cli projection) "endpoints" Some

    let endpoint (projection: ContractModel) identifier =
        slice (ContractRenderers.cli projection) "endpoints" (fun items ->
            named items "id" identifier)
