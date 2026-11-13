namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts

open System

/// Request-scoped dependencies; people is loaded afresh by each operation.
[<NoEquality; NoComparison>]
type ServiceContext =
    internal
        {
            InstallationId: Guid
            Store: IStore
            Calendar: IBusinessCalendar
            ProductVersion: string
            Claims: RequesterClaims
            Principal: PrincipalKey
        }

/// One snapshot instant and the installation calendar own all read/review date metadata.
module internal ServiceContext =
    let basis (services: ServiceContext) observed =
        {
            WorkDateBasis.ObservedAt = observed
            BusinessDate = services.Calendar.DateAt observed
            TimeZoneId = services.Calendar.TimeZoneId
        }

    let runtime services observed =
        {
            RuntimeContext.ProductVersion = services.ProductVersion
            Basis = basis services observed
        }
