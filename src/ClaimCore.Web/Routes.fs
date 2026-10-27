namespace ClaimCore.Web

open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Protocol

open System.Threading
open System.Threading.Tasks
open ClaimCore.Json

[<NoEquality; NoComparison>]
type PreparedRoute =
    {
        OperationId: System.Guid option
        Invoke: ServiceContext option -> CancellationToken -> (unit -> unit) -> Task<JsonValue>
    }

[<NoEquality; NoComparison>]
type Route =
    {
        Description: ClaimCore.Protocol.Route
        Prepare: JsonValue -> Result<PreparedRoute, Problem>
    }

module Route =
    /// Input, handler and output meet here while their types are still known to the compiler.
    let bind
        (endpoint: Endpoint<'Input, 'Output>)
        (operationId: 'Input -> System.Guid option)
        (unavailable: 'Input -> 'Output)
        (invoke: ServiceContext -> 'Input -> CancellationToken -> Task<'Output>)
        : Route =
        {
            Description = ClaimCore.Protocol.Route.describe endpoint
            Prepare =
                fun value ->
                    endpoint.Input.Read "$" value
                    |> Result.map (fun input ->
                        {
                            OperationId = operationId input
                            Invoke =
                                fun context cancellation completed ->
                                    task {
                                        match context with
                                        | None -> return endpoint.Output.Write(unavailable input)
                                        | Some context ->
                                            let! reply = invoke context input cancellation
                                            completed ()
                                            return endpoint.Output.Write reply
                                    }
                        })
        }

    let read endpoint operationId invoke =
        bind endpoint operationId (fun _ -> Outcome.Failed CoreFault.StoreUnavailable) invoke
