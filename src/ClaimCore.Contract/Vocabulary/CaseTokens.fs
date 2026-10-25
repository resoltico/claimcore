namespace ClaimCore.Contract

module BusinessActKinds =
    let vocabulary =
        Vocabulary.define
            "command"
            [
                BusinessActKind.Open, "OPEN"
                BusinessActKind.AmendRegistration, "AMEND_REGISTRATION"
                BusinessActKind.CorrectCase, "CORRECT_CASE"
                BusinessActKind.Decide, "DECIDE"
                BusinessActKind.WithdrawDecision, "WITHDRAW_DECISION"
                BusinessActKind.RecordPayment, "RECORD_PAYMENT"
                BusinessActKind.ClearPayment, "CLEAR_PAYMENT"
                BusinessActKind.Close, "CLOSE"
                BusinessActKind.Reopen, "REOPEN"
            ]

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

module StewardshipActKinds =
    let vocabulary =
        Vocabulary.define
            "command"
            [
                StewardshipActKind.Void, "VOID"
                StewardshipActKind.Reinstate, "REINSTATE"
                StewardshipActKind.Erase, "ERASE"
            ]

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

module CaseStatuses =
    let vocabulary =
        Vocabulary.define "token" [ CaseStatus.Opened, "OPENED"; CaseStatus.Closed, "CLOSED" ]

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

module CaseActKinds =
    let vocabulary =
        Vocabulary.define
            "command"
            ((Vocabulary.entries BusinessActKinds.vocabulary
              |> List.map (fun (value, token) -> CaseActKind.Business value, token))
             @ (Vocabulary.entries StewardshipActKinds.vocabulary
                |> List.map (fun (value, token) -> CaseActKind.Stewardship value, token)))

    let business = BusinessActKinds.all |> List.map CaseActKind.Business
    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

module Dispositions =
    let vocabulary =
        Vocabulary.define "token" [ Disposition.Active, "ACTIVE"; Disposition.Voided, "VOIDED" ]

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
