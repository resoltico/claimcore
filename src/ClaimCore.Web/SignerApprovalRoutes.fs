namespace ClaimCore.Web

open System.Threading
open Microsoft.AspNetCore.Http
open ClaimCore.Application

module SignerApprovalRoutes =
    let approve admit maximumBytes (core: IActorClaimsCore) (context: HttpContext) =
        Routes.json
            admit
            maximumBytes
            HttpSignerApprovalInput.approve
            (fun request -> core.ApproveCopySigner(request, CancellationToken.None))
            WebWire.signerApproval
            context
