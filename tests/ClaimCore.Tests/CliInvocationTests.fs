module ClaimCore.Tests.CliInvocationTests

open System
open System.Text
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Contract
open ClaimCore.Contract.Protocol
open ClaimCore.Application

let private aliases () =
    for command in CliCommands.commands do
        for alias in [ "--help"; "-h" ] do
            Expect.equal
                (CliInvocations.admit [ command; alias ])
                (Ok(CliInvocation.Help(command, "en")))
                "Offline exact aliases"

            Expect.isError
                (CliInvocations.admit [ command; alias; "--language"; "lv" ])
                "Extra operands never dispatch"

    for language in LocalizationPolicy.languages do
        for topic in CliCommands.topics do
            Expect.equal
                (CliInvocations.admit [ "help"; topic; "--language"; language ])
                (Ok(CliInvocation.Help(topic, language)))
                "Canonical localized help"

            Expect.isGreaterThan
                (ClaimCore.Cli.ContractDiscovery.helpTopic language topic).Length
                100
                "Useful guidance"

let private options () =
    Expect.equal
        (CliInvocations.admit [ "call"; "--retry" ])
        (Ok(CliInvocation.Call true))
        "Retry context is explicit and leaves the request frame unchanged."

    Expect.equal
        (CliInvocations.admit [ "call" ])
        (Ok(CliInvocation.Call false))
        "An ordinary call starts a fresh attempt."

    for args in
        [
            [ "call"; "--format"; "text" ]
            [ "describe"; "--language"; "lv" ]
            [ "schema"; "invocation"; "--language"; "en" ]
            [ "help"; "call"; "--language"; "en"; "--language"; "en" ]
            [ "help"; "secret-canary" ]
            [ "describe"; "secret-canary" ]
            [ "describe"; "summary"; "extra" ]
            [ "describe"; "commands"; "OPEN"; "extra" ]
            [ "schema"; "context"; "extra" ]
            [ "describe"; "--format"; "text"; "--format"; "text" ]
        ] do
        Expect.isError (CliInvocations.admit args) "Unsupported combinations refuse atomically"

    Expect.equal
        (CliInvocations.admit
            [ "describe"; "commands"; "OPEN"; "--format"; "text"; "--language"; "ar" ])
        (Ok(CliInvocation.Describe(CliDescription.Commands(Some "OPEN"), true, "ar")))
        "Explicit human format"

    Expect.isOk
        (Frames.admit (Encoding.UTF8.GetBytes "{\"endpoint\":\"context\",\"input\":{}}"))
        "Help example is admitted"

[<Tests>]
let tests =
    testList
        "CLI invocation admission"
        [
            testCase "exact help aliases and all localized topics" aliases
            testCase "strict human versus machine option combinations" options
        ]

let private grammarDescriptors () =
    ClaimCore.Cli.ContractDiscovery.descriptors ()
    |> StrictJson.read Limits.MaximumResponseBytes
    |> Result.defaultWith (fun _ -> failtest "Embedded descriptors are valid")

let private prepared arguments =
    Grammar.prepare
        (fun () -> "10000000-0000-4000-8000-000000000001")
        (grammarDescriptors ())
        arguments
    |> Result.defaultWith (fun _ -> failtest "Derived verb prepares a request")

let private verbCoverage () =
    let descriptors =
        JsonNode.Parse(ClaimCore.Cli.ContractDiscovery.descriptors ()) |> nonNull

    let commands = descriptors["commands"] |> nonNull |> _.AsArray()

    let verbs =
        commands
        |> Seq.map (fun item ->
            (nonNull item)["kind"] |> nonNull |> _.GetValue<string>() |> Grammar.caseVerb)
        |> Seq.toList

    Expect.equal
        (List.distinct verbs).Length
        CaseActKinds.all.Length
        "Exactly one derived verb for each case act"

    for command in commands do
        let command = nonNull command
        let kind = command["kind"] |> nonNull |> _.GetValue<string>()
        let inputs = command["inputs"] |> nonNull

        let flags =
            if (inputs["kind"] |> nonNull).GetValue<string>() = "CORRECTION_GROUPS" then
                inputs["groups"]
                |> nonNull
                |> _.AsArray()
                |> Seq.collect (fun group ->
                    [ "--" + ((nonNull group)["name"] |> nonNull).GetValue<string>(); "keep" ])
                |> Seq.toList
            else
                inputs["fields"]
                |> nonNull
                |> _.AsArray()
                |> Seq.collect (fun field ->
                    let name = ((nonNull field)["fieldName"] |> nonNull).GetValue<string>()
                    [ "--" + Grammar.kebab name; "synthetic" ])
                |> Seq.toList

        let revision = if kind = "OPEN" then [] else [ "--expected-revision"; "1" ]

        let request =
            prepared ([ "case"; Grammar.caseVerb kind; "SYNTHETIC" ] @ revision @ flags)

        Expect.isOk (Frames.admit request.Bytes) "Every descriptor-derived case verb is admitted"


