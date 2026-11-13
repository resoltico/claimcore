namespace ClaimCore.Presentation

open ClaimCore.Domain

open ClaimCore.Application.Contracts
open ClaimCore.Presentation

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module internal ReviewDocumentParts =
    /// The server computes presentation once from authoritative typed review facts.
    let ofCore input review =
        let described = ReviewScreen.operationReview input review

        let parent =
            match review with
            | OperationReview.Case value -> Some(defaultArg value.Before value.Proposed)
            | OperationReview.Note value -> Some value.Parent
            | OperationReview.Work value -> Some value.Parent
            | OperationReview.Reminder value -> Some value.Parent
            | OperationReview.People _ -> None

        let reminderContext =
            match review with
            | OperationReview.Reminder { Before = Some reminder } ->
                [
                    ScreenEntries.value
                        "reminderAction"
                        "field.reminderAction.label"
                        Prominence.Primary
                        (PresentedValue.Text(ReminderAction.format (Reminder.action reminder)))
                ]
            | _ -> []

        let action =
            input
            |> Option.map (fun input ->
                ScreenEntries.value
                    "act"
                    "label.action"
                    Prominence.Primary
                    (ScreenEntries.code ActKind.vocabulary (ActKind.ofInput input)))
            |> Option.toList

        let identity =
            let entries =
                match parent, review with
                | Some snapshot, _ ->
                    [
                        ScreenEntries.value
                            "caseReference"
                            "field.caseReference.label"
                            Prominence.Primary
                            (PresentedValue.Reference snapshot.Reference)
                    ]
                    @ CaseScreen.identityFacts snapshot
                    @ reminderContext
                | None, OperationReview.People value ->
                    [
                        ScreenEntries.value
                            "target"
                            "label.targetAccount"
                            Prominence.Primary
                            (match value.Target, value.After.Profile with
                             | Some target, _ -> PresentedValue.Actor target.Actor
                             | None, Some profile -> PresentedValue.Party profile.DisplayName
                             | _ -> PresentedValue.Absent "label.notRecorded")
                    ]
                | _ -> []

            if (action @ entries).IsEmpty then
                []
            else
                [ ScreenEntries.entryRegion RegionRole.Identity None (action @ entries) ]

        let revision target value =
            ScreenEntries.value
                (TaskInputId.key (TaskInputId.Target target))
                (TaskInputPresentation.messageKey (TaskInputId.Target target) "label")
                Prominence.Detail
                (PresentedValue.Identifier(Revision.format value))

        let reviewedVersion =
            match review with
            | OperationReview.Case value ->
                value.Before
                |> Option.map (fun before -> revision InputTarget.ExpectedRevision before.Revision)
                |> Option.toList
            | OperationReview.Work value ->
                [ revision InputTarget.ExpectedWorkRevision value.Before.Revision ]
            | OperationReview.Reminder value ->
                value.Before
                |> Option.map (fun before ->
                    revision InputTarget.ExpectedReminderRevision (Reminder.revision before))
                |> Option.toList
            | OperationReview.Note _
            | OperationReview.People _ -> []

        let sections = described.Effects

        let operationIdentity =
            input
            |> Option.map (fun input ->
                ScreenEntries.value
                    "operationId"
                    "field.operationId.label"
                    Prominence.Detail
                    (PresentedValue.Identifier((OperationInput.operationId input).ToString("D"))))
            |> Option.toList

        let details =
            operationIdentity
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
