namespace ClaimCore.Application

/// One caller-safe projection for durable operation authority closure. It is separate from the
/// Recovery transport vocabulary because ordinary Prepare and Execute need the same factual result.
module internal OperationRejection =
    let revoked: Rejection = Rejection.OperationRevoked

    let attemptLimit: Rejection = Rejection.RecoveryAttemptLimitReached
