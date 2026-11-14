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
            "schema", CliTopic.Help HelpTopic.Schema
            "call", CliTopic.Help HelpTopic.Call
            "session", CliTopic.Help HelpTopic.Session
        ] do
        for alias in [ "--help"; "-h" ] do
            Expect.equal
                (CliInvocations.admit [ command; alias ])
                (Ok(CliInvocation.Help(topic, "en", false)))
                "Offline exact aliases"

            Expect.isError
                (CliInvocations.admit [ command; alias; "--language"; "lv" ])
                "Extra operands never dispatch"

    for language in LocalizationPolicy.languages do
        for topic in CliTopic.all do
            Expect.equal
                (CliInvocations.admit [ "help"; CliTopic.token topic; "--language"; language ])
                (Ok(CliInvocation.Help(topic, language, false)))
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
            [ "help"; "--language"; "unknown" ]
            [ "schema"; "invocation"; "--language"; "en" ]
            [ "help"; "call"; "--language"; "en"; "--language"; "en" ]
            [ "help"; "secret-canary" ]
            [ "help"; "secret-canary" ]
            [ "help"; "summary"; "extra" ]
            [ "help"; "acts"; "case.open"; "extra" ]
            [ "schema"; "context"; "extra" ]
            [ "help"; "--format"; "text"; "--format"; "text" ]
        ] do
        Expect.isError (CliInvocations.admit args) "Unsupported combinations refuse atomically"

    Expect.equal
        (CliInvocations.admit [ "help"; "case"; "open"; "--format"; "text"; "--language"; "ar" ])
        (Ok(CliInvocation.CommandHelp([ "case"; "open" ], "ar", false)))
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

    let actual =
        Grammar.prepareOffline (args @ [ "--dry-run" ])
        |> Result.defaultWith (fun errors -> failtestf "Offline preparation: %A" errors)
        |> Option.defaultWith (fun () -> failtest "An offline operation draft is required")

    Expect.equal actual.Format VerbFormat.Text "Human acts default to text"

    let execution =
        Frames.admit actual.Bytes
        |> Result.defaultWith (fun _ -> failtest "Explicit raw-frame admission")

    Expect.isTrue
        (Frames.isProvisional execution)
        "Explicit submission acquires a provisional identity"

    Expect.isTrue
        (Frames.reportsIdentity execution)
        "The allocated identity is reported to the caller"

    let raw =
        StrictJson.read Limits.MaximumInvocationBytes actual.Bytes
        |> Result.defaultWith (fun error -> failtestf "Draft JSON: %A" error)

    let input = JsonValue.tryMember "input" raw |> Option.get
    Expect.isNone (JsonValue.tryMember "operationId" input) "Offline drafting allocates no identity"

    Expect.equal
        (JsonValue.tryMember "target" input)
        (Some(
            JsonValue.Object
                [
                    "kind", JsonValue.String "REFERENCE"
                    "caseReference", JsonValue.String "SYNTHETIC"
                ]
        ))
        "Authored target is retained"

    Expect.equal
        (JsonValue.tryMember "expectedRevision" input)
        (Some(JsonValue.Integer 1L))
        "Authored revision remains exact"

    Expect.equal
        (JsonValue.tryMember "act" input)
        (Some(JsonValue.Object [ "kind", JsonValue.String "case.close" ]))
        "Authored act remains exact"

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
    |> List.filter (fun operation -> (TaskRouting.act operation).IsNone)
    |> List.map (fun operation ->
        ClaimCore.Tests.CliExamples.queryArguments operation,
        ActorEndpoint.token (TaskRouting.endpoint operation))

