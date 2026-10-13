namespace ClaimCore.Web

open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open ClaimCore.Contracts

[<Sealed>]
type private EncodedJsonResult(bytes: byte array, status: int) =
    interface IResult with
        member _.ExecuteAsync context =
            task {
                context.Response.StatusCode <- status
                context.Response.ContentType <- "application/json; charset=utf-8"
                context.Response.ContentLength <- int64 bytes.Length
                do! context.Response.Body.WriteAsync(bytes, context.RequestAborted)
            }
            :> Task

module internal WebWire =
    let result status bytes =
        EncodedJsonResult(bytes, status) :> IResult

    let outcome endpoint value =
        OutcomeCodec.encode endpoint value |> result 200

    let hostFailure reason =
        HostFailures.encode reason |> result (HostFailures.status reason)
