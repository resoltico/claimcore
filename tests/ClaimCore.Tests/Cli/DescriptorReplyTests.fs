module ClaimCore.Tests.DescriptorReplyTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Cli
open ClaimCore.TestSupport

let private authorityReview () =
    let reply: PreviewReply =
        {
            OperationId = Guid.Parse "10000000-0000-7000-8000-000000000001"
            Outcome =
                Outcome.Succeeded(
                    Previewed.Reviewed(
                        OperationReview.Authority
                            {
                                Basis = Inputs.workBasis
                                Effects =
                                    [
                                        AuthorityReviewEffect.Grant
                                            {
                                                Role = Role.CaseReader
                                                Scope = GrantScope.Installation
                                            }
                                    ]
                                Target = None
                                AccessScope = GrantScope.Installation
                                Before =
                                    {
                                        Enabled = false
                                        Profile = None
                                        Grants = []
                                        Access = []
                                    }
                                After =
                                    {
                                        Enabled = true
                                        Profile = None
                                        Access = []
                                        Grants =
                                            [
                                                {
                                                    Role = Role.CaseReader
                                                    Scope = GrantScope.Installation
                                                }
                                            ]
                                    }
                            }
                    )
                )
        }

    CliReplies.humanPreview
        (OperationInput.Authority
            { CliReplyFixtures.authorityRequest with
                OperationId = reply.OperationId
                Act =
                    AuthorityAct.Grant
                        {
                            Role = Role.CaseReader
                            Scope = GrantTarget.Installation
                        }
            })
        reply

let rec private unknownAuthority =
    function
    | JsonValue.String "AUTHORITY" -> JsonValue.String "UNDECLARED"
    | JsonValue.Object fields ->
        fields
        |> List.map (fun (name, value) -> name, unknownAuthority value)
        |> JsonValue.Object
    | JsonValue.Array values -> values |> List.map unknownAuthority |> JsonValue.Array
    | value -> value

let private account =
    {
        ActorReviewItem.Actor =
            Inputs.actorReference (
                CaseFixtures.actorId (Guid.Parse "10000000-0000-7000-8000-000000000001")
            )
        Principal = PrincipalKey.human "https://fixture.example/" "person" |> Fixtures.accepted
        State = AccountState.Active
        Contact = None
        Roles =
            [
                {
                    Role = Role.CaseReader
                    Installation = true
                    CaseCount = 0L
                }
            ]
        ResponsibleCases =
            Some
                {
                    Count = 1L
                    ResponsibleActor =
                        CaseFixtures.actorId (Guid.Parse "10000000-0000-7000-8000-000000000001")
                }
    }

