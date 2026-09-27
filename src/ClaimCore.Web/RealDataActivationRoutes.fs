namespace ClaimCore.Web

open System.Threading
open Microsoft.AspNetCore.Http
open ClaimCore.Application

module RealDataActivationRoutes =
    let review admit maximumBytes (core: IActorClaimsCore) (context: HttpContext) =
        task {
            match! RouteSupport.admittedBody admit maximumBytes context with
            | Error result -> return result
            | Ok bytes ->
                match HttpRealDataActivationInput.review bytes with
                | Error reason -> return RouteSupport.inputFailure context reason
                | Ok planId ->
                    RouteSupport.markDispatched context
                    let! outcome = core.ReviewRealDataActivation(planId, CancellationToken.None)
                    RouteSupport.markCompleted context
                    return WebWire.realDataActivationReview outcome
        }

    let approve admit maximumBytes (core: IActorClaimsCore) (context: HttpContext) =
        task {
            match! RouteSupport.admittedBody admit maximumBytes context with
            | Error result -> return result
            | Ok bytes ->
                match HttpRealDataActivationInput.approve bytes with
                | Error reason -> return RouteSupport.inputFailure context reason
                | Ok request ->
                    RouteSupport.markDispatched context
                    let! outcome = core.ApproveRealDataActivation(request, CancellationToken.None)
                    RouteSupport.markCompleted context
                    return WebWire.realDataActivationApproval outcome
        }
