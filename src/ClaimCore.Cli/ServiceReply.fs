namespace ClaimCore.Cli

open ClaimCore.Protocol
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

module ServiceReply =
    let json (reply: ServiceReply) = reply.Wire
    let exitCode (reply: ServiceReply) = reply.Class.ExitCode

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
