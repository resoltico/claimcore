namespace ClaimCore.Application

open System

[<RequireQualifiedAccess>]
type Capability =
    | ReadDefinition
    | ListCases
    | ReadCase
    | ReadHistorySummary
    | ReadHistoryFull
    | ObserveOperation
    | EditCase
    | StewardCase
    | ManageAuthority

[<RequireQualifiedAccess>]
type ResourceScope =
    | Definition
    | Installation
    | Case of Guid

[<RequireQualifiedAccess>]
type CaseVisibility =
    | Every
    | Only of caseIds: Guid list
