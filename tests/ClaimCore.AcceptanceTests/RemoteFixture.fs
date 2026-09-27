module ClaimCore.AcceptanceTests.RemoteFixture

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json

let inputs = lazy (Configuration.load ())
let caseReference = lazy ("CC-CLI-LIVE-" + Guid.NewGuid().ToString("N"))

let private automationEnvironment () =
    let selected = inputs.Value
    let values = Dictionary<string, string>()
    values["CLAIMCORE_SERVICE_URL"] <- selected.ServiceUrl
    values["CLAIMCORE_OIDC_ISSUER"] <- selected.Issuer
    values["CLAIMCORE_OIDC_CLIENT_ID"] <- selected.ClientId
    values["CLAIMCORE_CLI_AUTH_MODE"] <- "automation"
    values["CLAIMCORE_OIDC_CLIENT_SECRET_FILE"] <- selected.SecretFile
    values["CLAIMCORE_CLI_OIDC_TRUST_ROOT_FILE"] <- selected.OidcCa
    values["CLAIMCORE_CLI_SERVICE_TRUST_ROOT_FILE"] <- selected.WebCertificate
    values :> IReadOnlyDictionary<string, string>

let private interactiveEnvironment mode =
    let selected = inputs.Value
    let values = Dictionary<string, string>()
    values["CLAIMCORE_SERVICE_URL"] <- selected.ServiceUrl
    values["CLAIMCORE_OIDC_ISSUER"] <- selected.Issuer
    values["CLAIMCORE_OIDC_CLIENT_ID"] <- selected.PublicClientId
    values["CLAIMCORE_CLI_AUTH_MODE"] <- "interactive"
    values["CLAIMCORE_CLI_OIDC_TRUST_ROOT_FILE"] <- selected.OidcCa
    values["CLAIMCORE_CLI_SERVICE_TRUST_ROOT_FILE"] <- selected.WebCertificate
    values["CLAIMCORE_CLI_TEST_AUTH_MODE"] <- mode
    values["CLAIMCORE_CLI_TEST_CREDENTIALS_FILE"] <- selected.OidcCredentialsFile
    values["CLAIMCORE_CLI_TEST_ISSUER"] <- selected.Issuer
    values["CLAIMCORE_CLI_TEST_PUBLIC_CLIENT_ID"] <- selected.PublicClientId
    values["CLAIMCORE_CLI_TEST_DRIVER"] <- selected.BrowserDriver

    let inheritedPath =
        Environment.GetEnvironmentVariable("PATH")
        |> Option.ofObj
        |> Option.defaultValue ""

    values["PATH"] <- selected.BrowserDirectory + string Path.PathSeparator + inheritedPath
    values :> IReadOnlyDictionary<string, string>

let encode endpoint input =
    JsonSerializer.Serialize(
        {|
            protocolVersion = 4
            endpoint = endpoint
            input = input
        |}
    )

let run arguments input =
    ProcessRunner.dotnet 60_000 inputs.Value.CliDll arguments (automationEnvironment ()) input

let call endpoint input =
    let source = encode endpoint input |> Encoding.UTF8.GetBytes
    run [ "call" ] (Some source)

let interactive mode endpoint input =
    let source = encode endpoint input |> Encoding.UTF8.GetBytes

    ProcessRunner.dotnet
        60_000
        inputs.Value.CliDll
        [ "call" ]
        (interactiveEnvironment mode)
        (Some source)

let withSecret secretFile endpoint input =
    let values = Dictionary<string, string>()

    for pair in automationEnvironment () do
        values[pair.Key] <- pair.Value

    values["CLAIMCORE_OIDC_CLIENT_SECRET_FILE"] <- secretFile
    let source = encode endpoint input |> Encoding.UTF8.GetBytes

    ProcessRunner.dotnet
        60_000
        inputs.Value.CliDll
        [ "call" ]
        (values :> IReadOnlyDictionary<string, string>)
        (Some source)

let parse expectedExit expectedEndpoint (result: ProcessRunner.Result) =
    if result.ExitCode <> expectedExit || result.StandardError.Length <> 0 then
        failwith "Published CLI returned an unexpected safe exit or diagnostics."

    let document = JsonDocument.Parse(ReadOnlyMemory result.StandardOutput)
    let root = document.RootElement

    if
        root.GetProperty("protocolVersion").GetInt32() <> 4
        || root.GetProperty("endpoint").GetString() <> expectedEndpoint
    then
        document.Dispose()
        failwith "Published CLI did not return the selected v4 endpoint."

    document

let tag (document: JsonDocument) =
    document.RootElement
        .GetProperty("service")
        .GetProperty("outcome")
        .GetProperty("tag")
        .GetString()

let id () = Guid.NewGuid().ToString("D")

let command endpoint operation revision kind values =
    call
        endpoint
        {|
            operationId = operation
            caseReference = caseReference.Value
            expectedRevision = string revision
            command = {| kind = kind; values = values |}
        |}
