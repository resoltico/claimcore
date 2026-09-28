namespace ClaimCore.Web

open System.Threading
open Microsoft.AspNetCore.Http
open ClaimCore.Application

module WriterHandoffApprovalRoutes =
    let approve admit maximumBytes (core: IActorClaimsCore) (context: HttpContext) =
        Routes.json
            admit
            maximumBytes
            HttpWriterHandoffApprovalInput.approve
            (fun request -> core.ApproveWriterHandoff(request, CancellationToken.None))
            WebWire.writerHandoffApproval
            context
