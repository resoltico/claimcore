namespace ClaimCore.Application

open System

[<RequireQualifiedAccess>]
type Capability =
    | ReadCase
    | ReadHistory
    | ReadHistoryFull
    | Register
    | Decide
    | RecordPayment
    | Close
    | Steward
    | ManageAuthority
    | ReadAuthority

[<RequireQualifiedAccess>]
type ResourceScope =
    | Installation
    | Case of Guid

[<RequireQualifiedAccess>]
type CaseVisibility =
    | Every
    | Only of caseIds: Guid list
