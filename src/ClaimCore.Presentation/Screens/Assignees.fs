namespace ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ScreenEntries

module AssigneesScreen =
    let assigneeColumns =
        [
            column
                "actor"
                "field.profile.displayName.label"
                Prominence.Primary
                false
                PresentedValue.Actor
            column "actorId" "input.actorId.label" Prominence.Detail false (fun actor ->
                PresentedValue.Identifier(ActorId.format actor.ActorId))
        ]



    let private rows caseId revision rows =
        let columns = assigneeColumns |> List.filter (fun column -> column.Key <> "actorId")

        choiceScreen
            "label.noEligibleAccounts"
            (fun (row: ActorRef) -> ActorId.format row.ActorId)
            (fun row ->
                {
                    MessageId = "label.personChoice"
                    Arguments = Map.ofList [ "person", PresentedValue.Actor row ]
                })
            columns
            (fun row ->
                Some(Link.person row.ActorId),
                [
                    TaskAffordances.work
                        caseId
                        revision
                        (Map.ofList
                            [
                                TaskAffordances.field BusinessField.ResponsibleActor,
                                TaskAffordances.text (ActorId.format row.ActorId)
                            ])
                        []
                ])
            rows

    let view (reply: AssigneePage) =
        let screen = rows reply.CaseId reply.WorkRevision reply.Items

        let details =
            reply.Items
            |> List.map (fun actor ->
                value
                    (ActorId.format actor.ActorId)
                    "input.actorId.label"
                    Prominence.Detail
                    (PresentedValue.Identifier(ActorId.format actor.ActorId)))

        Screen.create (
            screen.Regions
            @ [ entryRegion RegionRole.Details (Some "label.technicalDetails") details ]
        )

    let document (value: AssigneePage) =
        HumanDocument.listPage value.Basis (view value) value.NextCursor