let private authorityVerbCoverage () =
    for kind in AuthorityKinds.all |> List.filter ((<>) AuthorityKind.ProvisionOwner) do
        for verb in Grammar.authorityVerbs (AuthorityKinds.token kind) do
            let role =
                if List.contains verb [ "grant"; "revoke" ] then
                    [ "--role"; "CASE_READER" ]
                else
                    []

            let request =
                prepared (
                    [
                        "authority"
                        verb
                        "--kind"
                        "HUMAN"
                        "--issuer"
                        "synthetic"
                        "--subject"
                        "synthetic"
                    ]
                    @ role
                )

            Expect.isOk (Frames.admit request.Bytes) "Every authority act alias is admitted"

let private verbPreparation () =
    let args = [ "case"; "close"; "SYNTHETIC"; "--expected-revision"; "1" ]
    let actual = prepared (args @ [ "--dry-run" ])
    Expect.isTrue actual.DryRun "Dry run is an offline preparation"
    Expect.isTrue actual.Text "Human commands default to text"

    let frame =
        Frames.admit actual.Bytes |> Result.defaultWith (fun _ -> failtest "Frame")

    Expect.equal (Frames.encodeInvocation frame) actual.Bytes "Dry run is exactly the call frame"

    let preview =
        prepared (args @ [ "--preview"; "--format"; "json"; "--language"; "lv" ])

    Expect.isFalse preview.Text "Explicit JSON format"
    Expect.equal preview.Language "lv" "Explicit language"

    Expect.equal
        (Frames.admit preview.Bytes |> Result.map (Frames.endpoint >> Api.id))
        (Ok "operation.preview")
        "Preview uses shared preview endpoint"

    for rejected in
        [
            [ "case"; "close"; "SYNTHETIC" ]
            args @ [ "--expected-revision"; "2" ]
            args @ [ "--unknown"; "x" ]
            args @ [ "extra" ]
            args @ [ "--format"; "binary" ]
        ] do
        Expect.isError
            (Grammar.prepare
                (fun () -> Guid.NewGuid().ToString("D"))
                (grammarDescriptors ())
                rejected)
            "Malformed verbs refuse atomically"

[<Tests>]
let grammarTests =
    testList
        "derived CLI verbs"
        [
            testCase "every case and authority act has a derived verb" verbCoverage
            testCase "every authority alias has a core act" authorityVerbCoverage
            testCase "preparation is exact and malformed flags fail" verbPreparation
        ]

let private nonLedgerVerbExamples =
    [
        [ "case"; "get"; "SYNTHETIC" ], "case.get"
        [ "case"; "list"; "--limit"; "5"; "--cursor"; "synthetic" ], "case.list"
        [ "case"; "history"; "SYNTHETIC"; "--full" ], "case.history"
        [ "operation"; "observe"; "10000000-0000-4000-8000-000000000001" ], "operation.observe"
        [ "operation"; "pending" ], "operation.pending"
        [ "operation"; "forget"; "10000000-0000-4000-8000-000000000001" ], "operation.forget"
        [ "authority"; "list" ], "authority.list"
        [ "authority"; "grants"; "10000000-0000-4000-8000-000000000001" ], "authority.grants"
        [ "context" ], "context"
    ]

