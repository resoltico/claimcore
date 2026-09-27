namespace ClaimCore.Web

open System.Threading
open Microsoft.AspNetCore.Http
open ClaimCore.Application

module CopyAdoptionApprovalRoutes =
    let approve admit maximumBytes (core: IActorClaimsCore) (context: HttpContext) =
        Routes.json
            admit
            maximumBytes
            HttpCopyAdoptionApprovalInput.approve
            (fun request -> core.ApproveCopyAdoption(request, CancellationToken.None))
            WebWire.copyAdoptionApproval
            context
