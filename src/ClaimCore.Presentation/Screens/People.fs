namespace ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ScreenEntries

module PeopleScreen =
    let actorColumns =
        [
            column
                "actor"
                "field.profile.displayName.label"
                Prominence.Primary
                false
                (fun (item: ActorReviewItem) -> PresentedValue.Actor item.Actor)
            column "state" "label.accountState" Prominence.Primary false (fun item ->
                code AccountState.vocabulary item.State)
            column "roles" "field.role.label" Prominence.Secondary false (fun item ->
                codes Role.vocabulary (item.Roles |> List.map _.Role))
            column
                "responsibleCases"
                "label.responsibleCases"
                Prominence.Secondary
                false
                (fun item ->
                    item.ResponsibleCases
                    |> Option.map (fun (count: ActorResponsibleCases) ->
                        PresentedValue.Count count.Count)
                    |> Option.defaultValue (PresentedValue.Absent "label.detailsWithheld"))
            column "contact" "field.profile.contact.label" Prominence.Detail false (fun item ->
                optional (ContactText.format >> PresentedValue.Text) item.Contact)
            column "actorId" "input.actorId.label" Prominence.Detail false (fun item ->
                PresentedValue.Identifier(ActorId.format item.Actor.ActorId))
            column "principalKind" "field.principal.kind.label" Prominence.Detail false (fun item ->
                let kind, _, _ = PrincipalKey.parts item.Principal in
                code PrincipalKind.vocabulary kind)
            column "issuer" "field.principal.issuer.label" Prominence.Detail false (fun item ->
                let _, issuer, _ = PrincipalKey.parts item.Principal in
                PresentedValue.Identifier issuer)
            column "subject" "field.principal.subject.label" Prominence.Detail false (fun item ->
                let _, _, subject = PrincipalKey.parts item.Principal in
                PresentedValue.Identifier subject)
        ]

    let roleSections (roles: ActorRoleSummary list) =
        roles
        |> List.map (fun role ->
            section
                "field.role.label"
                [
                    value
                        "role"
                        "field.role.label"
                        Prominence.Primary
                        (code Role.vocabulary role.Role)
                    value
                        "installation"
                        "label.installationRoleAssigned"
                        Prominence.Secondary
                        (boolean role.Installation)
                    value
                        "cases"
                        "label.caseRoleAssignments"
                        Prominence.Secondary
                        (PresentedValue.Count role.CaseCount)
                ])

    let actor item =
        section "label.account" (entries actorColumns item) :: roleSections item.Roles


    let rows rows =
        let columns =
            actorColumns
            |> List.filter (fun column ->
                not (List.contains column.Key [ "actorId"; "issuer"; "subject" ]))

        resultScreen (fun (row: ActorReviewItem) -> ActorId.format row.Actor.ActorId) columns rows

    let view (reply: ActorReviewPage) =
        page reply.Basis reply.Items.Length reply.NextCursor (rows reply.Items)
