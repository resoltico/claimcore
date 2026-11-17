namespace ClaimCore.Server

open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation

open System.Threading
open System.Threading.Tasks
open ClaimCore.Json

/// The host writes one explicit media body; CSV success never enters a JSON codec.
[<RequireQualifiedAccess>]
type RouteBody =
    | Json of ClassifiedReply
    | Csv of bytes: byte array * filename: string * installation: System.Guid

/// Audit identity is taken from the confirmed use-case result, independently of representation.
type RouteReply =
    {
        Response: RouteBody
        RecordedBy: ActorId option
        ExpireBrowserSession: bool
    }

    member this.Wire =
        match this.Response with
        | RouteBody.Json value -> value.Wire
        | RouteBody.Csv _ -> invalidOp "CSV has no JSON representation."

    member this.Class =
        match this.Response with
        | RouteBody.Json value -> value.Class
        | RouteBody.Csv _ -> ReplyKind.Succeeded

[<NoEquality; NoComparison>]
type PreparedRoute =
    {
        OperationId: System.Guid option
        Invoke:
            Representation
                -> ServiceContext option
                -> CancellationToken
                -> (unit -> unit)
                -> Task<RouteReply>
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
                                                {
                                                    Response =
                                                        RouteBody.Json(
                                                            Endpoint.reply
                                                                endpoint
                                                                (endpoint.Unavailable input)
                                                        )
                                                    RecordedBy = None
                                                    ExpireBrowserSession = false
                                                }
                                        | Some context ->
                                            let! reply = invoke context input cancellation
                                            completed ()

                                            return
                                                {
                                                    Response =
                                                        RouteBody.Json(
                                                            Endpoint.reply endpoint reply
                                                        )
                                                    RecordedBy = None
                                                    ExpireBrowserSession = false
                                                }
                                    }
                        })
        }

    let read endpoint describe invoke =
        bind endpoint (fun context input token ->
            task {
                let! outcome = invoke context input token
                return Presented.outcome describe outcome
            })

    /// Project one authorized use-case result; representation never changes dispatch or input.
    let represented
        (endpoint: Endpoint<'input, RepresentationReply<'human, 'data>>)
        (unavailable: 'input -> 'data)
        (describe: 'input -> 'data -> 'human)
        (recordedBy: 'data -> ActorId option)
        (expireBrowserSession: 'data -> bool)
        (invoke: ServiceContext -> 'input -> CancellationToken -> Task<'data>)
        =
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
                                fun representation context cancellation completed ->
                                    task {
                                        let! outcome =
                                            match context with
                                            | Some services -> invoke services input cancellation
                                            | None -> Task.FromResult(unavailable input)

                                        // Completion belongs to the use case, before presentation or encoding can fail.
                                        completed ()

                                        let reply =
                                            match representation with
                                            | Representation.Human ->
                                                RepresentationReply.Human(describe input outcome)
                                            | Representation.Data ->
                                                RepresentationReply.Data outcome

                                        return
                                            {
                                                Response =
                                                    RouteBody.Json(Endpoint.reply endpoint reply)
                                                RecordedBy = recordedBy outcome
                                                ExpireBrowserSession = expireBrowserSession outcome
                                            }
                                    }
                        })
        }

    let representedRead endpoint describe invoke =
        represented
            endpoint
            (fun _ -> Outcome.Failed CoreFault.StoreUnavailable)
            (fun _ outcome -> HumanReplies.read describe outcome)
            (fun _ -> None)
            (fun _ -> false)
            invoke
