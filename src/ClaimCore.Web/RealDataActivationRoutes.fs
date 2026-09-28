namespace ClaimCore.Web

open System.Threading
open Microsoft.AspNetCore.Http
open ClaimCore.Application

module RealDataActivationRoutes =
    let review admit maximumBytes (core: IActorClaimsCore) (context: HttpContext) =
        Routes.json
            admit
            maximumBytes
            HttpRealDataActivationInput.review
            (fun planId -> core.ReviewRealDataActivation(planId, CancellationToken.None))
            WebWire.realDataActivationReview
            context

    let approve admit maximumBytes (core: IActorClaimsCore) (context: HttpContext) =
        Routes.json
            admit
            maximumBytes
            HttpRealDataActivationInput.approve
            (fun request -> core.ApproveRealDataActivation(request, CancellationToken.None))
            WebWire.realDataActivationApproval
            context
