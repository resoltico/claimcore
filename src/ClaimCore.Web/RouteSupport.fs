namespace ClaimCore.Web

open ClaimCore.Contract

open ClaimCore.Contract.Protocol

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

    let admissionFailure context =
        function
        | AdmissionFailure.UntrustedConnection ->
            hostFailure context HostFailureReason.ConnectionRejected
        | AdmissionFailure.OriginRejected
        | AdmissionFailure.FetchMetadataRejected ->
            hostFailure context HostFailureReason.OriginRejected
        | AdmissionFailure.UnsupportedMediaType ->
            hostFailure context HostFailureReason.MediaTypeRejected
        | AdmissionFailure.BodyTooLarge -> hostFailure context HostFailureReason.BodyTooLarge
        | AdmissionFailure.SessionRejected -> hostFailure context HostFailureReason.SessionRejected
        | AdmissionFailure.AntiforgeryRejected ->
            hostFailure context HostFailureReason.AntiforgeryRejected

    let inputFailure context =
        function
        | BodyProblem.BodyTooLarge -> hostFailure context HostFailureReason.BodyTooLarge
        | _ -> hostFailure context HostFailureReason.InputInvalid

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
