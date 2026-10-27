namespace ClaimCore.Application

open ClaimCore.Domain

open System

/// Request-scoped dependencies; authority is loaded afresh by each operation.
[<NoEquality; NoComparison>]
type ServiceContext =
    internal
        {
            InstallationId: Guid
            Store: IStore
            Calendar: IBusinessCalendar
            ProductVersion: string
            Principal: PrincipalKey
        }
