namespace ClaimCore.Application

open System
open ClaimCore.Domain

/// Maps Domain's closed failure vocabulary into the typed public endpoint algebra.
module internal CoreConversions =
    let private action error =
        match error with
        | DomainError.InvalidInput _ -> RecommendedAction.CorrectInput
        | DomainError.VersionConflict _ -> RecommendedAction.ReadCurrent
        | _ -> RecommendedAction.NoneRequired

    let private progressRejection error =
        match error with
        | DomainError.AmendmentRequiresUndecided ->
            RejectionCode.AmendmentRequiresUndecided,
            "Withdraw an unpaid decision before amending registration."
        | DomainError.CorrectionNoChanges ->
            RejectionCode.InvalidInput,
            "Choose at least one factual correction that changes the current case."
        | DomainError.CorrectionRequiresExistingValue ->
            RejectionCode.InvalidInput,
            "A correction can only replace or clear a value already recorded on this case."
        | DomainError.DecisionRequired ->
            RejectionCode.DecisionRequired, "Record a payment decision first."
        | DomainError.PaymentAlreadyRecorded ->
            RejectionCode.PaymentAlreadyRecorded, "ClaimCore records one full payment."
        | DomainError.PaymentNotRecorded ->
            RejectionCode.PaymentNotRecorded, "There is no payment record to clear."
        | DomainError.DecisionAlreadyPaid ->
            RejectionCode.DecisionAlreadyPaid, "A recorded payment prevents changing its decision."
        | DomainError.ZeroDecisionCannotBePaid ->
            RejectionCode.ZeroDecisionCannotBePaid, "A zero decision is not a payment."
        | _ -> invalidArg (nameof error) "Expected a progress rejection."

    let private statusRejection error =
        match error with
        | DomainError.ClosedCase ->
            RejectionCode.CaseClosed, "Reopen the case before changing its facts."
        | DomainError.AlreadyClosed -> RejectionCode.AlreadyClosed, "The case is already closed."
        | DomainError.AlreadyOpened -> RejectionCode.AlreadyOpened, "The case is already open."
        | _ -> invalidArg (nameof error) "Expected a status rejection."

    let private stateRejection error =
        match error with
        | DomainError.ClosedCase
        | DomainError.AlreadyClosed
        | DomainError.AlreadyOpened -> statusRejection error
        | _ -> progressRejection error

    let rejection error =
        let code, message, field, version =
            match error with
            | DomainError.InvalidInput(field, message) ->
                RejectionCode.InvalidInput, message, Some field, None
            | DomainError.NotFound ->
                RejectionCode.CaseNotFound, "The case reference was not found.", None, None
            | DomainError.AlreadyExists ->
                RejectionCode.CaseAlreadyExists, "The case reference already exists.", None, None
            | DomainError.VersionConflict actual ->
                RejectionCode.VersionConflict,
                "Read the current case before making a changed request.",
                None,
                Some actual
            | state ->
                let code, message = stateRejection state
                code, message, None, None

        {
            Code = code
            Message = message
            Field = field
            ActualVersion = version
            Action = action error
        }

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
