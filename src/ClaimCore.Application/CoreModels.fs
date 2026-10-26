namespace ClaimCore.Application

open ClaimCore.Contract

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.RecordFormat

/// Admission vocabulary shared by persistence and the composition root.
[<RequireQualifiedAccess>]
type RuntimeOpenFault =
    | InvalidTransport
    | UnsupportedServer
    | UnsafeRole
    | SchemaMismatch
    | InstallationInvalid
    | HeadMismatch
    | Unavailable
    | Cancelled

[<RequireQualifiedAccess>]
type AdmittedAuthorityAct =
    | Register
    | Grant of role: Role * scope: GrantTarget
    | Revoke of role: Role * scope: GrantTarget
    | Enable
    | Disable

type AdmittedAuthorityRequest =
    {
        OperationId: Guid
        Target: PrincipalKey
        Act: AdmittedAuthorityAct
    }

[<RequireQualifiedAccess; NoComparison>]
type internal Decision<'proceed> =
    | Proceed of 'proceed
    | Replay of OperationReceipt
    | Refuse of Refusal
    | Fail of CoreFault

module internal Decision =
    let bind next =
        function
        | Decision.Proceed value -> next value
        | Decision.Replay receipt -> Decision.Replay receipt
        | Decision.Refuse refusal -> Decision.Refuse refusal
        | Decision.Fail fault -> Decision.Fail fault

    let map mapping value =
        bind (mapping >> Decision.Proceed) value
