module ClaimCore.TestSupport.Reviews

open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts

/// Codec and rendering fixtures use the same accepted-part descriptions as real replies.
let caseReview task binding (before: CaseSnapshot option) (after: CaseSnapshot) context =
    let handling =
        CaseState.fields after.State
        |> Option.orElseWith (fun () ->
            before |> Option.bind (fun snapshot -> CaseState.fields snapshot.State))
        |> Option.map _.Handling
        |> Option.defaultValue Handling.Open

    let value (snapshot: CaseSnapshot) =
        {
            CaseValue.Reference = None
            Lifecycle = CaseState.lifecycle snapshot.State
            Handling =
                CaseState.fields snapshot.State
                |> Option.map _.Handling
                |> Option.defaultValue (Handling.clearNote handling)
            Content =
                CaseState.fields snapshot.State
                |> Option.map (fun fields ->
                    {
                        Registration = fields.Registration
                        Finance = fields.Finance
                    })
            Disposition = None
            ReopenReason = None
        }

    {
        OperationReview.Task = task
        Target = ReviewTarget.Case(after.CaseId, after.Reference)
        Binding = binding
        References = { Actors = []; Cases = [] }
        Context = context
        Changes =
            [
                {
                    PartId = CaseId.value after.CaseId
                    Revision = after.Revision
                    Description =
                        PartDescription.Case(
                            CaseDescription.describe
                                after.Reference
                                (before |> Option.map value)
                                (value after)
                        )
                }
            ]
    }

let workReview
    task
    binding
    (parent: CaseSnapshot)
    (before: CaseWorkReadModel)
    (after: CaseWorkReadModel)
    context
    =
    let value (work: CaseWorkReadModel) =
        {
            CaseWork.Responsible = work.Responsible |> Option.map _.ActorId
            Summary = work.Summary
        }

    let actors =
        [
            yield! before.Responsible |> Option.toList
            yield! after.Responsible |> Option.toList
        ]
        |> List.map (fun actor -> actor.ActorId, actor)
        |> Map.ofList
        |> Map.values
        |> Seq.toList

    {
        OperationReview.Task = task
        Binding = binding
        Target = ReviewTarget.Case(parent.CaseId, parent.Reference)
        References = { Actors = actors; Cases = [] }
        Context = context
        Changes =
            [
                {
                    PartId = CaseId.value parent.CaseId
                    Revision = after.Revision
                    Description =
                        PartDescription.Work(
                            WorkDescription.describe (Some(value before)) (Some(value after))
                        )
                }
            ]
    }

let reminderReview
    task
    binding
    (parent: CaseSnapshot)
    (before: Reminder option)
    (after: Reminder list)
    context
    =
    let value reminder =
        {
            ReminderValue.Plan =
                Some
                    {
                        Action = Reminder.action reminder
                        Due = Reminder.due reminder
                    }
            State = Reminder.state reminder
        }

    {
        OperationReview.Task = task
        Binding = binding
        Target = ReviewTarget.Case(parent.CaseId, parent.Reference)
        References = { Actors = []; Cases = [] }
        Context = context
        Changes =
            after
            |> List.map (fun reminder ->
                let prior =
                    before
                    |> Option.filter (fun before -> Reminder.id before = Reminder.id reminder)
                    |> Option.map value

                {
                    PartId = ReminderId.value (Reminder.id reminder)
                    Revision = Reminder.revision reminder
                    Description =
                        PartDescription.Reminder(
                            ReminderDescription.describe prior (value reminder)
                        )
                })
    }

let personReview
    task
    binding
    target
    id
    revision
    (description: PersonPartDescription)
    references
    context
    =
    {
        OperationReview.Task = task
        Binding = binding
        Target = ReviewTarget.Person target
        References = references
        Context = context
        Changes =
            [
                {
                    PartId = id
                    Revision = revision
                    Description = PartDescription.Person description
                }
            ]
    }