[<Tests>]
let tests =
    testList
        "typed reply presentation"
        [
            testCase "authority review uses the typed branch and localized grants" (fun () ->
                for language in [ "en"; "lv"; "ar" ] do
                    let lines =
                        ReplyText.resultLines language None (FrameResult.Reply(authorityReview ()))

                    let text = String.concat "\n" lines

                    let previous, next =
                        match language with
                        | "lv" -> "Nē", "Jā"
                        | "ar" -> "لا", "نعم"
                        | _ -> "No", "Yes"

                    Expect.stringContains
                        text
                        (previous + " → " + next)
                        "The actual state change remains visible"

                    Expect.isFalse
                        (text.Contains "kind:"
                         || text.Contains "CASE_READER"
                         || text.Contains "{\"")
                        "Human output contains neither wire tags nor JSON")
            testCase "unknown tagged output is rejected at the typed boundary" (fun () ->
                let original = authorityReview ()

                Expect.isError
                    (ServiceReply.read original.Route (unknownAuthority original.Wire))
                    "The codec rejects an unknown union branch before rendering")
            testCase
                "context and summary descriptors are localized in all human languages"
                (fun () ->
                    for language in [ "en"; "lv"; "ar" ] do
                        let render endpoint reply =
                            let typed = CliReplies.ofValue endpoint reply

                            ReplyText.resultLines language None (FrameResult.Reply typed)
                            |> String.concat "\n"

                        let outputs =
                            [
                                render Api.context (ResponseExamples.defaultHuman Api.context)
                                render
                                    Api.caseSummary
                                    (ResponseExamples.defaultHuman Api.caseSummary)
                                render
                                    Api.authorityList
                                    (HumanReplies.read
                                        HumanReadDocuments.actors
                                        (Outcome.Succeeded
                                            {
                                                Basis = Inputs.workBasis
                                                Items = [ account ]
                                                NextCursor = Some "opaque-cursor"
                                            })
                                     |> RepresentationReply.Human)
                                render
                                    Api.authorityGrants
                                    (HumanReplies.read
                                        HumanReadDocuments.grants
                                        (Outcome.Succeeded
                                            {
                                                Basis = Inputs.workBasis
                                                Actor = account
                                                Offers = []
                                                ProfileSeed = None
                                                Items =
                                                    [
                                                        {
                                                            Role = Role.CaseReader
                                                            Scope = GrantScope.Installation
                                                            GrantedAt = Inputs.workBasis.ObservedAt
                                                            GrantedBy =
                                                                ResponseExamples.caseRead
                                                                    .RecordedBy
                                                        }
                                                    ]
                                                NextCursor = None
                                            })
                                     |> RepresentationReply.Human)
                                render
                                    Api.authorityRequests
                                    (HumanReplies.read
                                        HumanReadDocuments.requests
                                        (Outcome.Succeeded
                                            {
                                                Basis = Inputs.workBasis
                                                Items =
                                                    [
                                                        {
                                                            Principal = account.Principal
                                                            FirstSeen = Inputs.workBasis.ObservedAt
                                                            LastSeen = Inputs.workBasis.ObservedAt
                                                            Claims =
                                                                RequesterClaims.admit
                                                                    (Some "Request name")
                                                                    None
                                                                    (Some "person@example.test")
                                                        }
                                                    ]
                                                NextCursor = None
                                            })
                                     |> RepresentationReply.Human)
                                render
                                    Api.authorityRequestsDismiss
                                    (HumanReplies.read
                                        HumanReadDocuments.dismissed
                                        (Outcome.Succeeded())
                                     |> RepresentationReply.Human)
                            ]

                        for output in outputs do
                            Expect.isFalse
                                (String.IsNullOrWhiteSpace output)
                                "Every human reply is visible"

                            for forbidden in
                                [
                                    "{\""
                                    "[{"
                                    "label."
                                    "field."
                                    "requesterState."
                                    "view."
                                    "cli.text."
                                ] do
                                Expect.isFalse
                                    (output.Contains(forbidden, StringComparison.Ordinal))
                                    ("No JSON or untranslated key: " + forbidden))
        ]


