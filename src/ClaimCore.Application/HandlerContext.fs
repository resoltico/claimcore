namespace ClaimCore.Application

open System

/// Request-scoped dependencies; authority is loaded afresh by each operation.
[<NoEquality; NoComparison>]
type HandlerContext =
    internal
        {
            InstallationId: Guid
            Store: ILedgerStore
            Calendar: IBusinessCalendar
            Principal: PrincipalKey
        }