let private assertNonLedgerVerbCoverage registry mapped =
    // Session routes are browser transport; preview is selected by a mutation verb option.
    let excluded = Set.ofList [ "session"; "session.logout"; "operation.preview" ]

    let eligible =
        registry
        |> List.filter (fun endpoint ->
            not (Api.executesLedger endpoint)
            && not (Set.contains (Api.id endpoint) excluded))
        |> List.map Api.id
        |> Set.ofList

    Expect.equal
        (List.distinct mapped).Length
        mapped.Length
        "Each eligible read or operational write has exactly one verb mapping"

    Expect.equal
        (Set.ofList mapped)
        eligible
        "Verb mappings exactly cover eligible reads and operational writes"

let private nonLedgerVerbs () =
    let mapped =
        nonLedgerVerbExamples
        |> List.map (fun (arguments, endpoint) ->
            let invocation = prepared arguments

            let admitted =
                Frames.admit invocation.Bytes
                |> Result.defaultWith (fun _ -> failtest "Prepared read frame")

            let actual = Frames.endpoint admitted |> Api.id
            Expect.equal actual endpoint "A read or operational verb selects its registry endpoint"

            let expectedEffect =
                if endpoint = "operation.forget" then
                    EndpointEffect.OperationalWrite
                else
                    EndpointEffect.Read

            Expect.equal
                (Api.effect (Frames.endpoint admitted))
                expectedEffect
                "Declared effect agrees with the verb's actual role"

            actual)

    assertNonLedgerVerbCoverage Api.all mapped

let private nonLedgerCoverageControls () =
    let mapped = nonLedgerVerbExamples |> List.map snd
    assertNonLedgerVerbCoverage Api.all mapped

    let additional =
        { Api.context with
            Id = "synthetic.read"
        }

    let boxed =
        { new AnyEndpoint with
            member _.Accept visitor = visitor.Visit additional
        }

    Expect.throws
        (fun () -> assertNonLedgerVerbCoverage (boxed :: Api.all) mapped)
        "An added eligible registry endpoint requires a verb"

    Expect.throws
        (fun () -> assertNonLedgerVerbCoverage Api.all (List.tail mapped))
        "Removing a required mapping fails the same coverage assertion"

    Expect.throws
        (fun () -> assertNonLedgerVerbCoverage Api.all (List.head mapped :: mapped))
        "Duplicating a mapping fails uniqueness even when sets agree"

let private opaqueRetryVerb () =
    let recoveryId = "10000000-0000-4000-8000-000000000002"
    let operationId = "10000000-0000-4000-8000-000000000003"
    let invocation = prepared [ "operation"; "retry"; recoveryId; operationId ]

    let admitted =
        Frames.admit invocation.Bytes
        |> Result.defaultWith (fun _ -> failtest "Retry frame")

    Expect.equal
        (Api.id (Frames.endpoint admitted))
        Api.operationRetry.Id
        "Registry-selected recovery retry"

    Expect.equal
        (Api.effect (Frames.endpoint admitted))
        EndpointEffect.LedgerExecution
        "Retry may accept a previously unaccepted operation"

    Expect.equal
        (Frames.input admitted)
        (Api.operationRetry.Input.Write
            {
                RecoveryId = Guid.Parse recoveryId
                OperationId = Guid.Parse operationId
            })
        "Caller supplies both frozen identifiers"

    Expect.isTrue
        (Frames.retryingUncertain admitted)
        "Opaque retry intrinsically retains earlier uncertainty"

let private localizedReplyRendering () =
    let path =
        System.IO.Path.Combine(
            ClaimCore.TestSupport.RepositoryRoot.find (),
            "tests/fixtures/wire-response-examples.json"
        )

    use examples = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllBytes path)

    for example in examples.RootElement.GetProperty("cases").EnumerateArray() do
        if example.GetProperty("valid").GetBoolean() then
            let reply =
                StrictJson.read
                    Limits.MaximumResponseBytes
                    (Encoding.UTF8.GetBytes(example.GetProperty("value").GetRawText()))
                |> Result.defaultWith (fun _ -> failtest "Frozen JSON")

            let frame =
                {
                    Endpoint = Some(example.GetProperty("endpoint").GetString() |> nonNull)
                    OperationId = None
                    Delivery = DeliveryState.Delivered
                    Result = FrameResult.Reply reply
                }

            for language in LocalizationPolicy.languages do
                let rendered =
                    ClaimCore.Cli.TextReply.bytes language (Frames.encode frame)
                    |> Encoding.UTF8.GetString

                Expect.isTrue
                    (rendered.EndsWith("\n", StringComparison.Ordinal))
                    "Human output ends with LF"

                Expect.isGreaterThan rendered.Length 1 "Every admitted reply has human output"

