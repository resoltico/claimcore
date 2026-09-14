namespace ClaimCore.Application

/// One caller-safe projection for durable operation authority closure. It is separate from the
/// Recovery transport vocabulary because ordinary Prepare and Execute need the same factual result.
module internal OperationRejection =
    let revoked: Rejection =
        {
            Code = RejectionCode.OperationRevoked
            Message = "This exact operation was durably revoked before execution."
            Field = None
            ActualVersion = None
            Action = RecommendedAction.ReadCurrent
        }

    let attemptLimit: Rejection =
        {
            Code = RejectionCode.RecoveryAttemptLimitReached
            Message =
                "This operation has reached its recovery attempt limit. Read the current case and author a new operation only after review."
            Field = None
            ActualVersion = None
            Action = RecommendedAction.ReadCurrent
        }
