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
        page reply.Basis reply.Items.Length reply.NextCursor (rows reply.Items)
