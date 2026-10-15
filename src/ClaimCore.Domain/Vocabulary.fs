namespace ClaimCore.Domain

/// Stable semantic tokens shared by commands, storage, discovery and renderers.
module CaseActKinds =
    let business =
        [
            CaseActKind.Open
            CaseActKind.AmendRegistration
            CaseActKind.CorrectCase
            CaseActKind.Decide
            CaseActKind.WithdrawDecision
            CaseActKind.RecordPayment
            CaseActKind.ClearPayment
            CaseActKind.Close
            CaseActKind.Reopen
        ]

    let all = business @ [ CaseActKind.Void; CaseActKind.Reinstate; CaseActKind.Erase ]

    let token kind =
        match kind with
        | CaseActKind.Open -> "OPEN"
        | CaseActKind.AmendRegistration -> "AMEND_REGISTRATION"
        | CaseActKind.CorrectCase -> "CORRECT_CASE"
        | CaseActKind.Decide -> "DECIDE"
        | CaseActKind.WithdrawDecision -> "WITHDRAW_DECISION"
        | CaseActKind.RecordPayment -> "RECORD_PAYMENT"
        | CaseActKind.ClearPayment -> "CLEAR_PAYMENT"
        | CaseActKind.Close -> "CLOSE"
        | CaseActKind.Reopen -> "REOPEN"
        | CaseActKind.Void -> "VOID"
        | CaseActKind.Reinstate -> "REINSTATE"
        | CaseActKind.Erase -> "ERASE"

module BusinessCommands =
    let kind command =
        match command with
        | BusinessCommand.Open _ -> CaseActKind.Open
        | BusinessCommand.AmendRegistration _ -> CaseActKind.AmendRegistration
        | BusinessCommand.CorrectCase _ -> CaseActKind.CorrectCase
        | BusinessCommand.Decide _ -> CaseActKind.Decide
        | BusinessCommand.WithdrawDecision -> CaseActKind.WithdrawDecision
        | BusinessCommand.RecordPayment _ -> CaseActKind.RecordPayment
        | BusinessCommand.ClearPayment -> CaseActKind.ClearPayment
        | BusinessCommand.Close -> CaseActKind.Close
        | BusinessCommand.Reopen -> CaseActKind.Reopen

    let name command = command |> kind |> CaseActKinds.token

module CaseStatuses =
    let all = [ CaseStatus.Opened; CaseStatus.Closed ]

    let token status =
        match status with
        | CaseStatus.Opened -> "OPENED"
        | CaseStatus.Closed -> "CLOSED"

    let tryParse value =
        all |> List.tryFind (fun status -> token status = value)

module CaseActs =
    let kind =
        function
        | CaseAct.Business command -> BusinessCommands.kind command
        | CaseAct.Stewardship(StewardshipAct.Void _) -> CaseActKind.Void
        | CaseAct.Stewardship(StewardshipAct.Reinstate _) -> CaseActKind.Reinstate
        | CaseAct.Stewardship StewardshipAct.Erase -> CaseActKind.Erase

    let name act = kind act |> CaseActKinds.token
