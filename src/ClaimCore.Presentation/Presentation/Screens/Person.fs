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

    let access (rows: GrantReadModel list) =
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
                    PresentedValue.Actor row.GrantedBy)
            ]

        Screen.create
            [
                region
                    RegionRole.Access
                    None
                    (RegionContent.Table(
                        table
                            (fun (row: GrantReadModel) ->
                                key { Role = row.Role; Scope = row.Scope })
                            (fun _ -> None)
                            columns
                            rows
                    ))
            ]

    let view (reply: PeopleGrantPage) =
        let actor = reply.Actor
        let _, issuer, subject = PrincipalKey.parts actor.Principal

        let accountColumns =
            PeopleScreen.actorColumns
            |> List.filter (fun column ->
                not (List.contains column.Key [ "actorId"; "issuer"; "subject"; "roles" ])
                && (column.Key <> "responsibleCases" || actor.ResponsibleCases.IsSome))

        let body = access reply.Items

        let screen =
            Screen.create (
                [ entryRegion RegionRole.Account None (entries accountColumns actor) ]
                @ body.Regions
                @ [
                    entryRegion
                        RegionRole.Details
                        (Some "label.recordDetails")
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

        page reply.Basis reply.Items.Length reply.NextCursor screen
