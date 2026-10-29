namespace ClaimCore.Cli

open ClaimCore.Protocol
open ClaimCore.Json

/// An admitted wire reply retains its endpoint-owned classification and original member order.
[<NoEquality; NoComparison>]
type ServiceReply =
    {
        Route: Route
        Wire: JsonValue
        Class: ReplyClass
    }

    member this.Endpoint = this.Route.Id

module ServiceReply =
    let json (reply: ServiceReply) = reply.Wire
    let exitCode (reply: ServiceReply) = reply.Class.ExitCode

    let read (route: Route) wire =
        route.ClassifyOutput wire
        |> Result.map (fun classification ->
            {
                Route = route
                Wire = wire
                Class = classification
            })
