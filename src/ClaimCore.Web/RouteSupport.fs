namespace ClaimCore.Web

open ClaimCore.Domain
open ClaimCore.Protocol
open ClaimCore.Json


open System.IO
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open ClaimCore.Web.Composition

module internal RouteSupport =
    let private dispatchKey = obj ()
    let markDispatched (context: HttpContext) = context.Items[dispatchKey] <- box false
    let markCompleted (context: HttpContext) = context.Items[dispatchKey] <- box true

    let hostFailure context reason =
        HttpHeaders.noStore context
        RequestLog.outcome "HOST_FAILURE" context
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
        | GateFailure.AntiforgeryRejected ->
            hostFailure context HostFailureReason.AntiforgeryRejected

    let inputFailure context =
        function
        | BodyProblem.BodyTooLarge -> hostFailure context HostFailureReason.BodyTooLarge
        | _ -> hostFailure context HostFailureReason.InputInvalid

    let private memberText name value =
        JsonValue.tryMember name value
        |> Option.bind (function
            | JsonValue.String text -> Some text
            | _ -> None)

    let reply value context =
        let outcome = JsonValue.tryMember "outcome" value

        let tag =
            outcome
            |> Option.bind (memberText "kind")
            |> Option.defaultWith (fun () -> invalidOp "Missing typed outcome.")

        RequestLog.outcome tag context

        if tag = "ACCEPTED" then
            outcome
            |> Option.bind (JsonValue.tryMember "data")
            |> Option.bind (memberText "recordedBy")
            |> Option.iter (fun text -> RequestLog.actor (System.Guid.Parse text) context)

        WebWire.reply value

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
                        | _ -> HostFailureReason.BeforeDispatchFailed

                    try
                        context.Response.Clear()
                        do! (hostFailure context reason).ExecuteAsync context
                    with _ ->
                        context.Abort()
        }
