module ClaimCore.Tests.CliDiagnosticTextTests

open ClaimCore.Wire
open ClaimCore.Presentation

open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Cli
open ClaimCore.TestSupport

let private frame refusal =
    {
        Endpoint = Some(EndpointId.Actor(ActorEndpoint.Query QueryTaskId.CaseGet))
        OperationId = None
        Delivery = DeliveryState.Delivered
        Result =
            FrameResult.Reply(
                ClaimCore.Tests.CliReplies.ofValue
                    Api.caseGet
                    (HumanReplies.read HumanCaseDocument.ofCore (Outcome.Rejected refusal)
                     |> RepresentationReply.Human)
            )
    }

let private assertTarget target identity violation code parameters =
    let refusal = Refusal.invalidInput (target, violation)
    let source = Frames.encode (frame refusal)
    let original = Array.copy source

    let parameters = Map.ofList parameters

    for language in LocalizationPolicy.languages do
        let rendered = ReplyText.bytes language (frame refusal) |> Encoding.UTF8.GetString
        let label = MessageCatalogs.render language identity Map.empty

        let message = MessageCatalogs.render language ("diagnostic." + code) parameters

        Expect.equal
            (rendered.Split('\n')[1])
            (label + ": " + message)
            "The exact localized target distinguishes the affected input"

        Expect.equal source original "Human rendering preserves the structured reply bytes"

        Expect.equal
            (Frames.decode source |> Result.map CliReplies.frameFacts)
            (Ok(CliReplies.frameFacts (frame refusal)))
            "Structured diagnostic target and typed parameters remain unchanged"

let private requiredNames () =
    for field, identity in
        [
            BusinessField.ClaimantName, "field.claimantName.label"
            BusinessField.InsurerName, "field.insurerName.label"
        ] do
        assertTarget (InputTarget.Field field) identity Violation.TextRequired "TEXT_REQUIRED" []

let private dates () =
    for field, identity in
        [
            BusinessField.IncidentDate, "field.incidentDate.label"
            BusinessField.IncidentNotificationDate, "field.incidentNotificationDate.label"
        ] do
        assertTarget
            (InputTarget.Field field)
            identity
            Violation.CalendarDateRequired
            "CALENDAR_DATE_REQUIRED"
            []

let private principalAndPayment () =
    assertTarget
        InputTarget.PrincipalIssuer
        "field.principal.issuer.label"
        Violation.PrincipalFormat
        "PRINCIPAL_FORMAT"
        []

    assertTarget
        InputTarget.PrincipalSubject
        "field.principal.subject.label"
        Violation.TextRequired
        "TEXT_REQUIRED"
        []


let private remainingTargets () =
    assertTarget
        InputTarget.OperationId
        "field.operationId.label"
        Violation.OperationIdRequired
        "OPERATION_ID_REQUIRED"
        []

    assertTarget
        InputTarget.ExpectedRevision
        "field.expectedRevision.label"
        Violation.ExpectedRevisionRange
        "EXPECTED_REVISION_RANGE"
        []

    assertTarget InputTarget.Reason "field.reason.label" Violation.TextRequired "TEXT_REQUIRED" []

    assertTarget
        InputTarget.Limit
        "field.limit.label"
        (Violation.PageLimitRange 50)
        "PAGE_LIMIT_RANGE"
        [ "maximumPageSize", "50" ]

    assertTarget
        (InputTarget.Field BusinessField.ClaimantName)
        "field.claimantName.label"
        (Violation.TooLong 200)
        "TOO_LONG"
        [ "maximumCharacters", "200" ]

let private assertOptionalTarget refusal identity code =
    let source = Frames.encode (frame refusal)

    for language in LocalizationPolicy.languages do
        let rendered = ReplyText.bytes language (frame refusal) |> Encoding.UTF8.GetString

        let message = MessageCatalogs.render language ("diagnostic." + code) Map.empty

        let expected =
            identity
            |> Option.map (fun key ->
                MessageCatalogs.render language key Map.empty + ": " + message)
            |> Option.defaultValue message

        Expect.equal
            (rendered.Split('\n')[0])
            expected
            "Only actual diagnostic targets add a localized label"

        Expect.equal
            (Frames.decode source |> Result.map CliReplies.frameFacts)
            (Ok(CliReplies.frameFacts (frame refusal)))
            "The structured diagnostic is unchanged"

[<Tests>]
let tests =
    testList
        "CLI diagnostic target text"
        [
            testCase
                "required claimant and insurer are distinguished in every language"
                requiredNames
            testCase "incident and notification dates are distinguished in every language" dates
            testCase
                "principal inputs and payment group use existing localized identities"
                principalAndPayment
            testCase
                "operation revision reason page and limit parameters retain their identity"
                remainingTargets
            testCase "cursor refusal uses its localized Core message" (fun () ->
                assertOptionalTarget Refusal.InvalidCursor None "INVALID_CURSOR")
            testCase "targetless refusal has no invented target" (fun () ->
                assertOptionalTarget
                    (Refusal.Access AccessRefusal.ResourceUnavailable)
                    None
                    "RESOURCE_UNAVAILABLE")
        ]

