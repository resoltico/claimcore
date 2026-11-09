namespace ClaimCore.Web

open ClaimCore.Application
open ClaimCore.Application.Contracts
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
            Representation
                -> ServiceContext option
                -> CancellationToken
                -> (unit -> unit)
                -> Task<ClassifiedReply>
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
                    endpoint.Input.Validate "$" value
                    |> Result.bind (fun () -> endpoint.Input.Read "$" value)
                    |> Result.map (fun input ->
                        {
                            OperationId =
                                endpoint.Correlation |> Option.map (fun echo -> echo.Request input)
                            Invoke =
                                fun _representation context cancellation completed ->
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

    /// Choose a representation after one authorized use-case result, without changing its input.
    let representedRead
        (endpoint: Endpoint<'input, RepresentationReply<HumanReadReply, Outcome<'value>>>)
        describe
        (invoke: ServiceContext -> 'input -> CancellationToken -> Task<Outcome<'value>>)
        =
        let prepare value =
            endpoint.Input.Validate "$" value
            |> Result.bind (fun () -> endpoint.Input.Read "$" value)
            |> Result.map (fun input ->
                {
                    OperationId =
                        endpoint.Correlation |> Option.map (fun echo -> echo.Request input)
                    Invoke =
                        fun representation context cancellation completed ->
                            task {
                                let! outcome =
                                    match context with
                                    | Some services -> invoke services input cancellation
                                    | None ->
                                        Task.FromResult(Outcome.Failed CoreFault.StoreUnavailable)

                                let reply =
                                    match representation with
                                    | Representation.Human ->
                                        HumanReplies.read describe outcome
                                        |> RepresentationReply.Human
                                    | Representation.Data -> RepresentationReply.Data outcome

                                completed ()
                                return Endpoint.reply endpoint reply
                            }
                })

        {
            Description = ClaimCore.Wire.Route.describe endpoint
            Prepare = prepare
        }
