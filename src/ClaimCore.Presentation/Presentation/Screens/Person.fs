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

    let access rows =
        Screen.create
            [
                region
                    RegionRole.Access
                    None
                    (RegionContent.Table(table key (fun _ -> None) grantColumns rows))
            ]

    let view (reply: AuthorityGrantPage) =
        let actor = reply.Actor
        let _, issuer, subject = PrincipalKey.parts actor.Principal

        let accountColumns =
            PeopleScreen.actorColumns
            |> List.filter (fun column ->
                not (List.contains column.Key [ "actorId"; "issuer"; "subject"; "roles" ]))

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
