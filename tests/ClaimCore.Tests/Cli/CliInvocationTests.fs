module ClaimCore.Tests.CliInvocationTests

open ClaimCore.Cli

open System
open System.Text
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Application

let private aliases () =
    for command, topic in
        [
            "help", CliTopic.Help HelpTopic.Help
            "version", CliTopic.Help HelpTopic.Version
            "describe", CliTopic.Help HelpTopic.Describe
            "schema", CliTopic.Help HelpTopic.Schema
            "call", CliTopic.Help HelpTopic.Call
            "session", CliTopic.Help HelpTopic.Session
        ] do
        for alias in [ "--help"; "-h" ] do
            Expect.equal
                (CliInvocations.admit [ command; alias ])
                (Ok(CliInvocation.Help(topic, "en")))
                "Offline exact aliases"

            Expect.isError
                (CliInvocations.admit [ command; alias; "--language"; "lv" ])
                "Extra operands never dispatch"

    for language in LocalizationPolicy.languages do
        for topic in CliTopic.all do
            Expect.equal
                (CliInvocations.admit [ "help"; CliTopic.token topic; "--language"; language ])
                (Ok(CliInvocation.Help(topic, language)))
                "Canonical localized help"

            Expect.isGreaterThan
                (ClaimCore.Cli.ContractDiscovery.helpTopic language topic).Length
                20
                "Useful guidance"

let private options () =
    Expect.equal
        (CliInvocations.admit [ "call" ])
        (Ok(CliInvocation.Call))
        "An ordinary call starts a fresh attempt."

    for args in
        [
            [ "call"; "--format"; "text" ]
            [ "describe"; "--language"; "unknown" ]
            [ "schema"; "invocation"; "--language"; "en" ]
            [ "help"; "call"; "--language"; "en"; "--language"; "en" ]
            [ "help"; "secret-canary" ]
            [ "describe"; "secret-canary" ]
            [ "describe"; "summary"; "extra" ]
            [ "describe"; "acts"; "OPEN"; "extra" ]
            [ "schema"; "context"; "extra" ]
            [ "describe"; "--format"; "text"; "--format"; "text" ]
        ] do
        Expect.isError (CliInvocations.admit args) "Unsupported combinations refuse atomically"

    Expect.equal
        (CliInvocations.admit [ "describe"; "case"; "open"; "--format"; "text"; "--language"; "ar" ])
        (Ok(CliInvocation.Describe(CliDescription.Operation [ "case"; "open" ], true, "ar")))
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

let private prepared arguments =
    Grammar.prepare (fun () -> "10000000-0000-4000-8000-000000000001") arguments
    |> Result.defaultWith (fun issues -> failtestf "Request preparation failed: %A" issues)

let private verbPreparation () =
    let args = [ "case"; "close"; "SYNTHETIC"; "--expected-revision"; "1" ]
    let actual = prepared (args @ [ "--dry-run" ])
    Expect.isTrue actual.DryRun "Dry run is an offline preparation"
    Expect.equal actual.Format VerbFormat.Text "Human acts default to text"

    let frame =
        Frames.admit actual.Bytes |> Result.defaultWith (fun _ -> failtest "Frame")

    Expect.equal (Frames.encodeInvocation frame) actual.Bytes "Dry run is exactly the call frame"

    let preview =
        prepared (args @ [ "--preview"; "--format"; "json"; "--language"; "lv" ])

    Expect.equal preview.Format VerbFormat.Json "Explicit JSON format"
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
            (Grammar.prepare (fun () -> Guid.NewGuid().ToString("D")) rejected)
            "Malformed verbs refuse atomically"

[<Tests>]
let grammarTests =
    testList
        "derived CLI verbs"
        [ testCase "preparation is exact and malformed flags fail" verbPreparation ]

let private nonLedgerVerbExamples =
    OperationCatalog.all
    |> List.filter (fun operation -> operation.SelectedAct.IsNone)
    |> List.map (fun operation ->
        ClaimCore.Tests.CliExamples.queryArguments operation,
        ActorEndpoint.token operation.Endpoint)

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
                ClaimCore.TestSupport.Inventory.rows "endpoints"
                |> List.find (fun row -> row.GetProperty("token").GetString() = endpoint)
                |> fun row -> row.GetProperty("effect").GetString() |> nonNull

            Expect.equal
                (string (Api.effect (Frames.endpoint admitted)))
                expectedEffect
                "Independent inventory agrees with the verb's actual effect"

            actual)

    assertNonLedgerVerbCoverage Api.all mapped

let private nonLedgerCoverageControls () =
    let mapped = nonLedgerVerbExamples |> List.map snd
    assertNonLedgerVerbCoverage Api.all mapped

    Expect.throws
        (fun () -> assertNonLedgerVerbCoverage Api.all ("synthetic.read" :: mapped))
        "A mapping outside the closed endpoint registry is rejected"

    Expect.throws
        (fun () -> assertNonLedgerVerbCoverage Api.all (List.tail mapped))
        "Removing a required mapping fails the same coverage assertion"

    Expect.throws
        (fun () -> assertNonLedgerVerbCoverage Api.all (List.head mapped :: mapped))
        "Duplicating a mapping fails uniqueness even when sets agree"