let private assertNonLedgerVerbCoverage registry mapped =
    // Session routes are browser transport; preview is selected by an act option. Retired human history/activity verbs remain explicit API calls.
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
    for endpoint in Api.all do
        let samples = ClaimCore.TestSupport.ResponseExamples.samples endpoint.Token

        for encoded in
            samples
            |> List.filter (fun bytes ->
                let value =
                    StrictJson.read Limits.MaximumResponseBytes bytes
                    |> Result.defaultWith (string >> failtest)

                JsonValue.tryMember "representation" value <> Some(JsonValue.String "DATA")) do
            let reply =
                StrictJson.read Limits.MaximumResponseBytes encoded
                |> Result.defaultWith (string >> failtest)

            let frame =
                {
                    Endpoint = Some endpoint.Id
                    OperationId = None
                    Delivery = DeliveryState.Delivered
                    Result =
                        FrameResult.Reply(
                            ServiceReply.read endpoint reply
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
                "Typed frames preserve the provided JSON member order and bytes"

            for language in LocalizationPolicy.languages do
                let rendered =
                    ClaimCore.Cli.ReplyText.bytes language frame |> Encoding.UTF8.GetString

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

let private peoplePreviewInvocation verb =
    let role =
        if List.contains verb [ "grant"; "revoke" ] then
            [ "--role"; "CASE_READER" ]
        else
            []

    let prepared =
        prepared (
            [
                "people"
                verb
                "--principal-kind"
                "HUMAN"
                "--issuer"
                "https://issuer.example/"
                "--subject"
                "synthetic-subject"
                "--preview"
            ]
            @ role
            @ (if verb = "register" then [ "--name"; "Authored" ] else [])
        )

    Frames.admit prepared.Bytes |> Result.defaultWith (fun _ -> failtest "Prepared")


let private peoplePreviewFrame input enabledBefore enabledAfter grantBefore grantAfter =
    let request =
        match input with
        | OperationInput.People request -> request
        | _ -> failtest "People input required"

    let registering, profile =
        match request.Act with
        | PeopleAct.Register profile -> true, (ActorProfile.parse profile |> Fixtures.accepted)
        | _ ->
            false,
            (ActorProfile.parse
                {
                    DisplayName = "Synthetic account"
                    Contact = None
                }
             |> Fixtures.accepted)

    let principal =
        PrincipalKey.ofKind request.Target.Kind request.Target.Issuer request.Target.Subject
        |> Fixtures.accepted

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
                    OperationReview.People
                        {
                            Basis = ClaimCore.TestSupport.Inputs.workBasis
                            Effects =
                                (if registering then
                                     [
                                         PeopleReviewEffect.Register principal
                                         PeopleReviewEffect.SetProfile profile
                                     ]
                                 elif enabledBefore <> enabledAfter then
                                     [
                                         if enabledAfter then
                                             PeopleReviewEffect.Enable
                                         else
                                             PeopleReviewEffect.Disable
                                     ]
                                 elif grantBefore <> grantAfter then
                                     [
                                         if grantAfter then
                                             PeopleReviewEffect.Grant
                                                 {
                                                     Role = Role.CaseReader
                                                     Scope = GrantScope.Installation
                                                 }
                                         else
                                             PeopleReviewEffect.Revoke
                                                 {
                                                     Role = Role.CaseReader
                                                     Scope = GrantScope.Installation
                                                 }
                                     ]
                                 else
                                     [
                                         if enabledAfter then
                                             PeopleReviewEffect.Enable
                                         else
                                             PeopleReviewEffect.Disable
                                     ])
                            Target =
                                if registering then
                                    None
                                else
                                    Some(
                                        ActorReviewItem.ofActor
                                            []
                                            {
                                                ActorId =
                                                    CliReplyFixtures.receipt.RecordedBy.ActorId
                                                Principal = principal
                                                Enabled = enabledBefore
                                                Profile = Some profile
                                            }
                                    )
                            AccessScope = GrantScope.Installation
                            Before =
                                {
                                    Enabled = enabledBefore
                                    Profile = if registering then None else Some profile
                                    Access = []
                                    Grants = grants grantBefore
                                }
                            After =
                                {
                                    Enabled = enabledAfter
                                    Profile = Some profile
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
            Result = FrameResult.Reply(ClaimCore.Tests.CliReplies.humanPreview input response)
        }

    frame

let private peoplePreviewText verb enabledBefore enabledAfter grantBefore grantAfter =
    let invocation = peoplePreviewInvocation verb

    let input =
        (Frames.value Api.operationPreview
         >> Option.map ClaimCore.TestSupport.Submissions.command)
            invocation
        |> Option.defaultWith (fun () -> failtest "Preview input required")

    let frame =
        peoplePreviewFrame input enabledBefore enabledAfter grantBefore grantAfter

    for language in LocalizationPolicy.languages do
        let rendered =
            ClaimCore.Cli.ReplyText.bytesFor language (Some invocation) frame
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
                "Person",
                "Case reader",
                "All cases"
            | "lv" ->
                [
                    "Reģistrēt kontu"
                    "Iespējot kontu"
                    "Atspējot kontu"
                    "Piešķirt lomu"
                    "Atsaukt lomu"
                ],
                "Persona",
                "Lietu lasītājs",
                "Visas lietas"
            | "ar" ->
                [ "تسجيل حساب"; "تمكين الحساب"; "تعطيل الحساب"; "منح دور"; "سحب دور" ],
                "شخص",
                "قارئ الملفات",
                "جميع الملفات"
            | "ko" ->
                [ "계정 등록"; "계정 활성화"; "계정 비활성화"; "역할 부여"; "역할 회수" ], "사람", "청구 건 조회자", "모든 청구 건"
            | "ja" ->
                [ "アカウントを登録"; "アカウントを有効化"; "アカウントを無効化"; "役割を付与"; "役割を取り消す" ],
                "個人",
                "案件閲覧者",
                "すべての案件"
            | "zh-Hans" -> [ "注册账户"; "启用账户"; "停用账户"; "授予角色"; "撤销角色" ], "人员", "案件查阅员", "所有案件"
            | "zh-Hant" -> [ "註冊帳戶"; "啟用帳戶"; "停用帳戶"; "授予角色"; "撤銷角色" ], "人員", "案件查閱員", "所有案件"
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
                "Todos los expedientes"
            | "de" ->
                [
                    "Konto registrieren"
                    "Konto aktivieren"
                    "Konto deaktivieren"
                    "Rolle zuweisen"
                    "Rolle entziehen"
                ],
                "Person",
                "Lesezugriff",
                "Alle Fälle"
            | _ -> failtest "Every supported language needs independently authored labels"

        let act =
            List.zip [ "register"; "enable"; "disable"; "grant"; "revoke" ] labels
            |> Map.ofList
            |> Map.find verb

        Expect.stringContains rendered act "Core review identifies the exact authored action"

        if verb = "register" then
            let details =
                ReplyText.bytesForWithDetails true language (Some invocation) frame
                |> Encoding.UTF8.GetString

            for expected in [ human; "https://issuer.example/"; "synthetic-subject" ] do
                Expect.stringContains
                    details
                    expected
                    "Core principal details retain the exact registration target"
        else
            Expect.stringContains
                rendered
                "Synthetic account"
                "Core target caption survives without client reconstruction"

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
let peopleTextTests =
    testList
        "CLI people preview text"
        [
            testCase "register" (fun () -> peoplePreviewText "register" false true false false)
            testCase "enable" (fun () -> peoplePreviewText "enable" false true false false)
            testCase "disable" (fun () -> peoplePreviewText "disable" true false false false)
            testCase "grant" (fun () -> peoplePreviewText "grant" true true false true)
            testCase "revoke" (fun () -> peoplePreviewText "revoke" true true true false)
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
