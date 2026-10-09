module ClaimCore.WebTests.TestServerCoreOutcomeTests

open System
open Expecto
open ClaimCore.Application
open ClaimCore.WebTests.RouteFixtures
open ClaimCore.WebTests.TestServerFixture
open ClaimCore.WebTests.TestServerOutcomeValues

let private commandDraft =
    """{"operationId":"40000000-0000-4000-8000-000000000001","caseReference":"WEB-V2-001","expectedRevision":"0","command":{"kind":"OPEN","values":{"incidentDate":"2026-09-01","incidentNotificationDate":"2026-09-02","incidentCountry":"Latvia","claimantName":"Synthetic claimant","insurerName":"Synthetic insurer","claimedAmount":"12.34","claimedCurrency":"EUR"}}}"""

let private getOutcomes (host: Host) token =
    let invoke value tag lookup =
        host.Runtime.GetOutcome <- Some value

        let data =
            postJson host token "case.get" """{"caseReference":"WEB-V2-001"}"""
            |> tagged "case.get" tag

        lookup
        |> Option.iter (fun expected ->
            Expect.equal (data.GetProperty("tag").GetString()) expected "Exact case lookup branch")

    invoke (QueryOutcome.Succeeded(Lookup.Found current)) "SUCCEEDED" (Some "FOUND")
    invoke (QueryOutcome.Succeeded(Lookup.NotFound caseReference)) "SUCCEEDED" (Some "NOT_FOUND")
    invoke (QueryOutcome.Rejected rejection) "REJECTED" None
    invoke (QueryOutcome.Failed fault) "FAILED" None
    invoke QueryOutcome.Cancelled "CANCELLED" None

let private noExistenceResponseParity () =
    use host = Host.Start()
    Expect.equal (host.Login().Status) 200 "Synthetic session"
    let token = host.SessionToken()
    host.Runtime.GetOutcome <- Some(QueryOutcome.Rejected Rejection.ResourceUnavailable)
    host.Runtime.ObserveOutcome <- Some(QueryOutcome.Rejected Rejection.ResourceUnavailable)

    let sendCase reference =
        host.Send(
            System.Net.Http.HttpMethod.Post,
            "/api/v3/cases/get",
            Some($"""{{"caseReference":"{reference}"}}"""),
            Some "application/json",
            Some token
        )

    let sendOperation operation =
        host.Send(
            System.Net.Http.HttpMethod.Post,
            "/api/v3/operations/observe",
            Some($"""{{"operationId":"{operation}"}}"""),
            Some "application/json",
            Some token
        )

    for first, second in
        [
            sendCase caseReference, sendCase "NO-SUCH-CASE"
            sendOperation (operationId.ToString("D")),
            sendOperation "50000000-0000-4000-8000-000000000001"
        ] do
        Expect.equal first.Status second.Status "Identical public HTTP status"
        Expect.equal first.Body second.Body "Identical non-disclosing body and guidance"
        Expect.stringContains first.CacheControl "no-store" "Refusal is not cached"

    Expect.equal host.Runtime.CoreCalls 4 "Both identity classes reached one typed core call"

let private listOutcomes (host: Host) token =
    let success: QueryOutcome<CaseSummaryPage> =
        QueryOutcome.Succeeded
            {
                Items =
                    [
                        {
                            CaseReference = caseReference
                            Revision = 1L
                            Status = current.Record.Fields.Status
                        }
                    ]
                NextCursor = None
                AvailableCommands = []
            }

    for value, expected in
        [
            success, "SUCCEEDED"
            QueryOutcome.Rejected rejection, "REJECTED"
            QueryOutcome.Failed fault, "FAILED"
            QueryOutcome.Cancelled, "CANCELLED"
        ] do
        host.Runtime.ListOutcome <- Some value

        let data =
            postJson host token "case.list" """{"limit":10}"""
            |> tagged "case.list" expected

        if expected = "SUCCEEDED" then
            Expect.equal (data.GetProperty("items").GetArrayLength()) 1 "Summary-only list payload"

let private historyOutcomes (host: Host) token =
    let request = """{"caseReference":"WEB-V2-001","limit":10,"detail":"FULL"}"""

    let found =
        QueryOutcome.Succeeded(
            Lookup.Found
                {
                    Entries = [ FullEntry receipt ]
                    NextCursor = None
                }
        )

    for value, expected, lookup in
        [
            found, "SUCCEEDED", Some "FOUND"
            QueryOutcome.Succeeded(Lookup.NotFound caseReference), "SUCCEEDED", Some "NOT_FOUND"
            QueryOutcome.Rejected rejection, "REJECTED", None
            QueryOutcome.Failed fault, "FAILED", None
            QueryOutcome.Cancelled, "CANCELLED", None
        ] do
        host.Runtime.HistoryOutcome <- Some value

        let data =
            postJson host token "case.history" request |> tagged "case.history" expected

        lookup
        |> Option.iter (fun branch ->
            Expect.equal (data.GetProperty("tag").GetString()) branch "Exact history lookup branch")