[<Tests>]
let accumulated =
    testCase "CLI prints every input issue in list order in every language" (fun () ->
        let values =
            [
                {
                    Target = InputTarget.Field BusinessField.ClaimantName
                    GrantRow = None
                    RelatedDate = None
                    Violation = Violation.TextRequired
                }
                {
                    Target = InputTarget.Field BusinessField.InsurerName
                    GrantRow = None
                    RelatedDate = None
                    Violation = Violation.TextRequired
                }
            ]

        let refusal = values |> InputIssues.ofList |> Option.get |> Refusal.InvalidInput

        for language in LocalizationPolicy.languages do
            let lines =
                ReplyText.bytes language (frame refusal)
                |> Encoding.UTF8.GetString
                |> fun text -> text.Split('\n')

            let message = MessageCatalogs.render language "diagnostic.TEXT_REQUIRED" Map.empty

            for index, field in
                [ BusinessField.ClaimantName; BusinessField.InsurerName ] |> List.indexed do
                let label =
                    MessageCatalogs.render language (FieldPresentation.labelId field) Map.empty

                Expect.equal
                    lines[index + 1]
                    (label + ": " + message)
                    "Each issue has its own localized line")


[<Tests>]
let nextAndConflicts =
    testList
        "CLI actionable refusal presentation"
        [
            testCase
                "recommended command arguments round trip through the actual shell grammar"
                (fun () ->
                    for raw in [ "R-1"; "R'Q"; "R$Q"; "R\\Q"; "R;Q"; "a b"; "" ] do
                        Expect.equal
                            (ShellTokenizer.split (CommandGrammar.quoteArgument raw))
                            (Ok [ raw ])
                            "The suggested argument remains literal")
            testCase
                "no-action refusals omit action tokens and access refusals name roles"
                (fun () ->
                    for language in LocalizationPolicy.languages do
                        let unchanged =
                            DiagnosticText.refusalLines
                                language
                                (Refusal.Case CaseRefusal.CorrectionNoChanges)
                            |> String.concat "\n"

                        Expect.isFalse
                            (unchanged.Contains "NONE_REQUIRED" || unchanged.Contains "Action:")
                            "Do not print internal next-action tokens"

                        let access =
                            DiagnosticText.refusalLines
                                language
                                (Refusal.Access(AccessRefusal.NotAuthorized Capability.Steward))
                            |> String.concat "\n"

                        Expect.stringContains
                            access
                            (MessageCatalogs.render language "role.DATA_STEWARD.label" Map.empty)
                            "The exact policy-required role is named"

                        Expect.isFalse
                            (access.Contains "ASK_FOR_ACCESS" || access.Contains "cli.next.")
                            "The next step is localized prose")
            testCase
                "conflict retains fifty authors and continuation while details are opt-in"
                (fun () ->
                    let entries =
                        [
                            for number in 1..50 ->
                                {
                                    CaseTimelineEntry.OperationId =
                                        CaseFixtures.operationId (
                                            System.Guid.Parse(
                                                sprintf "5b359a73-77f5-7a54-9015-%012d" number
                                            )
                                        )
                                    Position =
                                        EffectPosition.create
                                            (CaseFixtures.sequence (int64 number))
                                            0
                                        |> Option.get
                                    OperationHash = CliReplyFixtures.receipt.Position.EntryHash
                                    Subject =
                                        TimelineSubject.CaseFact(
                                            CaseFixtures.revision (int64 number),
                                            CaseActKind.AmendRegistration
                                        )
                                    RecordedAt = ClaimCore.TestSupport.Inputs.workBasis.ObservedAt
                                    RecordedBy =
                                        { CliReplyFixtures.receipt.RecordedBy with
                                            DisplayName =
                                                DisplayName.Named(
                                                    ClaimCore.TestSupport.Inputs.name (
                                                        sprintf "Editor %d" number
                                                    )
                                                )
                                        }
                                    Changes =
                                        if number = 1 then
                                            TimelineChanges.Withheld
                                        elif number = 2 then
                                            TimelineChanges.Erased
                                        else
                                            TimelineChanges.Disclosed
                                                {
                                                    Items =
                                                        [
                                                            TimelineChange.HandlingSummary
                                                                {
                                                                    Before = None
                                                                    After =
                                                                        Some(
                                                                            HandlingSummary.parse (
                                                                                sprintf
                                                                                    "Change %d"
                                                                                    number
                                                                            )
                                                                            |> Fixtures.accepted
                                                                        )
                                                                }
                                                        ]
                                                    Overview =
                                                        {
                                                            Event =
                                                                TimelineEvent.CaseActRecorded
                                                                    CaseActKind.AmendRegistration
                                                            Highlights = []
                                                        }
                                                }
                                }
                        ]

                    let refusal =
                        Refusal.RevisionConflict(
                            RevisionConflictInfo.at (CaseFixtures.revision 50L)
                            |> RevisionConflictInfo.explain (
                                ConflictChanges.Page
                                    {
                                        Basis =
                                            { ClaimCore.TestSupport.Inputs.workBasis with
                                                TimeZoneId = "Europe/Riga"
                                            }
                                        Disclosure = HistoryDisclosure.Full
                                        Entries = entries
                                        NextCursor = Some "literal-cursor"
                                    }
                            )
                        )

                    let admitted =
                        CliReplies.humanExecute
                            {
                                ExecuteReply.OperationId =
                                    System.Guid.Parse "10000000-0000-7000-8000-000000000001"
                                Outcome = ExecuteResult.Rejected refusal
                            }

                    for details in [ false; true ] do
                        let text =
                            ReplyText.resultLinesWithDetails
                                details
                                "en"
                                None
                                (FrameResult.Reply admitted)
                            |> String.concat "\n"

                        Expect.stringContains text "Editor 50" "Every supplied author is visible"

                        // 2026-08-31 12:00 UTC is 15:00 in Riga summer time.
                        Expect.stringContains
                            text
                            "15:00 +03:00"
                            "Conflict instants use the installation zone from the conflict basis"

                        Expect.isFalse
                            (text.Contains "+00:00")
                            "Conflict instants are never shown in UTC when the zone is known"

                        Expect.equal
                            (text.Contains "Change 50")
                            details
                            "Full differences follow the explicit details selection"

                        Expect.equal
                            (text.Contains(OperationId.format entries.Head.OperationId))
                            details
                            "Technical conflict identities follow the same details selection"

                    let supplied =
                        ScreenEntries.factScreen
                            [
                                {
                                    TitleId = "label.changes"
                                    Entries =
                                        [
                                            {
                                                Key = "future-server-change"
                                                LabelId = "label.after"
                                                Prominence = Prominence.Primary
                                                Value =
                                                    PresentedValue.Text
                                                        "SUPPLIED CLI CONFLICT VIEW"
                                            }
                                        ]
                                }
                            ]

                    let rec replace =
                        function
                        | ClaimCore.Json.JsonValue.Object fields ->
                            fields
                            |> List.map (fun (key, value) ->
                                key,
                                if key = "screen" then
                                    PresentationCodecs.view.Write supplied
                                else
                                    replace value)
                            |> ClaimCore.Json.JsonValue.Object
                        | other -> other

                    let custom =
                        ServiceReply.read admitted.Route (replace admitted.Wire)
                        |> Fixtures.accepted

                    let customText =
                        ReplyText.resultLines "en" None (FrameResult.Reply custom)
                        |> String.concat "\n"

                    Expect.stringContains
                        customText
                        "SUPPLIED CLI CONFLICT VIEW"
                        "CLI consumes the supplied Screen without reconstructing business fields."

                    Expect.isFalse
                        (customText.Contains "Change 50")
                        "Typed diagnostic data cannot replace supplied presentation."

                    for language in LocalizationPolicy.languages do
                        let output =
                            DiagnosticText.refusalLinesWithView
                                true
                                (Some(TimelineScreen.rows entries))
                                language
                                (Some "CASE-1")
                                refusal
                            |> String.concat "\n"

                        Expect.stringContains output "Editor 1" "First supplied author"

                        Expect.stringContains
                            output
                            "Editor 50"
                            "Last supplied author is not dropped"

                        Expect.stringContains output "Change 50" "Last supplied difference"

                        Expect.stringContains
                            output
                            "claimcore case timeline CASE-1 --cursor literal-cursor"
                            "Continuation retains the core range cursor"

                        Expect.stringContains
                            output
                            "claimcore case get CASE-1"
                            "The next step uses the actual target"

                        for key in [ "label.changesWithheld"; "label.changesErased" ] do
                            Expect.stringContains
                                output
                                (MessageCatalogs.render language key Map.empty)
                                "Disclosure alternatives remain distinct"

                        Expect.isFalse
                            (output.Contains "REVIEW_LATEST" || output.Contains "{\"")
                            "No business tokens or JSON leak into prose")
        ]

[<Tests>]
let hostFailures =
    testCase "host version and installation failures use CLI advice, not browser advice" (fun () ->
        let text language code =
            ReplyText.resultLines
                language
                None
                (FrameResult.HostFailure { Code = code; Phase = None })
            |> String.concat "\n"

        Expect.stringContains
            (text "en" HostFailureCode.ContractMismatch)
            "Use the CLI from the same ClaimCore release as the service."
            "English contract mismatch states the CLI's next step"

        for language in LocalizationPolicy.languages do
            for code, token in
                [
                    HostFailureCode.ContractMismatch, "CONTRACT_MISMATCH"
                    HostFailureCode.InstallationMismatch, "INSTALLATION_MISMATCH"
                ] do
                let rendered = text language code
                let browser = ReplyTextFormat.label language ("host." + token)

                Expect.isFalse
                    (rendered.Contains browser)
                    $"{language} {token} does not show the browser reload advice"

                Expect.stringContains rendered token $"{language} {token} keeps its code")
