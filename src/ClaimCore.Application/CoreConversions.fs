namespace ClaimCore.Application

open System
open ClaimCore.Domain

/// Maps Domain's closed failure vocabulary into the typed public endpoint algebra.
module internal CoreConversions =
    let rejection error = Rejection.Domain error

    let fault (error: CoreFailure) : CoreFault =
        let code, message, action =
            match error with
            | CoreFailure.Domain domain ->
                invalidArg "error" ($"Domain failure is not a core fault: {domain}.")
            | CoreFailure.IdempotencyConflict ->
                FaultCode.TechnicalMutationUnknown,
                "The operation ID belongs to different request content.",
                RecommendedAction.StopAndInvestigate
            | CoreFailure.StoreUnavailable ->
                FaultCode.StoreUnavailable,
                "The store was unavailable before completion was confirmed.",
                RecommendedAction.RetrySafe
            | CoreFailure.CommitOutcomeUnknown _ ->
                FaultCode.CommitOutcomeUnknown,
                "Commit completion was not confirmed.",
                RecommendedAction.RecoverExact
            | CoreFailure.StoreCorrupt ->
                FaultCode.StoreIntegrityError,
                "Stored data failed integrity validation.",
                RecommendedAction.StopAndInvestigate
            | CoreFailure.SchemaMismatch ->
                FaultCode.SchemaMismatch,
                "The runtime schema or database settings are incompatible.",
                RecommendedAction.StopAndInvestigate

        {
            Code = code
            Message = message
            Action = action
        }

    let currentCase claim =
        {
            Record = Claim.view claim
            AvailableCommands =
                Claim.availableCommands claim
                |> List.map (fun token ->
                    CommandKinds.all |> List.find (fun kind -> CommandKinds.token kind = token))
        }

    let receipt (value: Receipt) =
        {
            OperationId = value.OperationId
            Snapshot = Claim.view value.Case
            RecordedAt = value.RecordedAt
            RecordedBy = value.RecordedBy
            Replayed = value.Replayed
            Command =
                CommandKinds.all
                |> List.find (fun kind -> CommandKinds.token kind = value.CommandName)
        }
