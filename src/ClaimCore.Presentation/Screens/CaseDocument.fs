namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

module HumanCaseDocument =
    let ofCore (value: CaseReadModel) =
        let disclosed = CaseState.lifecycle value.Snapshot.State <> Lifecycle.Erased

        let actions =
            if not disclosed then
                []
            else
                let controls =
                    value.Advice.Offers
                    |> List.choose (function
                        | TaskOffer.Available(task, binding, availability) ->
                            let action =
                                TaskAffordances.offered
                                    task
                                    (Some binding)
                                    availability.Seeds
                                    availability.Editable
                                    availability.Choices

                            let action =
                                let input = TaskAffordances.field BusinessField.PaymentDate

                                if List.contains input action.Editable then
                                    { action with
                                        Seeds =
                                            Map.add
                                                input
                                                (InputValue.Text(
                                                    value.Work.Basis.BusinessDate.ToString(
                                                        CalendarDate.ExactFormat,
                                                        System.Globalization.CultureInfo
                                                            .InvariantCulture
                                                    )
                                                ))
                                                action.Seeds
                                    }
                                else
                                    action

                            let labelId =
                                let hasDecision =
                                    CaseState.fields value.Snapshot.State
                                    |> Option.exists (fun facts -> facts.Finance.Decision.IsSome)

                                if task = ClientCompositions.paymentDecision && hasDecision then
                                    "task.case.decide.change"
                                else
                                    action.LabelId

                            Some
                                { action with
                                    LabelId = labelId
                                    ShortLabelId =
                                        if labelId = "task.case.decide.change" then
                                            Some "task.case.decide.changeShortLabel"
                                        else
                                            action.ShortLabelId
                                    Blocked =
                                        availability.Blocked
                                        |> Option.map TaskAffordances.availability
                                }
                        | TaskOffer.Declared _ -> None)

                let offered, blocked =
                    controls |> List.partition (fun control -> control.Blocked.IsNone)

                let work =
                    if value.Work.CanUpdateSummary || value.Work.CanAssign then
                        let seeds =
                            [
                                if value.Work.CanUpdateSummary then
                                    match value.Work.Summary with
                                    | Some value ->
                                        TaskAffordances.field BusinessField.HandlingSummary,
                                        TaskAffordances.text (HandlingSummary.format value)
                                    | None -> ()
                                if value.Work.CanAssign then
                                    match value.Work.Responsible with
                                    | Some actor ->
                                        TaskAffordances.field BusinessField.ResponsibleActor,
                                        TaskAffordances.text (ActorId.format actor.ActorId)
                                    | None -> ()
                            ]
                            |> Map.ofList

                        let editable =
                            [
                                if value.Work.CanUpdateSummary then
                                    TaskAffordances.field BusinessField.HandlingSummary
                                if value.Work.CanAssign then
                                    TaskAffordances.field BusinessField.ResponsibleActor
                            ]

                        [
                            TaskAffordances.work
                                value.Snapshot.CaseId
                                value.Work.Revision
                                seeds
                                editable
                        ]
                    else
                        []

                offered
                @ blocked
                @ work
                @ [
                    if value.Work.CanCreateReminder then
                        yield TaskAffordances.reminderCreate value.Snapshot.CaseId
                    if value.Notes.CanAdd then
                        yield TaskAffordances.noteAdd value.Snapshot.CaseId
                ]

        let declarationInputs (action: Affordance) =
            TaskDeclarations.find (TaskId.token action.Task)
            |> Option.map _.Inputs
            |> Option.defaultValue []

        let isRecommended (action: Affordance) =
            action.Blocked.IsNone && value.Advice.Recommended = Some action.Task

        let supplies field mode (action: Affordance) =
            action.Blocked.IsNone
            && (declarationInputs action
                |> List.exists (fun input ->
                    input.Input = TaskAffordances.field field
                    && input.Source = InputSource.Authored
                    && input.Mode = mode))

        let paymentControls =
            actions
            |> List.filter (supplies BusinessField.PaymentDate TaskInputMode.Required)

        let decisionControls =
            actions
            |> List.filter (fun action ->
                not (isRecommended action)
                && supplies BusinessField.PayableAmount TaskInputMode.Required action)

        let registrationControls =
            actions
            |> List.filter (fun action ->
                not (isRecommended action)
                && supplies BusinessField.ReceivedDate TaskInputMode.Update action
                && not (
                    declarationInputs action
                    |> List.exists (fun input ->
                        match input.Input with
                        | TaskInputId.Target(InputTarget.Group _) -> true
                        | _ -> false)
                ))

        let recommended, actions = actions |> List.partition isRecommended

        let placed =
            registrationControls @ decisionControls |> List.map _.Task |> Set.ofList

        let actions =
            actions |> List.filter (fun action -> not (Set.contains action.Task placed))

        let screen = CaseScreen.view value

        let screen =
            let regions =
                screen.Regions
                |> List.map (fun region ->
                    match region.Role with
                    | RegionRole.Facts ->
                        { region with
                            Actions = registrationControls
                        }
                    | RegionRole.Payments ->
                        { region with
                            Actions = decisionControls
                        }
                    | _ -> region)

            let context =
                regions
                |> List.tryPick (fun region ->
                    match region.Role, region.Content with
                    | RegionRole.Payments, RegionContent.Table table when
                        not paymentControls.IsEmpty
                        ->
                        let entries =
                            table.Sections
                            |> List.collect _.Entries
                            |> List.filter (fun entry -> entry.Key = "remaining")

                        let context = ScreenEntries.entryRegion RegionRole.Context None entries

                        Some
                            { context with
                                Actions = paymentControls
                            }
                    | _ -> None)

            Screen.create (regions @ Option.toList context)
            |> Screen.withCurrentGroups screen.CurrentGroups

        let screen =
            if recommended.IsEmpty then
                screen
            else
                let region = ScreenEntries.entryRegion RegionRole.Recommendation None []

                Screen.create ({ region with Actions = recommended } :: screen.Regions)
                |> Screen.withCurrentGroups screen.CurrentGroups

        HumanDocument.create
            (DocumentSubject.Case(
                value.Snapshot.CaseId,
                value.Snapshot.Revision,
                value.Work.Revision
            ))
            screen
            actions
            (if disclosed then
                 [
                     {
                         NamedLink.Relation = LinkRelation.View
                         LabelId = "label.caseReminders"
                         Link =
                             Link.remindersForCase (
                                 CaseSelector.Reference(
                                     CaseReference.format value.Snapshot.Reference
                                 )
                             )
                     }
                     {
                         Relation = LinkRelation.Attachments
                         LabelId = "section.attachments.label"
                         Link =
                             Link.CaseAttachments
                                 {
                                     Target = CaseSelector.Id value.Snapshot.CaseId
                                 }
                     }
                 ]
             else
                 [])
            None
            (ObservationBasis.Snapshot value.Work.Basis)
            (if disclosed then
                 HumanDisclosure.Current
             else
                 HumanDisclosure.Erased)
            (Some value.Advice.History)
