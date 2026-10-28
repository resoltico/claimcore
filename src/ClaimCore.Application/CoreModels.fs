namespace ClaimCore.Application


open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.RecordFormat

/// Installation opening failures shared by persistence and the composition root.
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

[<RequireQualifiedAccess; NoComparison>]
type Verdict<'proceed> =
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

    let outcome proceeded replayed =
        function
        | Verdict.Proceed value -> Outcome.Succeeded(proceeded value)
        | Verdict.Replay receipt -> Outcome.Succeeded(replayed receipt)
        | Verdict.Refuse refusal -> Outcome.Rejected refusal
        | Verdict.Fail fault -> Outcome.Failed fault

type IBusinessCalendar =
    abstract TimeZoneId: string
    abstract DateAt: instant: DateTimeOffset -> DateOnly