let private resolveVerb () =
    let operationId = "10000000-0000-4000-8000-000000000003"
    let invocation = prepared [ "operation"; "resolve"; operationId ]

    let admitted =
        Frames.admit invocation.Bytes
        |> Result.defaultWith (fun _ -> failtest "Resolve frame")

    Expect.equal
        (Api.id (Frames.endpoint admitted))
        Api.operationResolve.Token
        "Registry-selected resolve"

    Expect.equal
        (Api.effect (Frames.endpoint admitted))
        EndpointEffect.OperationalWrite
        "Resolve writes a fence only"

    Expect.equal
        (Frames.input admitted)
        (Api.operationResolve.Input.Write { OperationId = Guid.Parse operationId })
        "Caller supplies operation ID only"

let private localizedReplyRendering () =
    let path =
        System.IO.Path.Combine(
            ClaimCore.TestSupport.RepositoryRoot.find (),
            "tests/fixtures/wire-response-examples.json"
        )

    use examples = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllBytes path)

    for example in examples.RootElement.GetProperty("cases").EnumerateArray() do
        if example.GetProperty("valid").GetBoolean() then
            let reply = ClaimCore.TestSupport.PositiveExamples.value example

            let frame =
                {
                    Endpoint =
                        Some(
                            example.GetProperty("endpoint").GetString()
                            |> nonNull
                            |> ClaimCore.Tests.CliReplies.endpointId
                        )
                    OperationId = None
                    Delivery = DeliveryState.Delivered
                    Result =
                        FrameResult.Reply(
                            ServiceReply.read
                                (Api.tryFind (
                                    example.GetProperty("endpoint").GetString() |> nonNull
                                 )
                                 |> Option.get)
                                reply
                            |> Result.defaultWith (fun _ -> failtest "Frozen DTO")
                        )
                }

            let expected =
                JsonValue.Object
                    [
                        "endpoint", JsonValue.String(EndpointIds.token frame.Endpoint.Value)
                        "operationId", JsonValue.Null
                        "delivery", JsonValue.String "DELIVERED"
                        "result", reply
                    ]
                |> WireWriter.bytes

            Expect.equal
                (Frames.encode frame)
                expected
                "Typed admission preserves the authored JSON member order and byte representation"

            for language in LocalizationPolicy.languages do
                let rendered =
                    ClaimCore.Cli.TextReply.bytes language frame |> Encoding.UTF8.GetString

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
            testCase
                "operation resolution keeps explicit identifiers and prior uncertainty"
                resolveVerb
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
                "--principal-kind"
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
    let grants present : AuthoredGrant list =
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
                (Previewed.Reviewed >> Outcome.Succeeded) (
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
            Endpoint = Some(EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Preview))
            OperationId = Some response.OperationId
            Delivery = DeliveryState.Delivered
            Result =
                FrameResult.Reply(ClaimCore.Tests.CliReplies.ofValue Api.operationPreview response)
        }

    frame

let private authorityPreviewText verb enabledBefore enabledAfter grantBefore grantAfter =
    let invocation = authorityPreviewInvocation verb
    let frame = authorityPreviewFrame enabledBefore enabledAfter grantBefore grantAfter

    for language in LocalizationPolicy.languages do
        let rendered =
            ClaimCore.Cli.TextReply.bytesFor language (Some invocation) frame
            |> Encoding.UTF8.GetString

        let labels, human, reader, installation =
            match language with
            | "en" ->
                [
                    "Register actor"
                    "Enable actor"
                    "Disable actor"
                    "Grant role"
                    "Revoke role"
                ],
                "Human",
                "Case reader",
                "Installation"
            | "lv" ->
                [
                    "Reģistrēt dalībnieku"
                    "Iespējot dalībnieku"
                    "Atspējot dalībnieku"
                    "Piešķirt lomu"
                    "Atsaukt lomu"
                ],
                "Cilvēks",
                "Lietu lasītājs",
                "Instalācija"
            | "ar" ->
                [ "تسجيل جهة"; "تفعيل الجهة"; "تعطيل الجهة"; "منح دور"; "سحب دور" ],
                "شخص",
                "قارئ الحالات",
                "التثبيت"
            | _ -> failtest "Every supported language needs independently authored labels"

        let act =
            List.zip [ "register"; "enable"; "disable"; "grant"; "revoke" ] labels
            |> Map.ofList
            |> Map.find verb

        Expect.stringContains
            rendered
            (act + " " + human + " synthetic synthetic-subject")
            "Authority preview identifies the exact act and principal using core labels"

        if enabledBefore <> enabledAfter then
            let expected =
                (if enabledBefore then "true" else "false")
                + " → "
                + (if enabledAfter then "true" else "false")

            Expect.stringContains rendered expected "The enabled transition is displayed"

        if grantBefore <> grantAfter then
            let expected =
                if grantBefore then
                    reader + "  " + installation + "  — → —"
                else
                    "— → " + reader + "  " + installation + "  —"

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
