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
[<RequireQualifiedAccess; NoComparison>]
type RouteBody =
    | Json of ClassifiedReply
    | Csv of Released<ExportBody> * filename: string * installation: System.Guid
    | Attachment of AttachmentTransfer * Released<AttachmentContent> * installation: System.Guid

/// Audit identity is taken from the confirmed use-case result, independently of representation.
[<NoComparison>]
type RouteReply =
    {
        Response: RouteBody
        RecordedBy: ActorId option
    }

    member this.Wire =
        match this.Response with
        | RouteBody.Json value -> value.Wire
        | RouteBody.Csv _
        | RouteBody.Attachment _ -> invalidOp "Binary transfers have no JSON representation."

    member this.Class =
        match this.Response with
        | RouteBody.Json value -> value.Class
        | RouteBody.Csv _
        | RouteBody.Attachment _ -> ReplyKind.Succeeded

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
    private
        {
            EndpointDescription: ClaimCore.Wire.Route
            RequiresDisclosure: bool
            PrepareRequest: JsonValue -> Result<PreparedRoute, Problem>
        }

    member this.Description = this.EndpointDescription
    member this.Protected = this.RequiresDisclosure
    member this.Prepare value = this.PrepareRequest value

module Route =
    let internal prepare (endpoint: Endpoint<'input, 'output>) invoke : Route =
        {
            EndpointDescription = ClaimCore.Wire.Route.describe endpoint
            RequiresDisclosure = false
            PrepareRequest =
                fun value ->
                    endpoint.Input.Validate "$" value
                    |> Result.bind (fun () -> endpoint.Input.Read "$" value)
                    |> Result.map (fun input ->
                        {
                            OperationId =
                                endpoint.Correlation |> Option.map (fun echo -> echo.Request input)
                            Invoke = invoke input
                        })
        }

    /// Input, handler and output meet here while their types are still known to the compiler.
    let bind
        (endpoint: Endpoint<'Input, 'Output>)
        (invoke: ServiceContext -> 'Input -> CancellationToken -> Task<'Output>)
        =
        prepare endpoint (fun input _representation context cancellation completed ->
            task {
                match context with
                | None ->
                    return
                        {
                            Response =
                                RouteBody.Json(
                                    Endpoint.reply endpoint (endpoint.Unavailable input)
                                )
                            RecordedBy = None
                        }
                | Some context ->
                    let! reply = invoke context input cancellation
                    completed ()

                    return
                        {
                            Response = RouteBody.Json(Endpoint.reply endpoint reply)
                            RecordedBy = None
                        }
            })

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
        (invoke: ServiceContext -> 'input -> CancellationToken -> Task<'data>)
        =
        prepare endpoint (fun input representation context cancellation completed ->
            task {
                let! outcome =
                    match context with
                    | Some services -> invoke services input cancellation
                    | None -> Task.FromResult(unavailable input)

                // Completion belongs to the use case, before presentation or encoding can fail.
                completed ()

                let reply =
                    match representation with
                    | Representation.Human -> RepresentationReply.Human(describe input outcome)
                    | Representation.Data -> RepresentationReply.Data outcome

                return
                    {
                        Response = RouteBody.Json(Endpoint.reply endpoint reply)
                        RecordedBy = recordedBy outcome
                    }
            })

    let representedReadWithInput endpoint describe invoke =
        represented
            endpoint
            (fun _ -> Outcome.Failed CoreFault.StoreUnavailable)
            (fun input outcome -> HumanReplies.read (describe input) outcome)
            (fun _ -> None)
            invoke

    let representedRead endpoint describe invoke =
        representedReadWithInput endpoint (fun _ -> describe) invoke


    let protectedReply
        endpoint
        (unavailable: 'input -> Released<'data>)
        describe
        recordedBy
        (invoke: ServiceContext -> 'input -> CancellationToken -> Task<Released<'data>>)
        =
        let route =
            represented
                endpoint
                (fun input -> (unavailable input).Value)
                describe
                recordedBy
                (fun context input token ->
                    task {
                        let! reply = invoke context input token
                        return reply.Value
                    })

        { route with RequiresDisclosure = true }

    let protectedReadWithInput endpoint describe invoke =
        protectedReply
            endpoint
            (fun _ -> Service.Disclosure.unavailableRead ())
            (fun input outcome -> HumanReplies.read (describe input) outcome)
            (fun _ -> None)
            invoke

    let protectedRead endpoint describe invoke =
        protectedReadWithInput endpoint (fun _ -> describe) invoke
