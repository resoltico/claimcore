namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts

module internal ReviewDocumentParts =
    let describe operationId (review: OperationReview) =
        let described = ReviewScreen.operationReview review

        let identityEntries =
            [
                ScreenEntries.value
                    "task"
                    "label.action"
                    Prominence.Primary
                    (PresentedValue.Message("task." + TaskId.token review.Task + ".label"))
                match review.Target with
                | ReviewTarget.Case(_, reference) ->
                    ScreenEntries.value
                        "reference"
                        "field.caseReference.label"
                        Prominence.Primary
                        (PresentedValue.Reference reference)
                | ReviewTarget.Person actor ->
                    ScreenEntries.value
                        "target"
                        "label.targetAccount"
                        Prominence.Primary
                        (ScreenEntries.optional PresentedValue.Actor actor)
            ]

        let identity =
            [ ScreenEntries.entryRegion RegionRole.Identity None identityEntries ]

        let reviewed target revision =
            let input = TaskInputId.Target target

            ScreenEntries.value
                (TaskInputId.key input)
                (TaskInputPresentation.messageKey input "label")
                Prominence.Detail
                (PresentedValue.Identifier(Revision.format revision))

        let reviewedVersion =
            match review.Binding with
            | Binding.Case(_, revision)
            | Binding.CasePayment(_, revision, _) ->
                [ reviewed InputTarget.ExpectedRevision revision ]
            | Binding.CaseErase(_, revision, sequence) ->
                [
                    reviewed InputTarget.ExpectedRevision revision
                    ScreenEntries.value
                        "expectedFamilySequence"
                        (TaskInputPresentation.messageKey
                            (TaskInputId.Target InputTarget.ExpectedFamilySequence)
                            "label")
                        Prominence.Detail
                        (PresentedValue.Identifier(Sequence.format sequence))
                ]
            | Binding.Work(_, revision) -> [ reviewed InputTarget.ExpectedWorkRevision revision ]
            | Binding.Reminder(_, _, revision) ->
                [ reviewed InputTarget.ExpectedReminderRevision revision ]
            | Binding.People(_, revision) -> [ reviewed InputTarget.ExpectedRevision revision ]
            | Binding.CaseNew _
            | Binding.PeopleNew _
            | Binding.ReminderNew _
            | Binding.PartyNew _
            | Binding.Party _
            | Binding.NoteNew _
            | Binding.Note _ -> []

        let sections =
            described.Effects
            @ (if described.Unchanged.IsEmpty then
                   []
               else
                   [ ScreenEntries.section "label.unchanged" described.Unchanged ])

        let operationIdentity =
            operationId
            |> Option.map (fun (operationId: System.Guid) ->
                ScreenEntries.value
                    "operationId"
                    "field.operationId.label"
                    Prominence.Detail
                    (PresentedValue.Identifier(operationId.ToString("D"))))
            |> Option.toList

        let partIdentities =
            review.Changes
            |> List.map (fun part ->
                ScreenEntries.partIdentity (PartDescription.kind part.Description) part.PartId)

        let details =
            operationIdentity
            @ partIdentities
            @ reviewedVersion
            @ (sections |> List.collect _.Entries |> List.filter ScreenEntries.technical)

        let facts =
            sections
            |> List.map (fun section ->
                { section with
                    Entries = section.Entries |> List.filter (ScreenEntries.technical >> not)
                })

        let screen =
            Screen.create (
                identity
                @ [
                    ScreenEntries.region
                        RegionRole.Notice
                        None
                        (RegionContent.Notice
                            {
                                MessageId = PresentationKeys.consequence review.Task
                                Reason = None
                                Tone =
                                    if
                                        TaskConsequences.ofTask review.Task = Consequence.Routine
                                    then
                                        NoticeTone.Info
                                    else
                                        NoticeTone.Warning
                            })
                ]
                @ ScreenEntries.factRegions facts
                @ (if details.IsEmpty then
                       []
                   else
                       [
                           ScreenEntries.entryRegion
                               RegionRole.Details
                               (Some "label.recordDetails")
                               details
                       ])
            )

        let presentation: ReviewPresentation =
            {
                Changes = described.Changes
                Unchanged =
                    described.Unchanged
                    |> List.map (fun entry ->
                        {
                            PresentedField.Key = entry.Key
                            LabelId = entry.LabelId
                        })
            }

        screen, presentation
