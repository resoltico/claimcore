module ClaimCore.AcceptanceTests.RemoteFixture

open System
open Expecto
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
    values["CLAIMCORE_CLI_SERVICE_TRUST_ROOT_FILE"] <- selected.ServiceCa
    values :> IReadOnlyDictionary<string, string>

let private interactiveEnvironment mode username =
    let selected = inputs.Value
    let values = Dictionary<string, string>()
    values["CLAIMCORE_SERVICE_URL"] <- selected.ServiceUrl
    values["CLAIMCORE_OIDC_ISSUER"] <- selected.Issuer
    values["CLAIMCORE_OIDC_CLIENT_ID"] <- selected.PublicClientId
    values["CLAIMCORE_CLI_AUTH_MODE"] <- "interactive"
    values["CLAIMCORE_CLI_OIDC_TRUST_ROOT_FILE"] <- selected.OidcCa
    values["CLAIMCORE_CLI_SERVICE_TRUST_ROOT_FILE"] <- selected.ServiceCa
    values["CLAIMCORE_CLI_TEST_AUTH_MODE"] <- mode

    username
    |> Option.iter (fun value -> values["CLAIMCORE_CLI_TEST_USERNAME"] <- value)

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
        (interactiveEnvironment mode None)
        (Some source)

let interactiveSession frames =
    let source = (String.concat "\n" frames + "\n") |> Encoding.UTF8.GetBytes

    ProcessRunner.dotnet
        90_000
        inputs.Value.CliDll
        [ "session" ]
        (interactiveEnvironment "valid" None)
        (Some source)

let interactiveSessionAs username frames =
    let source = (String.concat "\n" frames + "\n") |> Encoding.UTF8.GetBytes

    ProcessRunner.dotnet
        90_000
        inputs.Value.CliDll
        [ "session" ]
        (interactiveEnvironment "valid" (Some username))
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

let observedDestinationFailure operationId digest destination =
    let original = File.ReadAllBytes(destination)

    let repeated =
        call
            "recovery.export"
            {|
                operationId = operationId
                requestSha256 = digest
                destination = destination
            |}

    Expect.equal repeated.ExitCode 3 "A private file refuses overwrite after observed export"
    use response = JsonDocument.Parse(ReadOnlyMemory repeated.StandardOutput)

    Expect.equal
        (response.RootElement.GetProperty("executionPhase").GetString())
        "RESULT_OBSERVED"
        "Actual authenticated HTTP export completed"

    Expect.isTrue
        (File.ReadAllBytes(destination) = original)
        "Private destination remains unchanged"
