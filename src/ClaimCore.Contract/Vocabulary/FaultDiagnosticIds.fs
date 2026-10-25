namespace ClaimCore.Contract

/// Stable fault identities, independent from Application fault handling.
module FaultDiagnosticIds =
    [<Literal>]
    let InternalError = "CORE_INTERNAL_ERROR"

    [<Literal>]
    let StoreUnavailable = "CORE_STORE_UNAVAILABLE"

    [<Literal>]
    let StoreIntegrityError = "CORE_STORE_INTEGRITY_ERROR"

    [<Literal>]
    let SchemaMismatch = "CORE_SCHEMA_MISMATCH"

    [<Literal>]
    let OperationContentConflict = "CORE_OPERATION_CONTENT_CONFLICT"
