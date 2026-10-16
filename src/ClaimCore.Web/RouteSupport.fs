namespace ClaimCore.Web

open ClaimCore.ClientProtocol

open System.IO
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open ClaimCore.Hosting

module internal RouteSupport =
    let private dispatchKey = obj ()
    let markDispatched (context: HttpContext) = context.Items[dispatchKey] <- box false
    let markCompleted (context: HttpContext) = context.Items[dispatchKey] <- box true

    let hostFailure context reason =
        HttpHeaders.noStore context
        RequestLog.outcome "HOST_FAILURE" context
        WebWire.hostFailure reason

    let admissionFailure context =
        function
        | AdmissionFailure.UntrustedConnection -> hostFailure context HostFailure.ConnectionRejected
        | AdmissionFailure.OriginRejected
        | AdmissionFailure.FetchMetadataRejected -> hostFailure context HostFailure.OriginRejected
        | AdmissionFailure.UnsupportedMediaType -> hostFailure context HostFailure.MediaTypeRejected
        | AdmissionFailure.BodyTooLarge -> hostFailure context HostFailure.BodyTooLarge
        | AdmissionFailure.SessionRejected -> hostFailure context HostFailure.SessionRejected
        | AdmissionFailure.AntiforgeryRejected ->
            hostFailure context HostFailure.AntiforgeryRejected

    let inputFailure context =
        function
        | HttpInputProblem.BodyTooLarge -> hostFailure context HostFailure.BodyTooLarge
        | _ -> hostFailure context HostFailure.InputInvalid

    let outcome endpoint bytes context =
        RequestLog.outcome "SUCCEEDED" context
        WebWire.outcome endpoint bytes

    let reply (value: ServiceReply) context =
        RequestLog.outcome value.Outcome context
        value.OperationId |> Option.iter (fun id -> RequestLog.operation id context)
        value.ActorId |> Option.iter (fun id -> RequestLog.actor id context)
        WebWire.result 200 value.Bytes

    let handleFailures (sink: TextWriter) (context: HttpContext) (next: RequestDelegate) : Task =
        task {
            try
                do! next.Invoke context

                if context.Response.StatusCode = 405 && not context.Response.HasStarted then
                    do! (hostFailure context HostFailure.MethodRejected).ExecuteAsync context
            with error ->
                RequestLog.reportException sink error

                if context.Response.HasStarted then
                    context.Abort()
                else
                    let reason =
                        match context.Items.TryGetValue dispatchKey with
                        | true, (:? bool as completed) when completed ->
                            HostFailure.CompletedResponseFailed
                        | true, _ -> HostFailure.DispatchUnconfirmed
                        | _ -> HostFailure.BeforeDispatchFailed

                    try
                        context.Response.Clear()
                        do! (hostFailure context reason).ExecuteAsync context
                    with _ ->
                        context.Abort()
        }
