namespace ClaimCore.Catalog

/// Stable semantic tokens shared by commands, storage, discovery and renderers.
module CaseActKinds =
    let business =
        [
            (CaseActKind.Business BusinessActKind.Open)
            (CaseActKind.Business BusinessActKind.AmendRegistration)
            (CaseActKind.Business BusinessActKind.CorrectCase)
            (CaseActKind.Business BusinessActKind.Decide)
            (CaseActKind.Business BusinessActKind.WithdrawDecision)
            (CaseActKind.Business BusinessActKind.RecordPayment)
            (CaseActKind.Business BusinessActKind.ClearPayment)
            (CaseActKind.Business BusinessActKind.Close)
            (CaseActKind.Business BusinessActKind.Reopen)
        ]

    let all =
        business
        @ [
            (CaseActKind.Stewardship StewardshipActKind.Void)
            (CaseActKind.Stewardship StewardshipActKind.Reinstate)
            (CaseActKind.Stewardship StewardshipActKind.Erase)
        ]

    let token kind =
        match kind with
        | (CaseActKind.Business BusinessActKind.Open) -> "OPEN"
        | (CaseActKind.Business BusinessActKind.AmendRegistration) -> "AMEND_REGISTRATION"
        | (CaseActKind.Business BusinessActKind.CorrectCase) -> "CORRECT_CASE"
        | (CaseActKind.Business BusinessActKind.Decide) -> "DECIDE"
        | (CaseActKind.Business BusinessActKind.WithdrawDecision) -> "WITHDRAW_DECISION"
        | (CaseActKind.Business BusinessActKind.RecordPayment) -> "RECORD_PAYMENT"
        | (CaseActKind.Business BusinessActKind.ClearPayment) -> "CLEAR_PAYMENT"
        | (CaseActKind.Business BusinessActKind.Close) -> "CLOSE"
        | (CaseActKind.Business BusinessActKind.Reopen) -> "REOPEN"
        | (CaseActKind.Stewardship StewardshipActKind.Void) -> "VOID"
        | (CaseActKind.Stewardship StewardshipActKind.Reinstate) -> "REINSTATE"
        | (CaseActKind.Stewardship StewardshipActKind.Erase) -> "ERASE"

    let tryParse value =
        all |> List.tryFind (token >> (=) value)

module CaseStatuses =
    let all = [ CaseStatus.Opened; CaseStatus.Closed ]

    let token status =
        match status with
        | CaseStatus.Opened -> "OPENED"
        | CaseStatus.Closed -> "CLOSED"

    let tryParse value =
        all |> List.tryFind (fun status -> token status = value)
