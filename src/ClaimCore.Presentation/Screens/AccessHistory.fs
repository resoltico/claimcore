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
                PresentedValue.Message("task." + TaskId.token item.Task + ".label"))
            column "recordedBy" "label.recordedBy" Prominence.Primary false (fun item ->
                ActorPresentation.authorityValue item.RecordedBy)
            column "target" "label.targetAccount" Prominence.Primary false (fun item ->
                PresentedValue.Actor item.Target)
            column "sequence" "label.effectPosition" Prominence.Detail false (fun item ->
                PresentedValue.Identifier(EffectPosition.format item.Position))
            column "operationId" "field.operationId.label" Prominence.Detail false (fun item ->
                PresentedValue.Identifier(OperationId.format item.OperationId))
            column "operationHash" "label.operationHash" Prominence.Detail false (fun item ->
                PresentedValue.Identifier(
                    item.OperationHash |> EntryHash.toBytes |> Convert.ToHexStringLower
                ))
        ]

    let peopleHistory (item: PeopleHistoryEntry) =
        let changes, _ =
            ReviewScreen.describePart
                item.References
                (ActorId.value item.Target.ActorId)
                (ClaimCore.Domain.Changes.PartDescription.Person item.Changes)

        [
            section
                "label.peopleHistory"
                (entries peopleHistoryColumns item
                 @ [
                     if not item.PrivateAvailable then
                         value
                             "privateContent"
                             "label.changesErased"
                             Prominence.Secondary
                             (PresentedValue.Absent "label.changesErased")
                 ])
        ]
        @ (changes
           |> List.map (fun change ->
               section
                   change.LabelId
                   [
                       value
                           (change.Key + ".before")
                           "label.before"
                           Prominence.Secondary
                           change.Before
                       value (change.Key + ".after") "label.after" Prominence.Primary change.After
                   ]))

    let rows rows =
        let items =
            rows
            |> List.map (fun (item: PeopleHistoryEntry) ->
                let changes, _ =
                    ReviewScreen.describePart
                        item.References
                        (ActorId.value item.Target.ActorId)
                        (ClaimCore.Domain.Changes.PartDescription.Person item.Changes)

                {
                    RegionTimelineEntry.Key =
                        EffectPosition.format item.Position
                        + "."
                        + OperationId.format item.OperationId
                    Link = Some(Link.person item.Target.ActorId)
                    Target = Some(PresentedValue.Actor item.Target)
                    Changes = changes
                    ChangedFields = TimelineScreen.changedFieldLabels changes []
                    Actions = []
                    When = item.RecordedAt
                    Who = ActorPresentation.authorityValue item.RecordedBy
                    Summary =
                        {
                            MessageId = "task." + TaskId.token item.Task + ".label"
                            Arguments = Map.empty
                        }
                    Highlights =
                        [
                            section
                                "label.changes"
                                [
                                    if not item.PrivateAvailable then
                                        value
                                            "privateContent"
                                            "label.changes"
                                            Prominence.Secondary
                                            (PresentedValue.Absent "label.changesErased")
                                    else
                                        value
                                            "changes"
                                            "label.changes"
                                            Prominence.Secondary
                                            (PresentedValue.Count(int64 changes.Length))
                                ]
                        ]
                    Details =
                        [
                            section
                                "label.technicalDetails"
                                (entries peopleHistoryColumns item |> List.filter technical)
                        ]
                })

        Screen.create
            [
                region RegionRole.Activity None (RegionContent.Timeline items)
                |> withEmpty "empty.accessHistory"
            ]

    let view (reply: PeopleHistoryPage) = rows reply.Entries
