namespace ClaimCore.Domain

open ClaimCore.Contract

module CaseActs =
    let kind =
        function
        | CaseAct.Open _ -> CaseActKind.Business BusinessActKind.Open
        | CaseAct.AmendRegistration _ -> CaseActKind.Business BusinessActKind.AmendRegistration
        | CaseAct.CorrectCase _ -> CaseActKind.Business BusinessActKind.CorrectCase
        | CaseAct.Decide _ -> CaseActKind.Business BusinessActKind.Decide
        | CaseAct.WithdrawDecision -> CaseActKind.Business BusinessActKind.WithdrawDecision
        | CaseAct.RecordPayment _ -> CaseActKind.Business BusinessActKind.RecordPayment
        | CaseAct.ClearPayment -> CaseActKind.Business BusinessActKind.ClearPayment
        | CaseAct.Close -> CaseActKind.Business BusinessActKind.Close
        | CaseAct.Reopen -> CaseActKind.Business BusinessActKind.Reopen
        | CaseAct.Void _ -> CaseActKind.Stewardship StewardshipActKind.Void
        | CaseAct.Reinstate _ -> CaseActKind.Stewardship StewardshipActKind.Reinstate
        | CaseAct.Erase -> CaseActKind.Stewardship StewardshipActKind.Erase

    let name act = kind act |> CaseActKinds.token

module BusinessCommands =
    let kind act =
        match CaseActs.kind act with
        | CaseActKind.Business kind -> kind
        | CaseActKind.Stewardship _ -> invalidArg (nameof act) "Expected a business act."

    let name act = CaseActs.name act
