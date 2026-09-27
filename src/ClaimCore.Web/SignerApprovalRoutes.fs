namespace ClaimCore.Web

open System.Threading
open Microsoft.AspNetCore.Http
open ClaimCore.Application

module SignerApprovalRoutes =
    let approve admit maximumBytes (core: IActorClaimsCore) (context: HttpContext) =
        task {
            match! RouteSupport.admittedBody admit maximumBytes context with
            | Error result -> return result
            | Ok bytes ->
                match HttpSignerApprovalInput.approve bytes with
                | Error reason -> return RouteSupport.inputFailure context reason
                | Ok request ->
                    RouteSupport.markDispatched context
                    let! outcome = core.ApproveCopySigner(request, CancellationToken.None)
                    RouteSupport.markCompleted context
                    return WebWire.signerApproval outcome
        }
