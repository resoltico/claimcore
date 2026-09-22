namespace ClaimCore.Contracts

open ClaimCore.Application

/// Adapter failure, never an IClaimsCore result or evidence of non-commit.
[<RequireQualifiedAccess>]
type CliLocalFault =
    | RuntimeOpen of RuntimeOpenFault
    | CaseInputMismatch
    | RecoveryInputMismatch
    | ExportWriteFailed
    | ExportIdentityConflict

module CliLocalFaults =
    let private runtime =
        [
            CliLocalFault.RuntimeOpen RuntimeOpenFault.RuntimeConfigurationInvalid,
            ("CLI_RUNTIME_CONFIGURATION_INVALID",
             FaultCode.SchemaMismatch,
             "The local runtime configuration is invalid.")
            CliLocalFault.RuntimeOpen RuntimeOpenFault.RuntimeSchemaMismatch,
            ("CLI_RUNTIME_SCHEMA_MISMATCH",
             FaultCode.SchemaMismatch,
             "The PostgreSQL schema is not compatible with this runtime.")
            CliLocalFault.RuntimeOpen RuntimeOpenFault.RuntimeStoreUnavailable,
            ("CLI_RUNTIME_UNAVAILABLE",
             FaultCode.StoreUnavailable,
             "The PostgreSQL runtime is unavailable.")
            CliLocalFault.RuntimeOpen RuntimeOpenFault.RuntimeStoreIntegrityError,
            ("CLI_RUNTIME_INTEGRITY_ERROR",
             FaultCode.StoreIntegrityError,
             "The PostgreSQL runtime failed integrity checks.")
            CliLocalFault.RuntimeOpen RuntimeOpenFault.RuntimeCancelled,
            ("CLI_RUNTIME_OPEN_CANCELLED",
             FaultCode.StoreUnavailable,
             "Runtime opening was cancelled before work was admitted.")
        ]

    let private adapter =
        [
            CliLocalFault.CaseInputMismatch,
            ("CLI_CASE_INPUT_MISMATCH",
             FaultCode.StoreIntegrityError,
             "The endpoint input does not match its generated contract.")
            CliLocalFault.RecoveryInputMismatch,
            ("CLI_RECOVERY_INPUT_MISMATCH",
             FaultCode.RecoveryIntegrityError,
             "The endpoint input does not match its generated contract.")
            CliLocalFault.ExportWriteFailed,
            ("CLI_EXPORT_WRITE_FAILED",
             FaultCode.StoreUnavailable,
             "The recovery export could not be written to a new private file.")
        ]

    let private entries =
        runtime
        @ adapter
        @ [
            CliLocalFault.ExportIdentityConflict,
            ("CLI_EXPORT_IDENTITY_CONFLICT",
             FaultCode.RecoveryIntegrityError,
             "The export metadata does not match the requested operation. No file was written.")
        ]

    let all = entries |> List.map fst

    let private description reason =
        entries |> List.find (fst >> (=) reason) |> snd

    let token reason =
        let identifier, _, _ = description reason in identifier

    let code reason =
        let _, code, _ = description reason in code

    let render reason =
        let _, _, message = description reason in message
