module ClaimCore.Tests.CliInvocationTests

open ClaimCore.Cli

open System
open System.Text
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Wire
open ClaimCore.Presentation
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
    Grammar.prepare (fun () -> "10000000-0000-7000-8000-000000000001") arguments
    |> Result.defaultWith (fun issues -> failtestf "Request preparation failed: %A" issues)

let private verbPreparation () =
    let args = [ "case"; "close"; "SYNTHETIC"; "--expected-revision"; "1" ]
    let actual = prepared (args @ [ "--dry-run" ])
    Expect.isTrue actual.DryRun "Dry run is an offline preparation"
    Expect.equal actual.Format VerbFormat.Text "Human acts default to text"

    let frame =
        Frames.admit actual.Bytes |> Result.defaultWith (fun _ -> failtest "Frame")

    let raw =
        StrictJson.read Limits.MaximumInvocationBytes actual.Bytes
        |> Result.defaultWith (fun _ -> failtest "Dry-run JSON")

    Expect.isNone
        (JsonValue.tryMember "input" raw
         |> Option.bind (JsonValue.tryMember "operationId"))
        "An ID-less dry run allocates no executable identity"

    match Frames.value Api.operationExecute frame with
    | Some(OperationInput.Case request) ->
        Expect.equal
            request.Target
            (CaseSelector.Reference "SYNTHETIC")
            "Authored target is retained"

        Expect.equal request.ExpectedRevision 1L "Authored revision is retained"
        Expect.equal request.Act (AuthoredCaseAct.Bare BareAct.Close) "Authored act is retained"
    | _ -> failtest "Expected a case execution frame"

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
    CommandGrammar.operations
    |> List.filter (fun operation -> operation.SelectedAct.IsNone)
    |> List.map (fun operation ->
        ClaimCore.Tests.CliExamples.queryArguments operation,
        ActorEndpoint.token operation.Endpoint)

let private assertNonLedgerVerbCoverage registry mapped =
    // Session routes are browser transport; preview is selected by an act option. Retired human history/activity verbs remain explicit API calls.
    let excluded =
        Set.ofList
            [
                "session"
                "session.logout"
                "operation.preview"
                "case.history"
                "case.activity"
            ]

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

let private lookupVerb () =
    let operationId = "10000000-0000-7000-8000-000000000003"
    let invocation = prepared [ "operation"; "get"; operationId ]

    let admitted =
        Frames.admit invocation.Bytes
        |> Result.defaultWith (fun _ -> failtest "Lookup frame")

    Expect.equal
        (Api.id (Frames.endpoint admitted))
        Api.operationGet.Token
        "Registry-selected lookup"

    Expect.equal
        (Api.effect (Frames.endpoint admitted))
        EndpointEffect.Read
        "Lookup reads without appending"

    Expect.equal
        (Frames.input admitted)
        (Api.operationGet.Input.Write { OperationId = Guid.Parse operationId })
        "Caller supplies operation ID only"

