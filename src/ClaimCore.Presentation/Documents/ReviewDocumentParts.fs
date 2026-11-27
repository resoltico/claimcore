namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts

module internal ReviewDocumentParts =
    let describe operationId principal inputs (review: OperationReview) =
        let described = ReviewScreen.operationReview inputs review

        let identityEntries =
            [
                ScreenEntries.value
                    "task"
                    "label.action"
                    Prominence.Primary
                    (PresentedValue.Message("task." + TaskId.token review.Task + ".label"))
                match principal, review.Target with
                | Some principal, _ -> yield! ScreenEntries.principalIdentity principal
                | None, ReviewTarget.Case(_, reference) ->
                    ScreenEntries.value
                        "reference"
                        "field.caseReference.label"
                        Prominence.Primary
                        (PresentedValue.Reference reference)
                | None, ReviewTarget.Opening ->
                    ScreenEntries.value
                        "reference"
                        "field.caseReference.label"
                        Prominence.Primary
                        (PresentedValue.Message "label.caseReferencePending")
                | None, ReviewTarget.Person actor ->
                    ScreenEntries.value
                        "target"
                        "label.targetAccount"
                        Prominence.Primary
                        (ScreenEntries.optional PresentedValue.Actor actor)
                match review.Binding with
                | Binding.Attachment(_, attachmentId) ->
                    ScreenEntries.value
                        "attachmentId"
                        "field.attachmentId.label"
                        Prominence.Secondary
                        (PresentedValue.Identifier(AttachmentId.format attachmentId))
                | _ -> ()
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
            | Binding.AttachmentNew _
            | Binding.Attachment _
            | Binding.NoteNew _
            | Binding.LinkNew _
            | Binding.Link _
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

        let notices =
            [
                match review.Target with
                | ReviewTarget.Opening -> ()
                | _ ->
                    {
                        MessageId = PresentationKeys.consequence review.Task
                        Reason = None
                        Tone =
                            if TaskConsequences.ofTask review.Task = Consequence.Routine then
                                NoticeTone.Info
                            else
                                NoticeTone.Warning
                    }
                if review.ChangesOwnSignInIdentity then
                    {
                        MessageId = "notice.changesOwnSignInIdentity"
                        Reason = None
                        Tone = NoticeTone.Warning
                    }
            ]

        let screen =
            Screen.create (
                identity
                @ (if notices.IsEmpty then
                       []
                   else
                       [
                           ScreenEntries.region
                               RegionRole.Notice
                               None
                               (RegionContent.Notice notices)
                       ])
                @ ScreenEntries.factRegions facts
                @ (if details.IsEmpty then
                       []
                   else
                       [
                           ScreenEntries.entryRegion
                               RegionRole.Details
                               (Some "label.technicalDetails")
                               details
                       ])
            )

        let declaration = TaskDeclarations.describe (TaskId.token review.Task)
        let paired = FieldPresentation.declarations |> List.choose _.Pairs |> Set.ofList

        let entered input =
            match Map.tryFind input inputs with
            | None
            | Some InputValue.Null -> false
            | Some _ -> true

        let notEntered =
            if not declaration.Creates then
                []
            else
                declaration.Inputs
                |> List.filter (fun input ->
                    input.Mode = TaskInputMode.Optional
                    && input.Source = InputSource.Authored
                    && not (entered input.Input)
                    && (match input.Input with
                        | TaskInputId.Target(InputTarget.Field field) ->
                            not (Set.contains field paired)
                            && (field <> BusinessField.CaseReference || review.Opening.IsNone)
                        | _ -> true))
                |> List.map (fun input -> TaskInputPresentation.messageKey input.Input "label")

        let presentation: ReviewPresentation =
            {
                Changes = described.Changes
                NotEntered = notEntered
                Unchanged =
                    described.Unchanged
                    |> List.map (fun entry ->
                        {
                            PresentedField.Key = entry.Key
                            LabelId = entry.LabelId
                        })
            }

        screen, presentation
