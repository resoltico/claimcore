module ClaimCore.Tests.DescriptorReplyTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Protocol
open ClaimCore.Cli
open ClaimCore.TestSupport

let private authorityReview () =
    let reply: PreviewReply =
        {
            OperationId = Guid.Parse "10000000-0000-4000-8000-000000000001"
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

    CliReplies.ofValue Api.operationPreview reply

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
                CaseFixtures.actorId (Guid.Parse "10000000-0000-4000-8000-000000000001")
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
                        CaseFixtures.actorId (Guid.Parse "10000000-0000-4000-8000-000000000001")
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
                        TextReply.resultLines language None (FrameResult.Reply(authorityReview ()))

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

                            TextReply.resultLines language None (FrameResult.Reply typed)
                            |> String.concat "\n"

                        let outputs =
                            [
                                render Api.context (ResponseExamples.defaultReply Api.context)
                                render
                                    Api.caseSummary
                                    (ResponseExamples.defaultReply Api.caseSummary)
                                render
                                    Api.authorityList
                                    (Outcome.Succeeded
                                        {
                                            Basis = Inputs.workBasis
                                            Items = [ account ]
                                            NextCursor = Some "opaque-cursor"
                                        })
                                render
                                    Api.authorityGrants
                                    (Outcome.Succeeded
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
                                        })
                                render
                                    Api.authorityRequests
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
                                render Api.authorityRequestsDismiss (Outcome.Succeeded())
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
                    TextReply.resultLines
                        language
                        None
                        (FrameResult.Reply(
                            CliReplies.ofValue Api.session (Outcome.Succeeded session)
                        ))
                    |> String.concat "\n"

                Expect.isFalse
                    (rendered.Contains "SECRET-ANTIFORGERY-VALUE" || rendered.Contains "{\"")
                    "Session data has an explicit human projection"

                let rejected =
                    TextReply.resultLines
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
                    TextReply.resultLines
                        language
                        None
                        (FrameResult.Reply(CliReplies.ofValue Api.logout (Outcome.Succeeded())))
                    |> String.concat "\n"

                Expect.stringContains
                    logout
                    (MessageCatalogs.render language "label.signedOut" Map.empty)
                    "Logout success has a human descriptor")