[<Tests>]
let hostReplyPresentation =
    testCase
        "typed host replies render their diagnostics without printing session secrets"
        (fun () ->
            for language in [ "en"; "lv"; "ar" ] do
                let session =
                    {
                        Session.Authenticated = true
                        ContractIdentity = String.replicate 64 "0"
                        AntiforgeryToken = Some "SECRET-ANTIFORGERY-VALUE"
                    }

                let rendered =
                    ReplyText.resultLinesWithDetails
                        true
                        language
                        None
                        (FrameResult.Reply(
                            CliReplies.ofValue
                                Api.session
                                (Outcome.Succeeded(
                                    Presented.create
                                        (fun (reply: Session) ->
                                            ContextScreen.sessionView
                                                reply.Authenticated
                                                reply.ContractIdentity)
                                        session
                                ))
                        ))
                    |> String.concat "\n"

                Expect.isFalse
                    (rendered.Contains "SECRET-ANTIFORGERY-VALUE" || rendered.Contains "{\"")
                    "Session data has an explicit human projection"

                let rejected =
                    ReplyText.resultLinesWithDetails
                        true
                        language
                        None
                        (FrameResult.Reply(
                            CliReplies.ofValue
                                Api.session
                                (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
                        ))
                    |> String.concat "\n"

                Expect.stringContains
                    rejected
                    (MessageCatalogs.render language "diagnostic.RESOURCE_UNAVAILABLE" Map.empty)
                    "Real host read refusals use typed diagnostics"

                let logout =
                    ReplyText.resultLinesWithDetails
                        true
                        language
                        None
                        (FrameResult.Reply(
                            CliReplies.ofValue
                                Api.logout
                                (Outcome.Succeeded(Presented.create ContextScreen.logoutView ()))
                        ))
                    |> String.concat "\n"

                Expect.stringContains
                    logout
                    (MessageCatalogs.render language "label.signedOut" Map.empty)
                    "Logout success has a human descriptor")

[<Tests>]
let assigneePresentation =
    testCase
        "eligible accounts put current names before technical IDs and carry one actual basis"
        (fun () ->
            let actor =
                ActorRef.create
                    (CaseFixtures.actorId (Guid.Parse "12345678-1234-7234-8234-123456789abc"))
                    PrincipalKind.Human
                    ActorAvailability.Available
                    (Some(
                        ActorProfile.parse
                            {
                                DisplayName = "Anna Ozola"
                                Contact = None
                            }
                        |> Fixtures.accepted
                    ))

            let page: AssigneePage =
                {
                    Basis = Inputs.workBasis
                    Items = [ actor ]
                    NextCursor = Some "next-page"
                }

            let columns = AssigneesScreen.assigneeColumns

            Expect.equal
                (columns |> List.find (fun column -> column.Key = "actor")).Prominence
                Prominence.Primary
                "Names own the primary column"

            Expect.equal
                (columns |> List.find (fun column -> column.Key = "actorId")).Prominence
                Prominence.Detail
                "Stable IDs remain technical details"

            for language in LocalizationPolicy.languages do
                let text =
                    ReplyText.resultLinesWithDetails
                        true
                        language
                        None
                        (FrameResult.Reply(
                            CliReplies.ofValue
                                Api.caseAssignees
                                (HumanReplies.read
                                    HumanReadDocuments.assignees
                                    (Outcome.Succeeded page)
                                 |> RepresentationReply.Human)
                        ))
                    |> String.concat "\n"

                Expect.stringContains text "Anna Ozola" "The supplied current name is visible"

                Expect.stringContains
                    text
                    (MessageCatalogs.render language "label.shown" Map.empty + ": 1")
                    "Actual bounded row count"

                Expect.stringContains text "next-page" "Opaque continuation is retained"

                Expect.stringContains
                    text
                    ((Map.ofList
                        [
                            "en", "31 Aug 2026"
                            "lv", "31 aug. 2026"
                            "ar", "31 أغسطس 2026"
                            "de", "31. Aug. 2026"
                            "es", "31 de ago de 2026"
                            "ja", "2026年8月31日"
                            "ko", "2026년 8월 31일"
                            "zh-Hans", "2026年8月31日"
                            "zh-Hant", "2026年8月31日"
                        ]
                      |> Map.find language)
                     + " 12:00 +00:00")
                    "Actual supplied observation is rendered in the business zone"

                Expect.stringContains text "UTC" "Installation zone is rendered"
                Expect.isFalse (text.Contains "{\"") "No JSON fallback"
                Expect.isFalse (text.Contains "field.profile") "No untranslated descriptor key")

[<Tests>]
let fullDirectoryAndAuditDetails =
    testCase
        "directory and access history retain authorized details through wire and CLI"
        (fun () ->
            let profile =
                ActorProfile.parse
                    {
                        DisplayName = "Earlier profile"
                        Contact = Some "historical-only@example.test"
                    }
                |> Fixtures.accepted

            let principal =
                PrincipalKey.human "https://historical-issuer.example/" "historical-subject"
                |> Fixtures.accepted

            let entries =
                [
                    AuthorityHistoryFacts.ProfileSet(Some profile)
                    AuthorityHistoryFacts.Registered principal
                    AuthorityHistoryFacts.OwnerProvisioned principal
                ]
                |> List.mapi (fun index facts ->
                    {
                        AuthorityHistoryEntry.OperationId =
                            OperationId.create (
                                Guid.Parse(sprintf "10000000-0000-7000-8000-%012d" (index + 1))
                            )
                            |> Option.get
                        Position =
                            EffectPosition.create (CaseFixtures.sequence (int64 (index + 1))) 0
                            |> Option.get
                        OperationHash = EntryHash.zero
                        RecordedAt = Inputs.workBasis.ObservedAt
                        RecordedBy = account.Actor
                        Target = account.Actor
                        Facts = facts
                    })

            let history: AuthorityHistoryPage =
                {
                    Basis = Inputs.workBasis
                    Entries = entries
                    NextCursor = None
                }

            let view = AccessHistoryScreen.rows history.Entries

            let wire =
                HumanReplies.read
                    (fun value ->
                        { HumanReadDocuments.history value with
                            Screen = view
                        })
                    (Outcome.Succeeded history)
                |> RepresentationReply.Human
                |> CliReplies.ofValue Api.authorityHistory

            let text =
                ReplyText.resultLinesWithDetails true "en" None (FrameResult.Reply wire)
                |> String.concat "\n"

            for expected in
                [
                    "historical-only@example.test"
                    "https://historical-issuer.example/"
                    "historical-subject"
                ] do
                Expect.stringContains
                    text
                    expected
                    "The historical fact is preserved by the delivered view."

            let erased =
                { history with
                    Entries =
                        entries
                        |> List.map (fun item ->
                            { item with
                                Facts = AuthorityHistoryFacts.ProfileSet None
                            })
                }

            let erasedView = AccessHistoryScreen.rows erased.Entries

            let erasedText = ViewText.render "en" erasedView |> String.concat "\n"

            Expect.isFalse
                (erasedText.Contains "historical-only@example.test")
                "Erased historical profiles disclose no retained contact."

            let mixed =
                { account with
                    Roles =
                        [
                            {
                                Role = Role.CaseReader
                                Installation = true
                                CaseCount = 37L
                            }
                            {
                                Role = Role.CaseHandler
                                Installation = false
                                CaseCount = 12L
                            }
                        ]
                }

            let page: ActorReviewPage =
                {
                    Basis = Inputs.workBasis
                    Items = [ mixed ]
                    NextCursor = None
                }

            let directory =
                HumanReplies.read HumanReadDocuments.actors (Outcome.Succeeded page)
                |> RepresentationReply.Human
                |> CliReplies.ofValue Api.authorityList

            let detailText =
                ReplyText.resultLines "en" None (FrameResult.Reply directory)
                |> String.concat "\n"

            for role in [ Role.CaseReader; Role.CaseHandler ] do
                Expect.stringContains
                    detailText
                    (ReplyTextFormat.label
                        "en"
                        (Vocabulary.messageKey Role.vocabulary role "label"))
                    "The directory view preserves the supplied role identities"

            let exported =
                CliReplies.ofValue
                    Api.authorityList
                    (RepresentationReply.Data(Outcome.Succeeded page))

            match ServiceReply.value Api.authorityList exported with
            | Some(RepresentationReply.Data(Outcome.Succeeded value)) ->
                Expect.equal
                    value.Items.Head.Roles
                    mixed.Roles
                    "Typed export retains installation flags and scoped counts independently of the concise list view"
            | _ -> failtest "Expected typed directory export")

[<Tests>]
let emptyGrantPageDetails =
    testCase "empty grant pages retain their account presentation through wire and CLI" (fun () ->
        let target =
            { account with
                Contact = Some(ContactText.parse "empty-grants@example.test" |> Fixtures.accepted)
            }

        let page: AuthorityGrantPage =
            {
                Basis = Inputs.workBasis
                Actor = target
                Offers = []
                ProfileSeed = None
                Items = []
                NextCursor = None
            }

        let reply =
            HumanReplies.read HumanReadDocuments.grants (Outcome.Succeeded page)
            |> RepresentationReply.Human
            |> CliReplies.ofValue Api.authorityGrants

        let text =
            ReplyText.resultLinesWithDetails true "en" None (FrameResult.Reply reply)
            |> String.concat "\n"

        Expect.stringContains
            text
            "empty-grants@example.test"
            "No grant row is needed to carry the authorized account facts."

        Expect.stringContains
            text
            "https://fixture.example/"
            "The page retains the principal identity.")

[<Tests>]
let receiptHumanTime =
    testCase
        "receipt rendering keeps recorded time in human facts and identifiers in details"
        (fun () ->
            let view = ReceiptScreen.view CliReplyFixtures.receipt

            let entries role =
                view.Regions
                |> List.filter (fun region -> region.Role = role)
                |> List.collect (fun region ->
                    match region.Content with
                    | RegionContent.Entries entries -> entries
                    | _ -> [])

            Expect.isTrue
                (entries RegionRole.Identity
                 |> List.exists (fun entry -> entry.Key = "recordedAt"))
                "Receipt identity exposes the Core-authored recorded time"

            Expect.isFalse
                (entries RegionRole.Details
                 |> List.exists (fun entry -> entry.Key = "recordedAt"))
                "The human time is not duplicated under technical details"

            let text = ViewText.render "en" view |> String.concat "\n"

            Expect.stringContains
                text
                "Recorded at"
                "Default receipt text shows when it was recorded"

            Expect.isFalse
                (text.Contains(OperationId.format CliReplyFixtures.receipt.OperationId))
                "Technical operation identity remains opt-in")
