namespace ClaimCore.Cli

open ClaimCore.Application
open ClaimCore.Contracts

[<RequireQualifiedAccess>]
type DiscoveryResponse =
    | Text of string
    | Json of byte array

/// Database-free CLI-v3 discovery. Every payload is rendered by Contracts; this module selects the
/// requested one and never composes wire bytes of its own.
module Discovery =
    let private requiredOption message value =
        match value with
        | Some item -> Ok item
        | None -> Error message

    let private projection () = ContractProjection.current ()

    let private json (contract: CanonicalContract) =
        DiscoveryResponse.Json(CanonicalContract.bytes contract)

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
  describe diagnostics
  schema invocation|response|definition|recovery-envelope
  schema endpoint <endpoint-id>
  call
  session

Discovery commands do not open PostgreSQL. call reads one strict JSON invocation from stdin; session reads NDJSON."""

    let versionJson () =
        BuildIdentityCodec.bytes BuildIdentity.current

    let describe arguments =
        let model = projection ()

        match arguments with
        | [ "summary" ] -> Ok(json (CliDiscovery.summary model))
        | [ "fields" ] -> Ok(json (CliDiscovery.fields model))
        | [ "fields"; name ] -> Ok(json (CliDiscovery.field model name))
        | [ "commands" ] -> Ok(json (CliDiscovery.commands model))
        | [ "commands"; kind ] -> Ok(json (CliDiscovery.command model kind))
        | [ "endpoints" ] -> Ok(json (CliDiscovery.endpoints model))
        | [ "endpoints"; identifier ] -> Ok(json (CliDiscovery.endpoint model identifier))
        | [ "diagnostics" ] -> Ok(json (CliDiscovery.diagnostics model))
        | [ "recovery" ] -> Ok(json (CliDiscovery.recovery model))
        | _ -> Error ProtocolProblem.UnsupportedInvocation

    let schema arguments =
        let model = projection ()

        match arguments with
        | [ "invocation" ] -> Ok(json (CliSchemas.invocation model))
        | [ "response" ] -> Ok(json (CliSchemas.response model))
        | [ "definition" ] -> Ok(json (CliSchemas.definition model))
        | [ "recovery-envelope" ] -> Ok(json (CliSchemas.recoveryEnvelope model))
        | [ "endpoint"; identifier ] ->
            CliSchemas.endpoint model identifier
            |> Option.map json
            |> requiredOption ProtocolProblem.UnknownEndpoint
        | _ -> Error ProtocolProblem.UnsupportedInvocation
