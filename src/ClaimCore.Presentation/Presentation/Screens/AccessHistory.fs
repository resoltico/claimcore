namespace ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ScreenEntries
open PersonScreen

module AccessHistoryScreen =
    let authorityHistoryColumns =
        [
            column
                "recordedAt"
                "label.recordedAt"
                Prominence.Secondary
                false
                (fun (item: AuthorityHistoryEntry) -> PresentedValue.Instant item.RecordedAt)
            column "act" "label.action" Prominence.Primary false (fun item ->
                code AuthorityKind.vocabulary (AuthorityHistoryFacts.kind item.Facts))
            column "recordedBy" "label.recordedBy" Prominence.Primary false (fun item ->
                PresentedValue.Actor item.RecordedBy)
            column "target" "label.targetAccount" Prominence.Primary false (fun item ->
                PresentedValue.Actor item.Target)
            column
                "scopeReference"
                "field.caseReference.label"
                Prominence.Secondary
                false
                (fun item ->
                    match item.Facts with
                    | AuthorityHistoryFacts.Granted {
                                                        Scope = GrantScope.Case(_, reference)
                                                    }
                    | AuthorityHistoryFacts.Revoked {
                                                        Scope = GrantScope.Case(_, reference)
                                                    } -> PresentedValue.Reference reference
                    | _ -> absent)
            column "details" "label.details" Prominence.Primary false (fun item ->
                match item.Facts with
                | AuthorityHistoryFacts.Granted grant
                | AuthorityHistoryFacts.Revoked grant -> code Role.vocabulary grant.Role
                | AuthorityHistoryFacts.ProfileSet(Some profile) ->
                    PresentedValue.Party profile.DisplayName
                | AuthorityHistoryFacts.ProfileSet None
                | AuthorityHistoryFacts.ProfileErased ->
                    PresentedValue.Absent "label.changesErased"
                | AuthorityHistoryFacts.OwnerProvisioned principal
                | AuthorityHistoryFacts.Registered principal ->
                    let kind, _, _ = PrincipalKey.parts principal in
                    code PrincipalKind.vocabulary kind
                | AuthorityHistoryFacts.Enabled -> boolean true
                | AuthorityHistoryFacts.Disabled -> boolean false)
            column "scope" "input.scope.label" Prominence.Secondary false (fun item ->
                match item.Facts with
                | AuthorityHistoryFacts.Granted grant
                | AuthorityHistoryFacts.Revoked grant ->
                    code
                        ScopeKind.vocabulary
                        (match grant.Scope with
                         | GrantScope.Installation -> ScopeKind.Installation
                         | GrantScope.Case _ -> ScopeKind.Case)
                | _ -> PresentedValue.Absent "label.notApplicable")
            column "reference" "field.caseReference.label" Prominence.Secondary false (fun item ->
                match item.Facts with
                | AuthorityHistoryFacts.Granted {
                                                    Scope = GrantScope.Case(_, reference)
                                                }
                | AuthorityHistoryFacts.Revoked {
                                                    Scope = GrantScope.Case(_, reference)
                                                } -> PresentedValue.Reference reference
                | _ -> PresentedValue.Absent "label.notApplicable")
            column "sequence" "label.sequence" Prominence.Detail false (fun item ->
                PresentedValue.Identifier(Sequence.format item.Sequence))
        ]

    let authorityHistory item =
        let facts =
            match item.Facts with
            | AuthorityHistoryFacts.OwnerProvisioned principal
            | AuthorityHistoryFacts.Registered principal ->
                let kind, issuer, subject = PrincipalKey.parts principal

                [
                    value
                        "principalKind"
                        "field.principal.kind.label"
                        Prominence.Detail
                        (code PrincipalKind.vocabulary kind)
                    value
                        "issuer"
                        "field.principal.issuer.label"
                        Prominence.Detail
                        (PresentedValue.Identifier issuer)
                    value
                        "subject"
                        "field.principal.subject.label"
                        Prominence.Detail
                        (PresentedValue.Identifier subject)
                ]
            | AuthorityHistoryFacts.Granted item
            | AuthorityHistoryFacts.Revoked item -> grant item
            | AuthorityHistoryFacts.ProfileSet profile ->
                [
                    value
                        "name"
                        "field.profile.displayName.label"
                        Prominence.Primary
                        (optional
                            (fun (profile: ActorProfile) ->
                                PresentedValue.Party profile.DisplayName)
                            profile)
                    value
                        "contact"
                        "field.profile.contact.label"
                        Prominence.Secondary
                        (profile
                         |> Option.bind _.Contact
                         |> optional (ContactText.format >> PresentedValue.Text))
                ]
            | AuthorityHistoryFacts.Enabled
            | AuthorityHistoryFacts.Disabled
            | AuthorityHistoryFacts.ProfileErased -> []

        [
            section "label.authorityHistory" (entries authorityHistoryColumns item @ facts)
        ]


    let rows rows =
        let items =
            rows
            |> List.map (fun (item: AuthorityHistoryEntry) ->
                let sections =
                    authorityHistory item
                    |> List.map (fun section ->
                        let visible =
                            section.Entries
                            |> List.filter (fun entry ->
                                not (technical entry)
                                && not (
                                    List.contains entry.Key [ "recordedAt"; "recordedBy"; "act" ]
                                ))

                        { section with Entries = visible })

                {
                    RegionTimelineEntry.Key = Sequence.format item.Sequence
                    When = item.RecordedAt
                    Who = item.RecordedBy
                    Summary =
                        {
                            MessageId =
                                Vocabulary.messageKey
                                    AuthorityKind.vocabulary
                                    (AuthorityHistoryFacts.kind item.Facts)
                                    "label"
                            Arguments = Map.empty
                        }
                    Highlights = sections
                    Details = []
                })

        let details =
            rows
            |> List.collect (fun item ->
                authorityHistory item
                |> List.collect _.Entries
                |> List.filter technical
                |> List.map (fun entry ->
                    { entry with
                        Key = Sequence.format item.Sequence + "." + entry.Key
                    }))

        Screen.create
            [
                region RegionRole.Activity None (RegionContent.Timeline items)
                if not details.IsEmpty then
                    entryRegion RegionRole.Details (Some "label.recordDetails") details
            ]

    let view (reply: AuthorityHistoryPage) =
        rows reply.Entries |> page reply.Basis reply.Entries.Length reply.NextCursor
