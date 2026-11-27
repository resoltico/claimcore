namespace ClaimCore.Application


open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Installation opening failures shared by persistence and the composition root.
[<RequireQualifiedAccess>]
type RuntimeOpenFault =
    | InvalidTransport
    | UnsupportedServer
    | UnsupportedDatabase
    | UnsafeRole
    | SchemaMismatch
    | InstallationInvalid
    | DataKeyInvalid
    | HeadMismatch
    | Unavailable
    | Cancelled

[<RequireQualifiedAccess; NoComparison>]
type internal Verdict<'proceed> =
    | Proceed of 'proceed
    | Replay of 'proceed
    | Refuse of Refusal
    | Fail of CoreFault

type IBusinessCalendar =
    abstract TimeZoneId: string
    abstract DateAt: instant: DateTimeOffset -> DateOnly


[<NoEquality; NoComparison>]
type internal LedgerWriteContext =
    {
        Head: LedgerPosition
        NewPayloadNonce: unit -> byte array
    }