[<Tests>]
let renderingTests =
    testList
        "CLI verb rendering"
        [
            testCase "every read and operational write has a registry-selected verb" nonLedgerVerbs
            testCase "opaque retry keeps explicit identifiers and prior uncertainty" opaqueRetryVerb
            testCase
                "non-ledger coverage rejects added endpoints and missing or repeated mappings"
                nonLedgerCoverageControls
            testCase "frozen admitted replies render in every language" localizedReplyRendering
        ]

let private authorityPreviewInvocation verb =
    let role =
        if List.contains verb [ "grant"; "revoke" ] then
            [ "--role"; "CASE_READER" ]
        else
            []

    let prepared =
        prepared (
            [
                "authority"
                verb
                "--kind"
                "HUMAN"
                "--issuer"
                "synthetic"
                "--subject"
                "synthetic-subject"
                "--preview"
            ]
            @ role
        )

    Frames.admit prepared.Bytes |> Result.defaultWith (fun _ -> failtest "Prepared")


let private authorityPreviewFrame enabledBefore enabledAfter grantBefore grantAfter =
    let grants present : AuthorityGrant list =
        if present then
            [
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
            ]
        else
            []

    let response =
        {
            PreviewReply.OperationId = Guid.Parse "10000000-0000-4000-8000-000000000001"
            Outcome =
                PreviewResult.Reviewed(
                    OperationReview.Authority
                        {
                            Target = None
                            Before =
                                {
                                    Enabled = enabledBefore
                                    Grants = grants grantBefore
                                }
                            After =
                                {
                                    Enabled = enabledAfter
                                    Grants = grants grantAfter
                                }
                        }
                )
        }

    let frame =
        {
            Endpoint = Some "operation.preview"
            OperationId = Some response.OperationId
            Delivery = DeliveryState.Delivered
            Result = FrameResult.Reply(Api.operationPreview.Output.Write response)
        }

    frame

let private authorityPreviewText verb enabledBefore enabledAfter grantBefore grantAfter =
    let invocation = authorityPreviewInvocation verb
    let frame = authorityPreviewFrame enabledBefore enabledAfter grantBefore grantAfter

    for language in LocalizationPolicy.languages do
        let rendered =
            ClaimCore.Cli.TextReply.bytesFor language (Some invocation) (Frames.encode frame)
            |> Encoding.UTF8.GetString

        Expect.stringContains
            rendered
            (verb + " HUMAN synthetic synthetic-subject")
            "Authority preview identifies the exact act and principal"

        if enabledBefore <> enabledAfter then
            let expected =
                (if enabledBefore then "true" else "false")
                + " → "
                + (if enabledAfter then "true" else "false")

            Expect.stringContains rendered expected "The enabled transition is displayed"

        if grantBefore <> grantAfter then
            let expected =
                if grantBefore then
                    "CASE_READER  INSTALLATION  — → —"
                else
                    "— → CASE_READER  INSTALLATION  —"

            Expect.stringContains
                rendered
                expected
                "The role and installation scope are displayed in the correct direction"

[<Tests>]
let authorityTextTests =
    testList
        "CLI authority preview text"
        [
            testCase "register" (fun () -> authorityPreviewText "register" false true false false)
            testCase "enable" (fun () -> authorityPreviewText "enable" false true false false)
            testCase "disable" (fun () -> authorityPreviewText "disable" true false false false)
            testCase "grant" (fun () -> authorityPreviewText "grant" true true false true)
            testCase "revoke" (fun () -> authorityPreviewText "revoke" true true true false)
        ]
