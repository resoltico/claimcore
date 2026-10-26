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
type internal Verdict<'proceed> =
    | Proceed of 'proceed
    | Replay of OperationReceipt
    | Refuse of Refusal
    | Fail of CoreFault

module internal Verdict =
    let bind next =
        function
        | Verdict.Proceed value -> next value
        | Verdict.Replay receipt -> Verdict.Replay receipt
        | Verdict.Refuse refusal -> Verdict.Refuse refusal
        | Verdict.Fail fault -> Verdict.Fail fault

    let map mapping value = bind (mapping >> Verdict.Proceed) value
