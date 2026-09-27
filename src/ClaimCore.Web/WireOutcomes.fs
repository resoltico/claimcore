namespace ClaimCore.Web

open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open ClaimCore.Application
open ClaimCore.Contracts

type private EncodedJsonResult(bytes: byte array, status: int) =
    interface IResult with
        member _.ExecuteAsync(context: HttpContext) =
            task {
                context.Response.StatusCode <- status
                context.Response.ContentType <- "application/json; charset=utf-8"
                context.Response.ContentLength <- int64 bytes.Length
                do! context.Response.Body.WriteAsync(bytes, context.RequestAborted)
            }
            :> Task

module internal EncodedJson =
    let result status bytes =
        EncodedJsonResult(bytes, status) :> IResult

    let ok bytes = result StatusCodes.Status200OK bytes

module WebWire =
    let liveness = WebWireCodec.liveness |> EncodedJson.ok

    let hostFailure reason =
        WebWireCodec.hostFailure reason
        |> EncodedJson.result (WebHostFailures.status reason)

    let session endpoint authenticated (antiforgeryToken: string | null) =
        WebWireCodec.session endpoint authenticated (Option.ofObj antiforgeryToken)
        |> EncodedJson.ok

    let description value =
        WebWireCodec.description value |> EncodedJson.ok

    let get value =
        WebWireCodec.get value |> EncodedJson.ok

    let list value =
        WebWireCodec.list value |> EncodedJson.ok

    let history value =
        WebWireCodec.history value |> EncodedJson.ok

    let observe value =
        WebWireCodec.observe value |> EncodedJson.ok

    let management endpoint value =
        WebWireCodec.management endpoint value |> EncodedJson.ok

    let signerApproval value =
        WebWireCodec.signerApproval value |> EncodedJson.ok

    let copyDeletionApproval value =
        WebWireCodec.copyDeletionApproval value |> EncodedJson.ok

    let copyAdoptionApproval value =
        WebWireCodec.copyAdoptionApproval value |> EncodedJson.ok

    let writerHandoffApproval value =
        WebWireCodec.writerHandoffApproval value |> EncodedJson.ok

    let realDataActivationReview value =
        WebWireCodec.realDataActivationReview value |> EncodedJson.ok

    let realDataActivationApproval value =
        WebWireCodec.realDataActivationApproval value |> EncodedJson.ok

    let lifecycleReview value =
        WebWireCodec.lifecycleReview value |> EncodedJson.ok

    let lifecycleWrite endpoint value =
        WebWireCodec.lifecycleWrite endpoint value |> EncodedJson.ok

    let tombstoneReview value =
        WebWireCodec.tombstoneReview value |> EncodedJson.ok

    let tombstoneWrite endpoint value =
        WebWireCodec.tombstoneWrite endpoint value |> EncodedJson.ok

    let prepare _endpoint value =
        WebWireCodec.prepare value |> EncodedJson.ok

    let submit value =
        WebWireCodec.submit value |> EncodedJson.ok
