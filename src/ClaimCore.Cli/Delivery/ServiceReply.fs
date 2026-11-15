namespace ClaimCore.Cli

open ClaimCore.CommandLine

open ClaimCore.Wire
open ClaimCore.Application.Contracts
open ClaimCore.Json

/// An admitted wire reply retains its endpoint-owned classification and original member order.
[<NoEquality; NoComparison>]
type ServiceReply =
    {
        Route: Route
        Wire: JsonValue
        Typed: IEndpointOutput
    }

    member this.Class = this.Typed.Class
    member this.Endpoint = this.Route.Id

module ReplyExitCode =
    let ofKind =
        function
        | ReplyKind.Succeeded
        | ReplyKind.Reviewed
        | ReplyKind.Accepted
        | ReplyKind.AlreadyAccepted -> ExitCode.Success
        | ReplyKind.NotAccepted
        | ReplyKind.Rejected -> ExitCode.Rejected
        | ReplyKind.Failed -> ExitCode.Failed
        | ReplyKind.Pending
        | ReplyKind.OutcomeUnknown -> ExitCode.OutcomeUnknown

module ServiceReply =
    let json (reply: ServiceReply) = reply.Wire
    let exitCode (reply: ServiceReply) = ReplyExitCode.ofKind reply.Class

    let value endpoint (reply: ServiceReply) =
        EndpointValues.tryOutput endpoint reply.Typed

    let read (route: Route) wire =
        route.ReadOutput wire
        |> Result.map (fun typed ->
            {
                Route = route
                Wire = wire
                Typed = typed
            })
