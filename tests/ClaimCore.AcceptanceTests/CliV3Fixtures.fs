module ClaimCore.AcceptanceTests.CliV3Fixtures

open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open Expecto

let private encode endpoint input =
    JsonSerializer.Serialize(
        {|
            protocolVersion = 3
            endpoint = endpoint
            input = input
        |}
    )

let command endpoint operationId caseReference revision kind values =
    let input =
        box
            {|
                operationId = operationId
                caseReference = caseReference
                expectedRevision = string revision
                command =
                    {|
                        kind = kind
                        values = Map.ofList values
                    |}
            |}

    encode endpoint input

let execute operationId caseReference revision kind values =
    command "command.execute" operationId caseReference revision kind values

let prepare operationId caseReference revision kind values =
    command "command.prepare" operationId caseReference revision kind values

let private input endpoint value = encode endpoint (box value)

let caseGet reference =
    input "case.get" {| caseReference = reference |}

let caseList = input "case.list" {| limit = 50 |}

let caseHistory reference detail =
    input
        "case.history"
        {|
            caseReference = reference
            limit = 50
            detail = detail
        |}

let observe operationId =
    input "operation.observe" {| operationId = operationId |}

let recoveryList = input "recovery.list" {| limit = 50 |}

let recoveryInspect operationId =
    input "recovery.inspect" {| operationId = operationId |}

let recoveryResolve operationId requestSha256 =
    input
        "recovery.resolve"
        {|
            operationId = operationId
            requestSha256 = requestSha256
        |}

let recoveryDismiss operationId requestSha256 =
    input
        "recovery.dismiss"
        {|
            operationId = operationId
            requestSha256 = requestSha256
            confirmed = true
        |}

let recoveryExport operationId requestSha256 destination =
    input
        "recovery.export"
        {|
            operationId = operationId
            requestSha256 = requestSha256
            destination = destination
        |}

let importPreview endpoint source = input endpoint {| source = source |}

let importRetain endpoint source sourceSha256 =
    input
        endpoint
        {|
            source = source
            sourceSha256 = sourceSha256
            confirmed = true
        |}

let private environment (context: DatabaseFixture.Context) =
    let values = Dictionary<string, string>()
    values["CLAIMCORE_CONNECTION_FILE"] <- context.ApplicationConnectionFile
    values.Remove("CLAIMCORE_ADMIN_CONNECTION_FILE") |> ignore
    values :> IReadOnlyDictionary<string, string>

let call (context: DatabaseFixture.Context) (invocation: string) =
    let bytes = Encoding.UTF8.GetBytes(invocation)

    ProcessRunner.dotnet 60_000 context.CliDll [ "call" ] (environment context) (Some bytes)

let callBytes (context: DatabaseFixture.Context) (bytes: byte array) =
    ProcessRunner.dotnet 60_000 context.CliDll [ "call" ] (environment context) (Some bytes)

let session (context: DatabaseFixture.Context) (invocations: string list) =
    let content = String.concat "\n" invocations + "\n"
    let bytes = Encoding.UTF8.GetBytes(content)

    ProcessRunner.dotnet 60_000 context.CliDll [ "session" ] (environment context) (Some bytes)

let decode expectedExit expectedEndpoint (result: ProcessRunner.Result) =
    Expect.equal result.ExitCode expectedExit "Published CLI exit code"
    Expect.equal result.StandardError.Length 0 "Published CLI diagnostics"
    let document = JsonDocument.Parse(result.StandardOutput)
    let root = document.RootElement
    Expect.equal (root.GetProperty("protocolVersion").GetInt32()) 3 "CLI-v3 response version"
    Expect.equal (root.GetProperty("kind").GetString()) "result" "CLI result frame"
    Expect.equal (root.GetProperty("endpoint").GetString()) expectedEndpoint "CLI endpoint frame"
    document

let sessionLines expectedCount (result: ProcessRunner.Result) =
    Expect.equal result.ExitCode 0 "CLI-v3 session exit code"
    Expect.equal result.StandardError.Length 0 "CLI-v3 session diagnostics"
    let text = Encoding.UTF8.GetString(result.StandardOutput)
    let lines = text.TrimEnd('\n').Split('\n', StringSplitOptions.None) |> Array.toList
    Expect.equal lines.Length expectedCount "CLI-v3 session response count"
    lines

let decodeLine (expectedEndpoint: string) (line: string) =
    let document = JsonDocument.Parse(line)
    let root = document.RootElement
    Expect.equal (root.GetProperty("protocolVersion").GetInt32()) 3 "CLI-v3 session version"
    Expect.equal (root.GetProperty("kind").GetString()) "result" "CLI-v3 session result"

    Expect.equal
        (root.GetProperty("endpoint").GetString())
        expectedEndpoint
        "CLI-v3 session endpoint"

    document

let outcomeKind (document: JsonDocument) =
    document.RootElement.GetProperty("outcome").GetProperty("kind").GetString()
