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



    let rows rows =
        let columns = assigneeColumns |> List.filter (fun column -> column.Key <> "actorId")
        resultScreen (fun (row: ActorRef) -> ActorId.format row.ActorId) columns rows

    let view (reply: AssigneePage) =
        let screen = rows reply.Items

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
