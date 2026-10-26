namespace ClaimCore.Web.Composition

open System.Threading
open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.Contract
open ClaimCore.Json

[<NoEquality; NoComparison>]
type internal PreparedCall =
    {
        Invoke: HandlerContext -> CancellationToken -> (unit -> unit) -> Task<JsonValue>
        Unavailable: unit -> JsonValue
    }

/// The Application exports the typed binding; Web only admits and writes its codecs.
module internal Dispatch =
    let prepare (handler: AnyHandler) value =
        handler.Accept
            { new IHandlerVisitor<Result<PreparedCall, Problem>> with
                member _.Visit handler =
                    handler.Endpoint.Input.Read "$" value
                    |> Result.map (fun input ->
                        {
                            Unavailable =
                                fun () -> handler.Endpoint.Output.Write(handler.Unavailable input)
                            Invoke =
                                fun context cancellation completed ->
                                    task {
                                        let! reply = handler.Invoke context input cancellation
                                        completed ()
                                        return handler.Endpoint.Output.Write reply
                                    }
                        })
            }
