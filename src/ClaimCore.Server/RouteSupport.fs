namespace ClaimCore.Server

open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json


open System.IO
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open ClaimCore.Server.Composition

module internal RouteSupport =
    let private dispatchKey = obj ()
    let markDispatched (context: HttpContext) = context.Items[dispatchKey] <- box false
    let markCompleted (context: HttpContext) = context.Items[dispatchKey] <- box true

    let hostFailure context reason =
        HttpHeaders.noStore context

        if reason = HostFailureReason.RateLimited then
            context.Response.Headers.RetryAfter <- "1"

        RequestLog.hostFailure context
        WebWire.hostFailure reason

    let gateFailure context =
        function
        | GateFailure.UntrustedConnection ->
            hostFailure context HostFailureReason.ConnectionRejected
        | GateFailure.OriginRejected
        | GateFailure.FetchMetadataRejected -> hostFailure context HostFailureReason.OriginRejected
        | GateFailure.UnsupportedMediaType ->
            hostFailure context HostFailureReason.MediaTypeRejected
        | GateFailure.BodyTooLarge -> hostFailure context HostFailureReason.BodyTooLarge
        | GateFailure.SessionRejected -> hostFailure context HostFailureReason.SessionRejected
        | GateFailure.AdmissionRejected -> hostFailure context HostFailureReason.RateLimited
        | GateFailure.AntiforgeryRejected ->
            hostFailure context HostFailureReason.AntiforgeryRejected

    let inputFailure context =
        function
        | BodyProblem.BodyTooLarge -> hostFailure context HostFailureReason.BodyTooLarge
        | _ -> hostFailure context HostFailureReason.InputInvalid

    let reply (reply: ClassifiedReply) context =
        RequestLog.reply reply.Class context
        WebWire.reply reply.Wire

    let actorReply (result: RouteReply) context =
        result.RecordedBy
        |> Option.iter (fun actor -> RequestLog.actor (ActorId.value actor) context)

        reply result.Response context

    let handleFailures (sink: TextWriter) (context: HttpContext) (next: RequestDelegate) : Task =
        task {
            try
                do! next.Invoke context

                if context.Response.StatusCode = 405 && not context.Response.HasStarted then
                    do! (hostFailure context HostFailureReason.MethodRejected).ExecuteAsync context
            with error ->
                RequestLog.reportException sink error

                if context.Response.HasStarted then
                    context.Abort()
                else
                    let reason =
                        match context.Items.TryGetValue dispatchKey with
                        | true, (:? bool as completed) when completed ->
                            HostFailureReason.CompletedResponseFailed
                        | true, _ -> HostFailureReason.DispatchUnconfirmed
                        | _ when (error :? AdmissionRejectedException) ->
                            HostFailureReason.RateLimited
                        | _ -> HostFailureReason.BeforeDispatchFailed

                    try
                        context.Response.Clear()
                        do! (hostFailure context reason).ExecuteAsync context
                    with _ ->
                        context.Abort()
        }
