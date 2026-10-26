namespace ClaimCore.Application

open System.Threading
open System.Threading.Tasks
open ClaimCore.Contract
open ClaimCore.Json

[<NoEquality; NoComparison>]
type Route =
    {
        Description: ClaimCore.Contract.Route
        Prepare:
            JsonValue
                -> Result<
                    (HandlerContext option -> CancellationToken -> (unit -> unit) -> Task<JsonValue>),
                    Problem
                    >
    }

module Route =
    /// Input, handler and output meet here while their types are still known to the compiler.
    let bind
        (endpoint: Endpoint<'Input, 'Output>)
        (unavailable: 'Input -> 'Output)
        (invoke: HandlerContext -> 'Input -> CancellationToken -> Task<'Output>)
        : Route =
        {
            Description = ClaimCore.Contract.Route.describe endpoint
            Prepare =
                fun value ->
                    endpoint.Input.Read "$" value
                    |> Result.map (fun input ->
                        fun context cancellation completed ->
                            task {
                                match context with
                                | None -> return endpoint.Output.Write(unavailable input)
                                | Some context ->
                                    let! reply = invoke context input cancellation
                                    completed ()
                                    return endpoint.Output.Write reply
                            })
        }

    let read endpoint invoke =
        bind endpoint (fun _ -> ReadReply.Failed CoreFault.StoreUnavailable) invoke
