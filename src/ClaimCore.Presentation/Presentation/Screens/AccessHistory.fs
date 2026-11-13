namespace ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ScreenEntries
open PersonScreen

module AccessHistoryScreen =
    let peopleHistoryColumns =
        [
            column
                "recordedAt"
                "label.recordedAt"
                Prominence.Secondary
                false
                (fun (item: PeopleHistoryEntry) -> PresentedValue.Instant item.RecordedAt)
            column "act" "label.action" Prominence.Primary false (fun item ->
                code PeopleActKind.vocabulary (PeopleHistoryFacts.kind item.Facts))
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
                    | PeopleHistoryFacts.Granted {
                                                     Scope = GrantScope.Case(_, reference)
                                                 }
                    | PeopleHistoryFacts.Revoked {
                                                     Scope = GrantScope.Case(_, reference)
                                                 } -> PresentedValue.Reference reference
                    | _ -> absent)
            column "details" "label.details" Prominence.Primary false (fun item ->
                match item.Facts with
                | PeopleHistoryFacts.Granted grant
                | PeopleHistoryFacts.Revoked grant -> code Role.vocabulary grant.Role
                | PeopleHistoryFacts.ProfileSet(Some profile) ->
                    PresentedValue.Party profile.DisplayName
                | PeopleHistoryFacts.ProfileSet None
                | PeopleHistoryFacts.ProfileErased -> PresentedValue.Absent "label.changesErased"
                | PeopleHistoryFacts.OwnerProvisioned principal
                | PeopleHistoryFacts.Registered principal ->
                    let kind, _, _ = PrincipalKey.parts principal in
                    code PrincipalKind.vocabulary kind
                | PeopleHistoryFacts.Enabled -> boolean true
                | PeopleHistoryFacts.Disabled -> boolean false)
            column "scope" "input.scope.label" Prominence.Secondary false (fun item ->
                match item.Facts with
                | PeopleHistoryFacts.Granted grant
                | PeopleHistoryFacts.Revoked grant ->
                    code
                        ScopeKind.vocabulary
                        (match grant.Scope with
                         | GrantScope.Installation -> ScopeKind.Installation
                         | GrantScope.Case _ -> ScopeKind.Case)
                | _ -> PresentedValue.Absent "label.notApplicable")
            column "reference" "field.caseReference.label" Prominence.Secondary false (fun item ->
                match item.Facts with
                | PeopleHistoryFacts.Granted {
                                                 Scope = GrantScope.Case(_, reference)
                                             }
                | PeopleHistoryFacts.Revoked {
                                                 Scope = GrantScope.Case(_, reference)
                                             } -> PresentedValue.Reference reference
                | _ -> PresentedValue.Absent "label.notApplicable")
            column "sequence" "label.effectPosition" Prominence.Detail false (fun item ->
                PresentedValue.Identifier(EffectPosition.format item.Position))
            column "operationId" "field.operationId.label" Prominence.Detail false (fun item ->
                PresentedValue.Identifier(OperationId.format item.OperationId))
            column "operationHash" "label.operationHash" Prominence.Detail false (fun item ->
                PresentedValue.Identifier(
                    item.OperationHash |> EntryHash.toBytes |> Convert.ToHexStringLower
                ))
        ]

    let peopleHistory item =
        let facts =
            match item.Facts with
            | PeopleHistoryFacts.OwnerProvisioned principal
            | PeopleHistoryFacts.Registered principal ->
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
            | PeopleHistoryFacts.Granted item
            | PeopleHistoryFacts.Revoked item -> grant item
            | PeopleHistoryFacts.ProfileSet profile ->
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
            | PeopleHistoryFacts.Enabled
            | PeopleHistoryFacts.Disabled
            | PeopleHistoryFacts.ProfileErased -> []

        [ section "label.peopleHistory" (entries peopleHistoryColumns item @ facts) ]


    let rows rows =
        let items =
            rows
            |> List.map (fun (item: PeopleHistoryEntry) ->
                let sections =
                    peopleHistory item
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
                    RegionTimelineEntry.Key = EffectPosition.format item.Position
                    When = item.RecordedAt
                    Who = item.RecordedBy
                    Summary =
                        {
                            MessageId =
                                Vocabulary.messageKey
                                    PeopleActKind.vocabulary
                                    (PeopleHistoryFacts.kind item.Facts)
                                    "label"
                            Arguments = Map.empty
                        }
                    Highlights = sections
                    Details = []
                })

        let details =
            rows
            |> List.collect (fun item ->
                peopleHistory item
                |> List.collect _.Entries
                |> List.filter technical
                |> List.map (fun entry ->
                    { entry with
                        Key = EffectPosition.format item.Position + "." + entry.Key
                    }))

        Screen.create
            [
                region RegionRole.Activity None (RegionContent.Timeline items)
                if not details.IsEmpty then
                    entryRegion RegionRole.Details (Some "label.recordDetails") details
            ]

    let view (reply: PeopleHistoryPage) =
        rows reply.Entries |> page reply.Basis reply.Entries.Length reply.NextCursor