let private observationOutcomes (host: Host) token =
    let request = $"""{{"operationId":"{operationId:D}"}}"""

    for value, expected, lookup in
        [
            QueryOutcome.Succeeded(Lookup.Found receipt), "SUCCEEDED", Some "FOUND"
            QueryOutcome.Succeeded(Lookup.NotFound operationId), "SUCCEEDED", Some "NOT_FOUND"
            QueryOutcome.Rejected rejection, "REJECTED", None
            QueryOutcome.Failed fault, "FAILED", None
            QueryOutcome.Cancelled, "CANCELLED", None
        ] do
        host.Runtime.ObserveOutcome <- Some value

        let data =
            postJson host token "operation.observe" request
            |> tagged "operation.observe" expected

        lookup
        |> Option.iter (fun branch ->
            Expect.equal
                (data.GetProperty("tag").GetString())
                branch
                "Exact operation lookup branch")

let private queryRouteOutcomes () =
    use host = Host.Start()
    let token = authenticated host
    getOutcomes host token
    listOutcomes host token
    historyOutcomes host token
    observationOutcomes host token
    Expect.equal host.Runtime.CoreCalls 19 "Every query branch traversed the typed core route"

let private prepareOutcomes (host: Host) token =
    let request = commandDraft

    for value, expected in
        [
            PrepareOutcome.Prepared(preparationDetails, review), "PREPARED"
            PrepareOutcome.ObservedAccepted receipt, "OBSERVED_ACCEPTED"
            PrepareOutcome.RetainedForRecovery(preparationDetails, rejection),
            "RETAINED_FOR_RECOVERY"
            PrepareOutcome.PrepareRejected(operationId, rejection), "REJECTED"
            PrepareOutcome.PrepareFailed(operationId, fault), "FAILED"
            PrepareOutcome.CancelledBeforeAdmission operationId, "CANCELLED_BEFORE_ADMISSION"
            PrepareOutcome.PreparationStateUnknown(operationId, digest, fault),
            "PREPARATION_STATE_UNKNOWN"
        ] do
        host.Runtime.PrepareOutcome <- Some value

        let data =
            postJson host token "command.prepare" request
            |> tagged "command.prepare" expected

        if expected = "PREPARED" then
            Expect.equal
                (data
                    .GetProperty("details")
                    .GetProperty("summary")
                    .GetProperty("requestSha256")
                    .GetString())
                digest
                "Prepared route retains exact canonical digest"
        elif expected = "OBSERVED_ACCEPTED" then
            Expect.equal
                (data.GetProperty("receipt").GetProperty("operationId").GetString())
                (operationId.ToString("D"))
                "Accepted replay identifies the original operation"

            Expect.isFalse
                (data.TryGetProperty("details") |> fst)
                "Accepted replay has no fabricated preparation details"
        elif expected = "RETAINED_FOR_RECOVERY" then
            Expect.equal
                (data.GetProperty("rejection").GetProperty("code").GetString())
                "VERSION_CONFLICT"
                "Non-reviewable retained work stays a typed core refusal"
        elif expected = "PREPARATION_STATE_UNKNOWN" then
            Expect.equal
                (data.GetProperty("requestSha256").GetString())
                digest
                "Unknown preparation retains digest"

let private submitOutcomes (host: Host) token =
    let request = commandDraft

    for value, expected in
        [
            SubmissionOutcome.ObservedAccepted receipt, "OBSERVED_ACCEPTED"
            SubmissionOutcome.Completed(
                preparationSummary,
                attemptId,
                DefiniteExecution.Accepted receipt,
                SettlementConfirmation.Unconfirmed
            ),
            "COMPLETED"
            SubmissionOutcome.RejectedBeforeAttempt(Some preparationSummary, rejection),
            "REFUSED_BEFORE_ATTEMPT"
            SubmissionOutcome.FailedBeforeAttempt(Some preparationSummary, fault),
            "FAILED_BEFORE_ATTEMPT"
            SubmissionOutcome.CancelledBeforeAdmission operationId, "CANCELLED_BEFORE_ADMISSION"
            SubmissionOutcome.CancelledBeforeAttempt preparationSummary, "CANCELLED_BEFORE_ATTEMPT"
            SubmissionOutcome.AttemptAdmissionUnknown(preparationSummary, fault),
            "ATTEMPT_ADMISSION_UNKNOWN"
            SubmissionOutcome.AttemptUnresolved(preparationSummary, attemptId, fault),
            "ATTEMPT_UNRESOLVED"
            SubmissionOutcome.PreparationStateUnknown(operationId, digest, fault),
            "PREPARATION_STATE_UNKNOWN"
        ] do
        host.Runtime.ExecuteOutcome <- Some value

        let data =
            postJson host token "command.execute" request
            |> tagged "command.execute" expected

        if expected = "COMPLETED" then
            Expect.equal
                (data.GetProperty("settlement").GetString())
                "UNCONFIRMED"
                "Definite accepted execution survives unconfirmed settlement"

let private mutationRouteOutcomes () =
    use host = Host.Start()
    let token = authenticated host
    prepareOutcomes host token
    submitOutcomes host token
    Expect.equal host.Runtime.CoreCalls 16 "Prepare and submit use the actor-bound core"
    Expect.equal host.Runtime.RecoveryCalls 0 "Ordinary submit does not borrow recovery authority"

let tests =
    testList
        "Web HTTP-v3 TestServer"
        [
            testCase
                "[CC-WEB-001] case and operation routes preserve found, absent, rejected, failed, and cancelled core outcomes"
                queryRouteOutcomes
            testCase
                "[CC-WEB-001] inaccessible and absent case or operation have identical public responses"
                noExistenceResponseParity
            testCase
                "[CC-WEB-001] preparation and submission routes preserve definite, unknown, and exact-digest outcomes"
                mutationRouteOutcomes
        ]
