namespace ClaimCore.Application.Execution

open ClaimCore.Application.Reads

open ClaimCore.Application.Storage

open ClaimCore.Application

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
