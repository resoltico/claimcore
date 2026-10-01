namespace ClaimCore.Web

open ClaimCore.Contracts

open System.Threading
open Microsoft.AspNetCore.Http
open ClaimCore.Application

module CopyDeletionApprovalRoutes =
    let approve admit maximumBytes (core: IActorClaimsCore) (context: HttpContext) =
        Routes.json
            admit
            maximumBytes
            HttpCopyDeletionApprovalInput.approve
            (fun request -> core.ApproveCopyDeletion(request, CancellationToken.None))
            WebWire.copyDeletionApproval
            context
