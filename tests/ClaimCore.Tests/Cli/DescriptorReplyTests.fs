module ClaimCore.Tests.DescriptorReplyTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Cli
open ClaimCore.TestSupport

let private peopleReview () =
    let operation = Guid.Parse "10000000-0000-7000-8000-000000000001"
    let actor = CliReplyFixtures.receipt.RecordedBy |> AuthorityRef.actor |> Option.get
    let binding = Binding.People(actor.ActorId, Revision.first)

    let description =
        {
            PersonPartDescription.Profiles = { Before = None; After = None }
            Principal = None
            Enabled =
                Some
                    {
                        Before = Some false
                        After = Some true
                    }
            ProfilePresent = None
            DisplayName = None
            DigestEmail = None
            Contact = None
            Grants =
                [
                    {
                        Grant =
                            {
                                Role = Role.CaseReader
                                Scope = Scope.Installation
                            }
                        Before = false
                        After = true
                    }
                ]
        }

    let review =
        Reviews.personReview
            (TaskId.ofLiteral "people.grant")
            binding
            (Some actor)
            (ActorId.value actor.ActorId)
            (Revision.create 2L |> Option.get)
            description
            { Actors = []; Cases = [] }
            {
                ProductVersion = "fixture"
                Basis = DomainInputs.workBasis
            }

    let input =
        Submissions.create
            "people.grant"
            operation
            binding
            (Submissions.grant
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                })

    CliReplies.humanPreview
        input
        {
            PreviewReply.OperationId = operation
            Outcome = Outcome.Succeeded(Previewed.Reviewed review)
        }

let rec private unknownPeople =
    function
    | JsonValue.String "PEOPLE" -> JsonValue.String "UNDECLARED"
    | JsonValue.Object fields ->
        fields
        |> List.map (fun (name, value) -> name, unknownPeople value)
        |> JsonValue.Object
    | JsonValue.Array values -> values |> List.map unknownPeople |> JsonValue.Array
    | value -> value

