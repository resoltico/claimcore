namespace ClaimCore.Protocol

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type RevisionInput =
    | New
    | Expected


module CaseActAnnotations =
    let revision kind =
        match kind with
        | CaseActKind.Open -> RevisionInput.New
        | CaseActKind.AmendRegistration
        | CaseActKind.Correct
        | CaseActKind.Decide
        | CaseActKind.WithdrawDecision
        | CaseActKind.RecordPayment
        | CaseActKind.ClearPayment
        | CaseActKind.Close
        | CaseActKind.Reopen
        | CaseActKind.Void
        | CaseActKind.Reinstate
        | CaseActKind.Erase -> RevisionInput.Expected

    let prefill kind =
        match kind with
        | CaseActKind.AmendRegistration
        | CaseActKind.Correct
        | CaseActKind.Decide -> true
        | CaseActKind.Open
        | CaseActKind.WithdrawDecision
        | CaseActKind.RecordPayment
        | CaseActKind.ClearPayment
        | CaseActKind.Close
        | CaseActKind.Reopen
        | CaseActKind.Void
        | CaseActKind.Reinstate
        | CaseActKind.Erase -> false
