namespace ClaimCore.Domain

open ClaimCore.Catalog

module BusinessCommands =
    let kind command =
        match command with
        | BusinessCommand.Open _ -> BusinessActKind.Open
        | BusinessCommand.AmendRegistration _ -> BusinessActKind.AmendRegistration
        | BusinessCommand.CorrectCase _ -> BusinessActKind.CorrectCase
        | BusinessCommand.Decide _ -> BusinessActKind.Decide
        | BusinessCommand.WithdrawDecision -> BusinessActKind.WithdrawDecision
        | BusinessCommand.RecordPayment _ -> BusinessActKind.RecordPayment
        | BusinessCommand.ClearPayment -> BusinessActKind.ClearPayment
        | BusinessCommand.Close -> BusinessActKind.Close
        | BusinessCommand.Reopen -> BusinessActKind.Reopen

    let name command =
        command |> kind |> CaseActKind.Business |> CaseActKinds.token

module CaseActs =
    let kind =
        function
        | CaseAct.Business command -> BusinessCommands.kind command |> CaseActKind.Business
        | CaseAct.Stewardship(StewardshipAct.Void _) ->
            (CaseActKind.Stewardship StewardshipActKind.Void)
        | CaseAct.Stewardship(StewardshipAct.Reinstate _) ->
            (CaseActKind.Stewardship StewardshipActKind.Reinstate)
        | CaseAct.Stewardship StewardshipAct.Erase ->
            (CaseActKind.Stewardship StewardshipActKind.Erase)

    let name act = kind act |> CaseActKinds.token
