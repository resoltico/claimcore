namespace ClaimCore.Application

open System

[<RequireQualifiedAccess>]
type ResourceScope =
    | Installation
    | Case of Guid

[<RequireQualifiedAccess>]
type CaseVisibility =
    | Every
    | Assigned
