namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

/// Owner diagnostics are independent of actor disclosure/refusal contracts and contain no raw input.
[<RequireQualifiedAccess>]
type OperatorFault =
    | InvocationInvalid
    | ConnectionFileRequired
    | ConnectionFileRefused
    | ConnectionFileInvalid
    | DataKeyFileRequired
    | DataKeyFileInvalid
    | DataKeyInvalid
    | ConnectionInvalid
    | ConnectionRejected
    | UnsafeTransport
    | PermissionDenied
    | TimeZoneInvalid
    | InitialOwnerInvalid
    | OwnerRecoveryRefused
    | OwnerRecoveryNameRequired
    | OwnerRecoveryIdentityReserved
    | AlreadyInitialized
    | CaseReferencePolicyInvalid
    | CaseReferencePolicyMismatch
    | UnsupportedServer
    | UnsupportedDatabase
    | SchemaMismatch
    | FormatMismatch
    | InstallationInvalid
    | HeadMismatch
    | MaintenanceRequired
    | MaintenanceConflict
    | StaleRecoveryPreparation
    | AttachmentFileRefused
    | AttachmentRepairRefused
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
