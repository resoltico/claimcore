namespace ClaimCore.Web

open ClaimCore.Wire

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
        | BodyProblem.BodyTooLarge -> hostFailure context HostFailure.BodyTooLarge
        | _ -> hostFailure context HostFailure.InputInvalid

    let private tag =
        function
        | OutcomeTag.Succeeded -> "SUCCEEDED"
        | OutcomeTag.Reviewed -> "REVIEWED"
        | OutcomeTag.Accepted -> "ACCEPTED"
        | OutcomeTag.AlreadyAccepted -> "ALREADY_ACCEPTED"
        | OutcomeTag.Rejected -> "REJECTED"
        | OutcomeTag.Failed -> "FAILED"
        | OutcomeTag.Cancelled -> "CANCELLED"
        | OutcomeTag.OutcomeUnknown -> "OUTCOME_UNKNOWN"

    let outcome value context =
        match WireReply.attempt value with
        | AttemptResult.Outcome(outcome, _) -> RequestLog.outcome (tag outcome) context
        | _ -> invalidArg "value" "Expected a typed service outcome."

        WebWire.reply value

    let reply (value: WireReply) context =
        match value with
        | WireReply.CaseExecute {
                                    Outcome = CaseExecuteOutcome.Accepted receipt
                                } -> RequestLog.actor receipt.RecordedBy context
        | WireReply.AuthorityExecute {
                                         Outcome = AuthorityExecuteOutcome.Accepted receipt
                                     } -> RequestLog.actor receipt.RecordedBy context
        | _ -> ()

        outcome value context

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
