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

    CliReplies.ofValue Api.operationPreview (PresentedPreviewReply.ofCore None reply)

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
                                render Api.context (ResponseExamples.defaultReply Api.context)
                                render
                                    Api.caseSummary
                                    (ResponseExamples.defaultReply Api.caseSummary)
                                render
                                    Api.authorityList
                                    (Outcome.Succeeded(
                                        Presented.create
                                            (fun (page: ActorReviewPage) ->
                                                PeopleScreen.rows page.Items)
                                            {
                                                Basis = Inputs.workBasis
                                                Items = [ account ]
                                                NextCursor = Some "opaque-cursor"
                                            }
                                    ))
                                render
                                    Api.authorityGrants
                                    (Outcome.Succeeded(
                                        Presented.create
                                            (fun (page: AuthorityGrantPage) ->
                                                PersonScreen.access page.Items)
                                            {
                                                Basis = Inputs.workBasis
                                                Actor = account
                                                Items =
                                                    [
                                                        {
                                                            Role = Role.CaseReader
                                                            Scope = GrantScope.Installation
                                                        }
                                                    ]
                                                NextCursor = None
                                            }
                                    ))
                                render
                                    Api.authorityRequests
                                    (Outcome.Succeeded(
                                        Presented.create
                                            (fun (page: AccessRequestPage) ->
                                                AccessRequestsScreen.rows page.Items)
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
                                            }
                                    ))
                                render
                                    Api.authorityRequestsDismiss
                                    (Outcome.Succeeded(
                                        Presented.create AccessRequestsScreen.dismissed ()
                                    ))
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
                    ReplyText.resultLines
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
                    ReplyText.resultLines
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
                    ReplyText.resultLines
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
                    ReplyText.resultLines
                        language
                        None
                        (FrameResult.Reply(
                            CliReplies.ofValue
                                Api.caseAssignees
                                (Outcome.Succeeded(
                                    Presented.create
                                        (fun (page: AssigneePage) ->
                                            AssigneesScreen.rows page.Items)
                                        page
                                ))
                        ))
                    |> String.concat "\n"

                Expect.stringContains text "Anna Ozola" "The supplied current name is visible"

                Expect.stringContains
                    text
                    (MessageCatalogs.renderCli language (CliMessage.PageCount "1"))
                    "Actual bounded row count"

                Expect.stringContains text "next-page" "Opaque continuation is retained"

                Expect.stringContains
                    text
                    "2026-08-31 12:00"
                    "Actual supplied observation is rendered"

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
                        AuthorityHistoryEntry.Sequence = CaseFixtures.sequence (int64 (index + 1))
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
                Presented.create (fun _ -> view) history
                |> Outcome.Succeeded
                |> CliReplies.ofValue Api.authorityHistory

            let text =
                ReplyText.resultLines "en" None (FrameResult.Reply wire) |> String.concat "\n"

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
                Presented.create
                    (fun (value: ActorReviewPage) -> PeopleScreen.rows value.Items)
                    page
                |> Outcome.Succeeded
                |> CliReplies.ofValue Api.authorityList

            let detailText =
                ReplyText.resultLines "en" None (FrameResult.Reply directory)
                |> String.concat "\n"

            for expected in
                [
                    "37"
                    "12"
                    ReplyTextFormat.label "en" "label.installationRoleAssigned"
                    ReplyTextFormat.label "en" "label.caseRoleAssignments"
                    ReplyTextFormat.label "en" "boolean.TRUE.label"
                    ReplyTextFormat.label "en" "boolean.FALSE.label"
                ] do
                Expect.stringContains
                    detailText
                    expected
                    "Global assignments and case counts stay distinguishable.")

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
                Items = []
                NextCursor = None
            }

        let reply =
            Outcome.Succeeded(Presented.create PersonScreen.view page)
            |> CliReplies.ofValue Api.authorityGrants

        let text =
            ReplyText.resultLines "en" None (FrameResult.Reply reply) |> String.concat "\n"

        Expect.stringContains
            text
            "empty-grants@example.test"
            "No grant row is needed to carry the authorized account facts."

        Expect.stringContains
            text
            "https://fixture.example/"
            "The page retains the principal identity.")
