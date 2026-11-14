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

        resultScreenWithControls
            (fun (row: ActorRef) -> ActorId.format row.ActorId)
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

        let screen =
            Screen.create (
                screen.Regions
                @ [ entryRegion RegionRole.Details (Some "label.recordDetails") details ]
            )

        page reply.Basis reply.Items.Length reply.NextCursor screen
