namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.RecordFormat

/// Reuse Domain effects when checking a retained request against its preceding entity version.
module internal RecordedDecisions =
    let work (request: WorkRequestRecord) (before: CaseWorkSnapshot) =
        if request.CaseId <> before.CaseId then
            Error(Refusal.Access AccessRefusal.ResourceUnavailable)
        else
            CaseWorkDecision.apply request.ExpectedRevision request.Update before

    let reminder (request: ReminderRequestRecord) before =
        Reminder.decideEffects
            request.OperationId
            (ReminderCommand.parent request.Command)
            true
            before
            false
            request.Command