let private account =
    {
        ActorReviewItem.Actor =
            DomainInputs.actorReference (
                CaseFixtures.actorId (Guid.Parse "10000000-0000-7000-8000-000000000001")
            )
        Principal = PrincipalKey.human "https://fixture.example/" "person" |> CaseBuilders.accepted
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
            testCase "people review uses the typed branch and localized grants" (fun () ->
                for language in [ "en"; "lv"; "ar" ] do
                    let lines =
                        ReplyText.resultLines
                            (RenderingContext.forLanguage language)
                            None
                            (FrameResult.Reply(peopleReview ()))

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

                         || text.Contains "{\"")
                        "Human output contains neither wire tags nor JSON")
            testCase "unknown tagged output is rejected at the typed boundary" (fun () ->
                let original = peopleReview ()

                Expect.isError
                    (ServiceReply.read original.Route (unknownPeople original.Wire))
                    "The codec rejects an unknown union branch before rendering")
            testCase
                "context and summary descriptors are localized in all human languages"
                (fun () ->
                    for language in [ "en"; "lv"; "ar" ] do
                        let render endpoint reply =
                            let typed = CliReplies.ofValue endpoint reply

                            ReplyText.resultLines
                                (RenderingContext.forLanguage language)
                                None
                                (FrameResult.Reply typed)
                            |> String.concat "\n"

                        let outputs =
                            [
                                render Api.context (ResponseExamples.defaultHuman Api.context)
                                render
                                    Api.caseSummary
                                    (ResponseExamples.defaultHuman Api.caseSummary)
                                render
                                    Api.peopleList
                                    (HumanReplies.read
                                        HumanReadDocuments.actors
                                        (Outcome.Succeeded
                                            {
                                                Basis = DomainInputs.workBasis
                                                Items = [ account ]
                                                NextCursor = Some "opaque-cursor"
                                            })
                                     |> RepresentationReply.Human)
                                render
                                    Api.peopleGrants
                                    (HumanReplies.read
                                        HumanReadDocuments.grants
                                        (Outcome.Succeeded
                                            {
                                                Revision = Revision.first
                                                Basis = DomainInputs.workBasis
                                                Actor = account
                                                Offers = []
                                                Controls = []
                                                ProfileSeed = None
                                                CanTerminateSessions = false
                                                Items =
                                                    [
                                                        {
                                                            GrantReadModel.Controls = []
                                                            Role = Role.CaseReader
                                                            Scope = GrantScope.Installation
                                                            GrantedAt =
                                                                DomainInputs.workBasis.ObservedAt
                                                            GrantedBy =
                                                                AuthorityRef.Actor
                                                                    ResponseExamples.caseRead
                                                                        .RecordedBy
                                                        }
                                                    ]
                                                NextCursor = None
                                            })
                                     |> RepresentationReply.Human)
                                render
                                    Api.peopleRequests
                                    (HumanReplies.read
                                        HumanReadDocuments.requests
                                        (Outcome.Succeeded
                                            {
                                                Basis = DomainInputs.workBasis
                                                Items =
                                                    [
                                                        {
                                                            Principal = account.Principal
                                                            FirstSeen =
                                                                DomainInputs.workBasis.ObservedAt
                                                            LastSeen =
                                                                DomainInputs.workBasis.ObservedAt
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
                                    Api.peopleRequestsDismiss
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
                        (RenderingContext.forLanguage language)
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
                        (RenderingContext.forLanguage language)
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
                        (RenderingContext.forLanguage language)
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
                                DigestEmail = None
                                Contact = None
                            }
                        |> CaseBuilders.accepted
                    ))

            let page: AssigneePage =
                {
                    CaseId = ResponseExamples.snapshot.CaseId
                    WorkRevision = Revision.zero
                    Basis = DomainInputs.workBasis
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
                        (RenderingContext.forLanguage language)
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

                let date =
                    (Map.ofList
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

                let instants =
                    match language with
                    | "ar" -> [ " 12:00\u00a0م +00:00"; " 12:00 م +00:00" ]
                    // ICU 77 uses wide Korean day periods; ICU 78 defaults to the abbreviated set.
                    | "ko" -> [ " 오후 12:00 +00:00"; " PM 12:00 +00:00" ]
                    | "zh-Hant" -> [ " 下午12:00 +00:00" ]
                    | _ -> [ " 12:00 +00:00" ]
                    |> List.map (fun time -> date + time)

                let includesObservation (output: string) =
                    instants |> List.exists (fun instant -> output.Contains instant)

                Expect.isTrue
                    (includesObservation text)
                    (sprintf
                        "Actual supplied observation retains the exact date, time and business-zone offset. Expected one of %A in %s"
                        instants
                        text)

                if language = "ko" then
                    let observation = instants |> List.find (fun instant -> text.Contains instant)

                    for incorrect in
                        [
                            observation.Replace("2026년 8월 31일", "31 Aug 2026")
                            observation.Replace("2026년 8월 31일", "2026년 8월 30일")
                            observation.Replace("12:00", "12:01")
                            observation.Replace("+00:00", "+01:00")
                            observation.Replace(" 오후 ", " 오전 ").Replace(" PM ", " AM ")
                        ] do
                        Expect.isFalse
                            (includesObservation incorrect)
                            "Finite locale-data variants still reject a wrong locale, date, time, offset or day period"

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
                        DigestEmail = None
                        Contact = Some "historical-only@example.test"
                    }
                |> CaseBuilders.accepted

            let principal =
                PrincipalKey.human "https://historical-issuer.example/" "historical-subject"
                |> CaseBuilders.accepted

            let blank =
                {
                    PersonPartDescription.Profiles = { Before = None; After = None }
                    Principal = None
                    Enabled = None
                    ProfilePresent = None
                    DisplayName = None
                    DigestEmail = None
                    Contact = None
                    Grants = []
                }

            let profileChange =
                { blank with
                    Profiles = { Before = None; After = Some profile }
                    DisplayName =
                        Some
                            {
                                Before = None
                                After = Some profile.DisplayName
                            }
                    Contact =
                        Some
                            {
                                Before = None
                                After = profile.Contact
                            }
                }

            let entries =
                [
                    "people.setProfile", profileChange
                    "people.register",
                    { profileChange with
                        Principal =
                            Some
                                {
                                    Before = None
                                    After = Some principal
                                }
                    }
                    "people.provisionOwner",
                    { profileChange with
                        Principal =
                            Some
                                {
                                    Before = None
                                    After = Some principal
                                }
                    }
                ]
                |> List.mapi (fun index (task, changes) ->
                    {
                        PeopleHistoryEntry.OperationId =
                            OperationId.create (
                                Guid.Parse(sprintf "10000000-0000-7000-8000-%012d" (index + 1))
                            )
                            |> Option.get
                        Position =
                            EffectPosition.create (CaseFixtures.sequence (int64 (index + 1))) 0
                            |> Option.get
                        OperationHash = EntryHash.zero
                        RecordedAt = DomainInputs.workBasis.ObservedAt
                        RecordedBy = AuthorityRef.Actor(account.Actor)
                        Target = account.Actor
                        Task = TaskId.ofLiteral task
                        Changes = changes
                        PrivateAvailable = true
                        References = { Actors = []; Cases = [] }
                    })

            let history: PeopleHistoryPage =
                {
                    Basis = DomainInputs.workBasis
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
                |> CliReplies.ofValue Api.peopleHistory

            let text =
                ReplyText.resultLinesWithDetails
                    true
                    (RenderingContext.forLanguage "en")
                    None
                    (FrameResult.Reply wire)
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
                                PrivateAvailable = false
                                Changes =
                                    { item.Changes with
                                        Profiles = { Before = None; After = None }
                                        DisplayName = None
                                        DigestEmail = None
                                        Contact = None
                                    }
                            })
                }

            let erasedView = AccessHistoryScreen.rows erased.Entries

            let erasedText =
                ViewText.render (RenderingContext.forLanguage "en") erasedView
                |> String.concat "\n"

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
                    Basis = DomainInputs.workBasis
                    Items = [ mixed ]
                    NextCursor = None
                }

            let directory =
                HumanReplies.read HumanReadDocuments.actors (Outcome.Succeeded page)
                |> RepresentationReply.Human
                |> CliReplies.ofValue Api.peopleList

            let detailText =
                ReplyText.resultLines
                    (RenderingContext.forLanguage "en")
                    None
                    (FrameResult.Reply directory)
                |> String.concat "\n"

            for role in [ Role.CaseReader; Role.CaseHandler ] do
                Expect.stringContains
                    detailText
                    (ReplyTextFormat.label
                        (RenderingContext.forLanguage "en")
                        (Vocabulary.messageKey Role.vocabulary role "label"))
                    "The directory view preserves the supplied role identities"

            let exported =
                CliReplies.ofValue
                    Api.peopleList
                    (RepresentationReply.Data(Outcome.Succeeded page))

            match ServiceReply.value Api.peopleList exported with
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
                Contact =
                    Some(ContactText.parse "empty-grants@example.test" |> CaseBuilders.accepted)
            }

        let page: PeopleGrantPage =
            {
                Revision = Revision.first
                Basis = DomainInputs.workBasis
                Actor = target
                Offers = []
                Controls = []
                ProfileSeed = None
                CanTerminateSessions = false
                Items = []
                NextCursor = None
            }

        let reply =
            HumanReplies.read HumanReadDocuments.grants (Outcome.Succeeded page)
            |> RepresentationReply.Human
            |> CliReplies.ofValue Api.peopleGrants

        let text =
            ReplyText.resultLinesWithDetails
                true
                (RenderingContext.forLanguage "en")
                None
                (FrameResult.Reply reply)
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

            let text =
                ViewText.render (RenderingContext.forLanguage "en") view |> String.concat "\n"

            Expect.stringContains
                text
                "Recorded at"
                "Default receipt text shows when it was recorded"

            Expect.isFalse
                (text.Contains(OperationId.format CliReplyFixtures.receipt.OperationId))
                "Technical operation identity remains opt-in")
