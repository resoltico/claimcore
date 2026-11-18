namespace ClaimCore.Domain.Tasks

open ClaimCore.Domain

/// The only inventory of ledgered tasks, including the operator-only initializer.
module Registry =
    let all =
        [
            ClaimCore.Domain.Tasks.Case.Open.task
            ClaimCore.Domain.Tasks.Case.AmendRegistration.task
            ClaimCore.Domain.Tasks.Case.ChangeReference.task
            ClaimCore.Domain.Tasks.Case.Correct.task
            ClaimCore.Domain.Tasks.Case.Decide.task
            ClaimCore.Domain.Tasks.Case.WithdrawDecision.task
            ClaimCore.Domain.Tasks.Case.RecordPayment.task
            ClaimCore.Domain.Tasks.Case.RemovePayment.task
            ClaimCore.Domain.Tasks.Case.CorrectPayment.task
            ClaimCore.Domain.Tasks.Case.Void.task
            ClaimCore.Domain.Tasks.Case.Reinstate.task
            ClaimCore.Domain.Tasks.Case.Close.task
            ClaimCore.Domain.Tasks.Case.Reopen.task
            ClaimCore.Domain.Tasks.Case.Erase.task
            ClaimCore.Domain.Tasks.People.ProvisionOwner.task
            ClaimCore.Domain.Tasks.People.Register.task
            ClaimCore.Domain.Tasks.People.Grant.task
            ClaimCore.Domain.Tasks.People.Revoke.task
            ClaimCore.Domain.Tasks.People.Enable.task
            ClaimCore.Domain.Tasks.People.Disable.task
            ClaimCore.Domain.Tasks.People.SetProfile.task
            ClaimCore.Domain.Tasks.People.EraseProfile.task
            ClaimCore.Domain.Tasks.People.Admit.task
            ClaimCore.Domain.Tasks.Work.Update.task
            ClaimCore.Domain.Tasks.Party.PartyTasks.add
            ClaimCore.Domain.Tasks.Party.PartyTasks.update
            ClaimCore.Domain.Tasks.Party.PartyTasks.remove
            ClaimCore.Domain.Tasks.Note.Add.task
            ClaimCore.Domain.Tasks.Note.Remove.task
            ClaimCore.Domain.Tasks.Link.Add.task
            ClaimCore.Domain.Tasks.Link.Remove.task
            ClaimCore.Domain.Tasks.Reminder.Create.task
            ClaimCore.Domain.Tasks.Reminder.Update.task
            ClaimCore.Domain.Tasks.Reminder.Complete.task
            ClaimCore.Domain.Tasks.Reminder.Cancel.task
            ClaimCore.Domain.Tasks.Reminder.Reopen.task
            ClaimCore.Domain.Tasks.Reminder.CompleteAndPlanNext.task
        ]

    let declarations = all |> List.map Task.declaration

    let clientDeclarations =
        declarations |> List.filter (fun task -> task.Exposure = TaskExposure.Client)

    let private byId =
        all |> List.map (fun task -> (Task.declaration task).Id, task) |> Map.ofList

    let find id = Map.tryFind id byId
    let findDeclaration id = find id |> Option.map Task.declaration
    let initialOwnerRoles = ClaimCore.Domain.Tasks.People.ProvisionOwner.initialRoles

    let validateInitialOwnerRoles =
        ClaimCore.Domain.Tasks.People.ProvisionOwner.validateRoles

    /// Tasks own recommendation ranks; this common selection never recommends a blocked task.
    let recommendation (offers: (TaskDeclaration * Availability) list) =
        offers
        |> List.choose (fun (declaration, available) ->
            if available.Blocked.IsSome then
                None
            else
                available.Recommendation |> Option.map (fun rank -> rank, declaration.Id))
        |> List.sortBy fst
        |> List.tryHead
        |> Option.map snd
