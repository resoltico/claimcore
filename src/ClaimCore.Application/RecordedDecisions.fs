namespace ClaimCore.Application

open ClaimCore.Domain

/// Typed inputs reconstructed from the owning root for independent decisions.
type internal WorkDecisionInput =
    {
        CaseId: CaseId
        ExpectedRevision: Revision
        Update: ValidCaseWorkUpdate
    }

type internal ReminderDecisionInput =
    {
        OperationId: OperationId
        Command: ReminderCommand
    }

type internal NoteDecisionInput =
    {
        OperationId: OperationId
        Command: NoteCommand
    }

/// Reuse Domain effects when checking a retained request against its preceding entity version.
module internal RecordedDecisions =
    let work (request: WorkDecisionInput) (before: CaseWorkSnapshot) =
        if request.CaseId <> before.CaseId then
            Error(Refusal.Access AccessRefusal.ResourceUnavailable)
        else
            CaseWorkDecision.apply request.ExpectedRevision request.Update before

    let reminder (request: ReminderDecisionInput) before =
        Reminder.decideEffects
            request.OperationId
            (ReminderCommand.parent request.Command)
            true
            before
            false
            request.Command
