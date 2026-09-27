namespace ClaimCore.Web

open System.Threading
open Microsoft.AspNetCore.Http
open ClaimCore.Application

module WriterHandoffApprovalRoutes =
    let approve admit maximumBytes (core: IActorClaimsCore) (context: HttpContext) =
        task {
            match! RouteSupport.admittedBody admit maximumBytes context with
            | Error result -> return result
            | Ok bytes ->
                match HttpWriterHandoffApprovalInput.approve bytes with
                | Error reason -> return RouteSupport.inputFailure context reason
                | Ok request ->
                    RouteSupport.markDispatched context
                    let! outcome = core.ApproveWriterHandoff(request, CancellationToken.None)
                    RouteSupport.markCompleted context
                    return WebWire.writerHandoffApproval outcome
        }
