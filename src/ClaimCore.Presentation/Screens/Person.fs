namespace ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ScreenEntries

module PersonScreen =
    let grantColumns =
        [
            column "role" "field.role.label" Prominence.Primary false (fun (grant: ScopedGrant) ->
                code Role.vocabulary grant.Role)
            column "scope" "input.scope.label" Prominence.Primary false (fun grant ->
                code
                    ScopeKind.vocabulary
                    (match grant.Scope with
                     | GrantScope.Installation -> ScopeKind.Installation
                     | GrantScope.Case _ -> ScopeKind.Case))
            column "reference" "field.caseReference.label" Prominence.Primary false (fun grant ->
                match grant.Scope with
                | GrantScope.Case(_, reference) -> PresentedValue.Reference reference
                | GrantScope.Installation -> absent)
        ]

    let grant item = entries grantColumns item


    let key (grant: ScopedGrant) =
        Role.token grant.Role
        + ":"
        + (match grant.Scope with
           | GrantScope.Installation -> "INSTALLATION"
           | GrantScope.Case(id, _) -> CaseId.format id)

    let private accessWithControls controls (rows: GrantReadModel list) =
        let columns =
            grantColumns
            |> List.map (fun column ->
                {
                    Key = column.Key
                    LabelId = column.LabelId
                    Prominence = column.Prominence
                    IsMoney = column.IsMoney
                    Read =
                        fun (row: GrantReadModel) ->
                            column.Read { Role = row.Role; Scope = row.Scope }
                })

        let columns =
            columns
            @ [
                column "grantedAt" "label.grantedAt" Prominence.Secondary false (fun row ->
                    PresentedValue.Instant row.GrantedAt)
                column "grantedBy" "label.grantedBy" Prominence.Secondary false (fun row ->
                    ActorPresentation.authorityValue row.GrantedBy)
            ]

        Screen.create
            [
                region
                    RegionRole.Access
                    None
                    (RegionContent.Table(
                        tableWithControls
                            (fun (row: GrantReadModel) ->
                                key { Role = row.Role; Scope = row.Scope })
                            (fun _ -> None)
                            columns
                            controls
                            rows
                    ))
            ]

    let access rows =
        accessWithControls (fun _ -> None, []) rows

    let view (reply: PeopleGrantPage) =
        let actor = reply.Actor
        let _, issuer, subject = PrincipalKey.parts actor.Principal

        let accountColumns =
            PeopleScreen.actorColumns
            |> List.filter (fun column ->
                not (List.contains column.Key [ "actorId"; "issuer"; "subject"; "roles" ])
                && (column.Key <> "responsibleCases" || actor.ResponsibleCases.IsSome))

        let body =
            accessWithControls
                (fun row ->
                    let link =
                        match row.Scope with
                        | GrantScope.Case(id, _) -> Some(Link.caseById id)
                        | _ -> None

                    let actions = row.Controls |> List.choose TaskAffordances.fromOffer

                    link, actions)
                reply.Items

        Screen.create (
            [ entryRegion RegionRole.Account None (entries accountColumns actor) ]
            @ body.Regions
            @ [
                entryRegion
                    RegionRole.Details
                    (Some "label.technicalDetails")
                    [
                        value
                            "actorId"
                            "input.actorId.label"
                            Prominence.Detail
                            (PresentedValue.Identifier(ActorId.format actor.Actor.ActorId))
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
            ]
        )

    let document (value: PeopleGrantPage) =
        let actor = value.Actor.Actor.ActorId

        let actions =
            (value.Controls |> List.choose TaskAffordances.fromOffer)
            @ (if value.CanTerminateSessions then
                   [ Affordance.command (CommandRequest.TerminatePersonSessions actor) ]
               else
                   [])

        let links =
            value.Actor.ResponsibleCases
            |> Option.map (fun selection ->
                NamedLink.create
                    "field.responsibleActor.label"
                    LinkRelation.View
                    (Link.CaseList
                        {
                            Page =
                                {
                                    AfterCursor = None
                                    Limit = Paging.DefaultPageSize
                                }
                            Filter =
                                { CaseListFilter.activeOpen<string> with
                                    View = Some selection.View
                                    ResponsibleActor = Some selection.ResponsibleActor
                                }
                        }))
            |> Option.toList

        HumanDocument.snapshot
            value.Basis
            (view value)
            (DocumentSubject.Person actor)
            actions
            links
            value.NextCursor
            HumanDisclosure.Current
            None
