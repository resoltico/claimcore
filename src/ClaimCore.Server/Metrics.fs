namespace ClaimCore.Server

open System
open System.Collections.Generic
open System.Diagnostics
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Application

[<RequireQualifiedAccess>]
type internal RequestMetricResult =
    | Succeeded
    | Refused
    | Failed
    | HostFailure

/// Hosting observations use only closed endpoint, result and session-reason values.
module internal WebMetrics =
    let private meter = Service.metrics
    let private requests = meter.CreateCounter<int64>("claimcore.requests")

    let private duration =
        meter.CreateHistogram<double>("claimcore.request.duration", "s")

    let private starts = meter.CreateCounter<int64>("claimcore.sessions.started")
    let private ends = meter.CreateCounter<int64>("claimcore.sessions.ended")

    let private safe record =
        try
            record ()
        with _ ->
            ()

    let result =
        function
        | ReplyKind.Rejected -> RequestMetricResult.Refused
        | ReplyKind.Failed
        | ReplyKind.OutcomeUnknown -> RequestMetricResult.Failed
        | ReplyKind.Succeeded
        | ReplyKind.Reviewed
        | ReplyKind.Accepted
        | ReplyKind.AlreadyAccepted
        | ReplyKind.Pending
        | ReplyKind.NotAccepted -> RequestMetricResult.Succeeded

    let private resultToken =
        function
        | RequestMetricResult.Succeeded -> "succeeded"
        | RequestMetricResult.Refused -> "refused"
        | RequestMetricResult.Failed -> "failed"
        | RequestMetricResult.HostFailure -> "host_failure"

    let private recordRequest endpoint result (elapsed: TimeSpan) =
        safe (fun () ->
            let mutable tags = TagList()
            tags.Add("endpoint", endpoint)
            tags.Add("result", resultToken result)
            requests.Add(1L, &tags)
            duration.Record(elapsed.TotalSeconds, KeyValuePair<string, obj>("endpoint", endpoint)))

    let request endpoint result elapsed =
        recordRequest
            (endpoint |> Option.map EndpointIds.token |> Option.defaultValue "unknown")
            result
            elapsed

    let csvExport result elapsed =
        recordRequest ExportTransport.token result elapsed

    let sessionStarted () = safe (fun () -> starts.Add 1L)

    let private ended reason count =
        let reason =
            match reason with
            | SessionEndReason.Logout -> "logout"
            | SessionEndReason.Expired -> "expired"
            | SessionEndReason.Invalid -> "invalid"

        if count > 0L then
            safe (fun () -> ends.Add(count, KeyValuePair<string, obj>("reason", reason)))

    let sessionEnded reason = ended reason 1L
    let sessionsExpired count = ended SessionEndReason.Expired count

    let sessionsTerminated count =
        if count > 0L then
            safe (fun () -> ends.Add(count, KeyValuePair<string, obj>("reason", "terminated")))

    let sessionChanges (changes: SessionChanges<'value>) =
        changes.Ended |> List.iter sessionEnded
        changes.Value
