namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

/// Owner diagnostics are independent of actor disclosure/refusal contracts and contain no raw input.
[<RequireQualifiedAccess>]
type OperatorFault =
    | InvocationInvalid
    | ConnectionFileRequired
    | ConnectionFileRefused
    | ConnectionFileInvalid
    | ConnectionInvalid
    | ConnectionRejected
    | UnsafeTransport
    | PermissionDenied
    | TimeZoneInvalid
    | InitialOwnerInvalid
    | AlreadyInitialized
    | UnsupportedServer
    | SchemaMismatch
    | FormatMismatch
    | InstallationInvalid
    | HeadMismatch
    | MaintenanceRequired
    | CheckpointFileRefused
    | CheckpointInvalid
    | Unavailable
    | IntegrityFailure
    | Cancelled
    | InternalFailure

module OperatorFault =
    let vocabulary =
        Vocabulary.ofUnion<OperatorFault> "operatorFault" TokenCasing.ScreamingSnake None

    let all = Vocabulary.entries vocabulary
