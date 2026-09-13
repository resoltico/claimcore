namespace ClaimCore.Domain

/// Stable semantic tokens shared by commands, storage, discovery and renderers.
module CommandKinds =
    let all =
        [
            CommandKind.Open
            CommandKind.AmendRegistration
            CommandKind.Decide
            CommandKind.WithdrawDecision
            CommandKind.RecordPayment
            CommandKind.ClearPayment
            CommandKind.Close
            CommandKind.Reopen
        ]

    let token kind =
        match kind with
        | CommandKind.Open -> "OPEN"
        | CommandKind.AmendRegistration -> "AMEND_REGISTRATION"
        | CommandKind.Decide -> "DECIDE"
        | CommandKind.WithdrawDecision -> "WITHDRAW_DECISION"
        | CommandKind.RecordPayment -> "RECORD_PAYMENT"
        | CommandKind.ClearPayment -> "CLEAR_PAYMENT"
        | CommandKind.Close -> "CLOSE"
        | CommandKind.Reopen -> "REOPEN"

module Commands =
    let kind command =
        match command with
        | Command.Open _ -> CommandKind.Open
        | Command.AmendRegistration _ -> CommandKind.AmendRegistration
        | Command.Decide _ -> CommandKind.Decide
        | Command.WithdrawDecision -> CommandKind.WithdrawDecision
        | Command.RecordPayment _ -> CommandKind.RecordPayment
        | Command.ClearPayment -> CommandKind.ClearPayment
        | Command.Close -> CommandKind.Close
        | Command.Reopen -> CommandKind.Reopen

    let name command = command |> kind |> CommandKinds.token

module CaseStatuses =
    let all = [ CaseStatus.Opened; CaseStatus.Closed ]

    let token status =
        match status with
        | CaseStatus.Opened -> "OPENED"
        | CaseStatus.Closed -> "CLOSED"

    let tryParse value =
        all |> List.tryFind (fun status -> token status = value)
