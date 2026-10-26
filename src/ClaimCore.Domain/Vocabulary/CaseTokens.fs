namespace ClaimCore.Domain

module BusinessActKinds =
    let vocabulary =
        Vocabulary.define
            "act"
            [
                BusinessActKind.Open
                BusinessActKind.AmendRegistration
                BusinessActKind.Correct
                BusinessActKind.Decide
                BusinessActKind.WithdrawDecision
                BusinessActKind.RecordPayment
                BusinessActKind.ClearPayment
                BusinessActKind.Close
                BusinessActKind.Reopen
            ]
            (function
             | BusinessActKind.Open -> "OPEN"
             | BusinessActKind.AmendRegistration -> "AMEND_REGISTRATION"
             | BusinessActKind.Correct -> "CORRECT"
             | BusinessActKind.Decide -> "DECIDE"
             | BusinessActKind.WithdrawDecision -> "WITHDRAW_DECISION"
             | BusinessActKind.RecordPayment -> "RECORD_PAYMENT"
             | BusinessActKind.ClearPayment -> "CLEAR_PAYMENT"
             | BusinessActKind.Close -> "CLOSE"
             | BusinessActKind.Reopen -> "REOPEN")

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

module StewardshipActKinds =
    let vocabulary =
        Vocabulary.define
            "act"
            [
                StewardshipActKind.Void
                StewardshipActKind.Reinstate
                StewardshipActKind.Erase
            ]
            (function
             | StewardshipActKind.Void -> "VOID"
             | StewardshipActKind.Reinstate -> "REINSTATE"
             | StewardshipActKind.Erase -> "ERASE")

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

module CaseStatuses =
    let vocabulary =
        Vocabulary.define "token" [ CaseStatus.Opened; CaseStatus.Closed ] (function
            | CaseStatus.Opened -> "OPENED"
            | CaseStatus.Closed -> "CLOSED")

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

module CaseActKinds =
    let vocabulary =
        Vocabulary.define
            "act"
            ((BusinessActKinds.all |> List.map CaseActKind.Business)
             @ (StewardshipActKinds.all |> List.map CaseActKind.Stewardship))
            (function
             | CaseActKind.Business value -> BusinessActKinds.token value
             | CaseActKind.Stewardship value -> StewardshipActKinds.token value)

    let business = BusinessActKinds.all |> List.map CaseActKind.Business
    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary
