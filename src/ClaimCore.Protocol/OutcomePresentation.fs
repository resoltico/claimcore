namespace ClaimCore.Protocol

open ClaimCore.Domain

module OutcomePresentation =
    let exitCode outcome =
        Outcome.fold
            (fun _ -> ExitCode.Success)
            (fun _ -> ExitCode.Rejected)
            (fun _ -> ExitCode.Failed)
            outcome

    let recommendedAction outcome =
        Outcome.fold
            (fun _ -> RecommendedAction.NoneRequired)
            Refusals.action
            FaultAdvice.action
            outcome

    let executeExitCode =
        function
        | ExecuteResult.Accepted _
        | ExecuteResult.AlreadyAccepted _ -> exitCode (Outcome.Succeeded())
        | ExecuteResult.Rejected refusal -> exitCode (Outcome.Rejected refusal)
        | ExecuteResult.Failed fault -> exitCode (Outcome.Failed fault)
        | ExecuteResult.OutcomeUnknown -> ExitCode.OutcomeUnknown

    let resolveExitCode =
        function
        | Outcome.Succeeded Resolved.NotAccepted -> ExitCode.Rejected
        | outcome -> exitCode outcome
