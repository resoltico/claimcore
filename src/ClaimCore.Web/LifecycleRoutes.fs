namespace ClaimCore.Web

open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open ClaimCore.Application

/// Transport-only binding of exact lifecycle drafts to the actor-scoped Application workflow.
module LifecycleRoutes =
    let invoke admit maximumBytes decode execute render context =
        task {
            match! RouteSupport.admittedBody admit maximumBytes context with
            | Error result -> return result
            | Ok bytes ->
                match decode bytes with
                | Error reason -> return RouteSupport.inputFailure context reason
                | Ok input ->
                    RouteSupport.markDispatched context
                    let! outcome = execute input
                    RouteSupport.markCompleted context
                    return render outcome
        }

    let review admit maximumBytes (core: IActorClaimsCore) (context: HttpContext) =
        invoke
            admit
            maximumBytes
            HttpLifecycleInput.review
            (fun reference -> core.Lifecycle.Review(reference, context.RequestAborted))
            WebWire.lifecycleReview
            context

    let apply admit maximumBytes (core: IActorClaimsCore) context =
        invoke
            admit
            maximumBytes
            HttpLifecycleInput.apply
            (fun change -> core.Lifecycle.Apply(change, CancellationToken.None))
            (WebWire.lifecycleWrite "lifecycle.apply")
            context

    let approve admit maximumBytes (core: IActorClaimsCore) context =
        invoke
            admit
            maximumBytes
            HttpLifecycleInput.approve
            (fun input ->
                core.Lifecycle.Approve(
                    input.Change,
                    input.ApprovalId,
                    input.ExpiresAt,
                    CancellationToken.None
                ))
            (WebWire.lifecycleWrite "lifecycle.approve")
            context
