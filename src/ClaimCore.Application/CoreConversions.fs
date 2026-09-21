namespace ClaimCore.Application

open System
open ClaimCore.Domain

/// Maps Domain's closed failure vocabulary into the typed public endpoint algebra.
module internal CoreConversions =
    let rejection error = Rejection.Domain error

    let fault (error: CoreFailure) : CoreFault =
        match error with
        | CoreFailure.Domain domain ->
            invalidArg "error" ($"Domain failure is not a core fault: {domain}.")
        | CoreFailure.IdempotencyConflict -> CoreFault.OperationContentConflict
        | CoreFailure.StoreUnavailable -> CoreFault.StoreUnavailable
        | CoreFailure.CommitOutcomeUnknown _ -> CoreFault.CommitOutcomeUnknown
        | CoreFailure.StoreCorrupt -> CoreFault.StoreIntegrityError
        | CoreFailure.SchemaMismatch -> CoreFault.SchemaMismatch

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
