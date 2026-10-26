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
