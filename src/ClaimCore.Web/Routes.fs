namespace ClaimCore.Web

open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation

open System.Threading
open System.Threading.Tasks
open ClaimCore.Json

[<NoEquality; NoComparison>]
type PreparedRoute =
    {
        OperationId: System.Guid option
        Invoke:
            ServiceContext option -> CancellationToken -> (unit -> unit) -> Task<ClassifiedReply>
    }

[<NoEquality; NoComparison>]
type Route =
    {
        Description: ClaimCore.Wire.Route
        Prepare: JsonValue -> Result<PreparedRoute, Problem>
    }

module Route =
    /// Input, handler and output meet here while their types are still known to the compiler.
    let bind
        (endpoint: Endpoint<'Input, 'Output>)
        (invoke: ServiceContext -> 'Input -> CancellationToken -> Task<'Output>)
        : Route =
        {
            Description = ClaimCore.Wire.Route.describe endpoint
            Prepare =
                fun value ->
                    endpoint.Input.Read "$" value
                    |> Result.map (fun input ->
                        {
                            OperationId =
                                endpoint.Correlation |> Option.map (fun echo -> echo.Request input)
                            Invoke =
                                fun context cancellation completed ->
                                    task {
                                        match context with
                                        | None ->
                                            return
                                                Endpoint.reply
                                                    endpoint
                                                    (endpoint.Unavailable input)
                                        | Some context ->
                                            let! reply = invoke context input cancellation
                                            completed ()
                                            return Endpoint.reply endpoint reply
                                    }
                        })
        }

    let read endpoint describe invoke =
        bind endpoint (fun context input token ->
            task {
                let! outcome = invoke context input token
                return Presented.outcome describe outcome
            })
