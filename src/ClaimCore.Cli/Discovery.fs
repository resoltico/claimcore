namespace ClaimCore.Cli

open System
open System.Buffers
open System.Text.Json
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Domain

[<RequireQualifiedAccess>]
type DiscoveryResponse =
    | Text of string
    | Json of byte array

module Discovery =
    let private requiredOption message value =
        match value with
        | Some item -> Ok item
        | None -> Error message

    let private encode write =
        let buffer = ArrayBufferWriter<byte>()

        use writer =
            new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = false, SkipValidation = false))

        write writer
        writer.Flush()
        Array.append (buffer.WrittenSpan.ToArray()) [| byte '\n' |]

    let private copy (contract: CanonicalContract) = CanonicalContract.bytes contract

    let private projection () = ContractProjection.current ()

    let private semantic () =
        ContractRenderers.semantic (projection ())

    let private cli () = ContractRenderers.cli (projection ())

    let private fromSemantic (collection: string) (selector: JsonElement -> JsonElement option) =
        use document = JsonDocument.Parse(CanonicalContract.bytes (semantic ()))
        let property = document.RootElement.GetProperty(collection)

        selector property
        |> Option.map (fun value -> encode (fun writer -> value.WriteTo(writer)))
        |> Option.defaultWith (fun () -> encode (fun writer -> writer.WriteNullValue()))

    let private field (name: string) =
        fromSemantic "fields" (fun (fields: JsonElement) ->
            fields.EnumerateArray()
            |> Seq.tryFind (fun item -> item.GetProperty("name").GetString() = name))

    let private command (kind: string) =
        fromSemantic "commands" (fun (commands: JsonElement) ->
            commands.EnumerateArray()
            |> Seq.tryFind (fun item -> item.GetProperty("kind").GetString() = kind))

    let private fromCli (collection: string) (selector: JsonElement -> JsonElement option) =
        use document = JsonDocument.Parse(CanonicalContract.bytes (cli ()))
        let property = document.RootElement.GetProperty(collection)

        selector property
        |> Option.map (fun value -> encode (fun writer -> value.WriteTo(writer)))
        |> Option.defaultWith (fun () -> encode (fun writer -> writer.WriteNullValue()))

    let private endpoint (identifier: string) =
        fromCli "endpoints" (fun (endpoints: JsonElement) ->
            endpoints.EnumerateArray()
            |> Seq.tryFind (fun item -> item.GetProperty("id").GetString() = identifier))

    let private summary () =
        let model = projection ()

        let semanticFingerprint =
            ContractRenderers.semanticFingerprint model
            |> ClaimCore.Application.SemanticCoreFingerprint.value

        let cliFingerprint =
            ContractRenderers.cliFingerprint model |> CliWireContractFingerprint.value

        encode (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("application", model.Semantic.Application)
            writer.WriteString("productVersion", BuildIdentity.current.Version)
            writer.WriteString("scope", model.Semantic.Scope)
            writer.WriteNumber("businessFieldCount", model.Semantic.Fields.Length)
            writer.WriteNumber("cliProtocolVersion", 3)
            writer.WriteString("semanticCoreFingerprint", semanticFingerprint)
            writer.WriteString("cliWireContractFingerprint", cliFingerprint)
            writer.WriteNumber("maximumPageSize", model.Semantic.MaximumPageSize)
            writer.WriteEndObject())

    let private recovery () =
        let model = projection ()

        encode (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("recoveryEnvelopeFormat", model.Semantic.RecoveryEnvelopeFormat)
            writer.WriteStartArray("endpoints")

            model.CliEndpoints
            |> List.filter (fun item -> item.Identifier.StartsWith("recovery."))
            |> List.iter (fun item -> writer.WriteStringValue(item.Identifier))

            writer.WriteEndArray()
            writer.WriteEndObject())

    let help () =
        DiscoveryResponse.Text
            """ClaimCore CLI v3
  help [topic]
  version
  version --json
  describe summary
  describe fields [field-name]
  describe commands [command-kind]
  describe endpoints [endpoint-id]
  describe recovery
  schema invocation|response|definition|recovery-envelope
  schema endpoint <endpoint-id>
  call
  session

Discovery commands do not open PostgreSQL. call reads one strict JSON invocation from stdin; session reads NDJSON."""

    let versionJson () =
        BuildIdentityCodec.bytes BuildIdentity.current

    let describe arguments =
        match arguments with
        | [ "summary" ] -> Ok(DiscoveryResponse.Json(summary ()))
        | [ "fields" ] -> Ok(DiscoveryResponse.Json(fromSemantic "fields" Some))
        | [ "fields"; name ] -> Ok(DiscoveryResponse.Json(field name))
        | [ "commands" ] -> Ok(DiscoveryResponse.Json(fromSemantic "commands" Some))
        | [ "commands"; kind ] -> Ok(DiscoveryResponse.Json(command kind))
        | [ "endpoints" ] -> Ok(DiscoveryResponse.Json(fromCli "endpoints" Some))
        | [ "endpoints"; identifier ] -> Ok(DiscoveryResponse.Json(endpoint identifier))
        | [ "recovery" ] -> Ok(DiscoveryResponse.Json(recovery ()))
        | _ -> Error "Use help for the supported describe commands."

    let schema arguments =
        let model = projection ()

        match arguments with
        | [ "invocation" ] -> Ok(DiscoveryResponse.Json(copy (CliSchemas.invocation model)))
        | [ "response" ] -> Ok(DiscoveryResponse.Json(copy (CliSchemas.response model)))
        | [ "definition" ] -> Ok(DiscoveryResponse.Json(copy (CliSchemas.definition model)))
        | [ "recovery-envelope" ] ->
            Ok(DiscoveryResponse.Json(copy (CliSchemas.recoveryEnvelope model)))
        | [ "endpoint"; identifier ] ->
            CliSchemas.endpoint model identifier
            |> Option.map (copy >> DiscoveryResponse.Json)
            |> requiredOption "The endpoint is not declared by the generated CLI contract."
        | _ -> Error "Use help for the supported schema commands."