let private localizedReplyRendering () =
    let path =
        System.IO.Path.Combine(
            ClaimCore.TestSupport.RepositoryRoot.find (),
            "tests/fixtures/examples/responses.json"
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

                Expect.isFalse
                    (rendered.Contains "{\"" || rendered.Contains "[{\"")
                    "Human replies never fall back to serialized business objects"

                Expect.isFalse
                    (System.Text.RegularExpressions.Regex.IsMatch(
                        rendered,
                        @"(?:^|\s|:)(?:label|field|act|role|capability|view|headline|caseStage|requesterState|accountState|cli)\.[A-Za-z0-9_.]+"
                    ))
                    "Every human descriptor uses translated labels and sentences"

[<Tests>]
let renderingTests =
    testList
        "CLI verb rendering"
        [
            testCase "every read and operational write has a registry-selected verb" nonLedgerVerbs
            testCase
                "operation resolution keeps explicit identifiers and prior uncertainty"
                lookupVerb
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
            @ (if verb = "register" then [ "--name"; "Authored" ] else [])
        )

    Frames.admit prepared.Bytes |> Result.defaultWith (fun _ -> failtest "Prepared")


let private authorityPreviewFrame enabledBefore enabledAfter grantBefore grantAfter =
    let grants present : ScopedGrant list =
        if present then
            [
                {
                    Role = Role.CaseReader
                    Scope = GrantScope.Installation
                }
            ]
        else
            []

    let response =
        {
            PreviewReply.OperationId = Guid.Parse "10000000-0000-7000-8000-000000000001"
            Outcome =
                (Previewed.Reviewed >> Outcome.Succeeded) (
                    OperationReview.Authority
                        {
                            Basis = ClaimCore.TestSupport.Inputs.workBasis
                            Effects =
                                (if enabledBefore <> enabledAfter then
                                     [
                                         if enabledAfter then
                                             AuthorityReviewEffect.Enable
                                         else
                                             AuthorityReviewEffect.Disable
                                     ]
                                 elif grantBefore <> grantAfter then
                                     [
                                         if grantAfter then
                                             AuthorityReviewEffect.Grant
                                                 {
                                                     Role = Role.CaseReader
                                                     Scope = GrantScope.Installation
                                                 }
                                         else
                                             AuthorityReviewEffect.Revoke
                                                 {
                                                     Role = Role.CaseReader
                                                     Scope = GrantScope.Installation
                                                 }
                                     ]
                                 else
                                     [
                                         if enabledAfter then
                                             AuthorityReviewEffect.Enable
                                         else
                                             AuthorityReviewEffect.Disable
                                     ])
                            Target = None
                            AccessScope = GrantScope.Installation
                            Before =
                                {
                                    Enabled = enabledBefore
                                    Profile = None
                                    Access = []
                                    Grants = grants grantBefore
                                }
                            After =
                                {
                                    Enabled = enabledAfter
                                    Profile = None
                                    Access = []
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
                FrameResult.Reply(
                    ClaimCore.Tests.CliReplies.ofValue
                        Api.operationPreview
                        (PresentedPreviewReply.ofCore response)
                )
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
                    "Register account"
                    "Enable account"
                    "Disable account"
                    "Grant role"
                    "Revoke role"
                ],
                "Human",
                "Case reader",
                "Installation"
            | "lv" ->
                [
                    "Reģistrēt kontu"
                    "Iespējot kontu"
                    "Atspējot kontu"
                    "Piešķirt lomu"
                    "Atsaukt lomu"
                ],
                "Cilvēks",
                "Lietu lasītājs",
                "Instalācija"
            | "ar" ->
                [ "تسجيل حساب"; "تمكين الحساب"; "تعطيل الحساب"; "منح دور"; "سحب دور" ],
                "شخص",
                "قارئ الملفات",
                "التثبيت"
            | "ko" -> [ "계정 등록"; "계정 활성화"; "계정 비활성화"; "역할 부여"; "역할 회수" ], "사람", "건 조회자", "설치 환경"
            | "ja" ->
                [ "アカウントを登録"; "アカウントを有効化"; "アカウントを無効化"; "役割を付与"; "役割を取り消す" ], "人", "案件閲覧者", "設置環境"
            | "zh-Hans" -> [ "注册账户"; "启用账户"; "停用账户"; "授予角色"; "撤销角色" ], "自然人", "档案查阅员", "安装实例"
            | "zh-Hant" -> [ "註冊帳戶"; "啟用帳戶"; "停用帳戶"; "授予角色"; "撤銷角色" ], "自然人", "檔案查閱員", "安裝實例"
            | "es" ->
                [
                    "Registrar una cuenta"
                    "Activar la cuenta"
                    "Desactivar la cuenta"
                    "Asignar un rol"
                    "Revocar un rol"
                ],
                "Persona",
                "Lector de expedientes",
                "Instalación"
            | "de" ->
                [
                    "Konto registrieren"
                    "Konto aktivieren"
                    "Konto deaktivieren"
                    "Rolle zuweisen"
                    "Rolle entziehen"
                ],
                "Mensch",
                "Fallleser",
                "Installation"
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
            let yes, no =
                match language with
                | "en" -> "Yes", "No"
                | "lv" -> "Jā", "Nē"
                | "ar" -> "نعم", "لا"
                | "ko" -> "예", "아니요"
                | "ja" -> "はい", "いいえ"
                | "zh-Hans"
                | "zh-Hant" -> "是", "否"
                | "es" -> "Sí", "No"
                | "de" -> "Ja", "Nein"
                | _ -> failtest "Every supported language needs independent boolean labels"

            let expected =
                (if enabledBefore then yes else no) + " → " + (if enabledAfter then yes else no)

            Expect.stringContains rendered expected "The enabled transition is displayed"

        if grantBefore <> grantAfter then
            let absent = MessageCatalogs.render language "label.notRecorded" Map.empty

            let expected role =
                if grantBefore then
                    role + " → " + absent
                else
                    absent + " → " + role

            Expect.stringContains
                rendered
                (expected reader)
                "The role is displayed in the correct direction"

            Expect.stringContains
                rendered
                (expected installation)
                "The scope is displayed in the correct direction"

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

[<Tests>]
let recoveryCommands =
    testList
        "CLI unconfirmed changes"
        [
            testCase
                "an invalid explicit execute ID cannot become an unconfirmed journal entry"
                (fun () ->
                    let input =
                        Frames.input
                            (prepared [ "case"; "close"; "SYNTHETIC"; "--expected-revision"; "1" ])
                                .Invocation

                    let fields =
                        match input with
                        | JsonValue.Object fields -> fields
                        | _ -> failtest "Object input"

                    let frame fields =
                        JsonValue.Object
                            [
                                "endpoint", JsonValue.String "operation.execute"
                                "input", JsonValue.Object fields
                            ]
                        |> WireWriter.bytes

                    let missing =
                        fields
                        |> List.filter (fst >> ((<>) "operationId"))
                        |> frame
                        |> Frames.admit

                    Expect.isOk missing "The CLI fills omitted execute identity"

                    let invalid =
                        fields
                        |> List.map (fun (key, value) ->
                            key,
                            if key = "operationId" then
                                Codec.uuid.Write(
                                    Guid.Parse "10000000-0000-4000-8000-000000000001"
                                )
                            else
                                value)
                        |> frame
                        |> Frames.admit

                    Expect.isError invalid "UUIDv4 cannot be journaled or dispatched as a change")
            testCase "list and check admit explicit scope-independent attempt identity" (fun () ->
                let attempt = System.Guid.Parse "10000000-0000-7000-8000-000000000003"

                for arguments, action in
                    [
                        [ "recovery"; "list" ], RecoveryAction.List
                        [ "recovery"; "check"; string attempt ],
                        RecoveryAction.Attempt(RecoveryAttemptAction.Check, attempt)
                    ] do
                    match CliInvocations.admit arguments with
                    | Ok(CliInvocation.Recovery request) ->
                        Expect.equal request.Action action "Command meaning"
                    | _ -> failtest "Expected a recovery command"

                for verb in [ "settle"; "resolve" ] do
                    Expect.isError
                        (CliInvocations.admit [ "recovery"; verb; string attempt ])
                        "Removed verbs have no aliases"

                let lookup = prepared [ "operation"; "get"; string attempt ]

                Expect.equal
                    (Frames.endpoint lookup.Invocation).Id
                    Api.operationGet.Id
                    "Current lookup endpoint"

                Expect.equal
                    (Api.effect (Frames.endpoint lookup.Invocation))
                    EndpointEffect.Read
                    "Lookup appends nothing"

                Expect.isTrue
                    (Api.requiresInstallation (Frames.endpoint lookup.Invocation))
                    "Lookup remains installation bound")
        ]
