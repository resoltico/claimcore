module ClaimCore.WebTests.TestServerTombstoneRouteTests

open System
open System.Net.Http
open Expecto
open ClaimCore.Contracts
open ClaimCore.WebTests.TestServerFixture

let private eventId = "40000000-0000-4000-8000-000000000001"
let private caseId = "50000000-0000-4000-8000-000000000001"
let private approvalId = "60000000-0000-4000-8000-000000000001"
let private digest = String.replicate 64 "a"

let private proposal =
    $"""{{"eventId":"{eventId}","caseId":"{caseId}","purgeEventId":"{approvalId}","purgeWitnessSequence":"11","purgeWitnessEpoch":"1","purgeWitnessHash":"{digest}","cutoffSequence":"12","cutoffHash":"{digest}","targetCount":"8","targetDigest":"{digest}","expectedAuthorityRevision":"2","expectedAuthorityHash":"{digest}","validUntil":"2026-10-01T00:00:00.0000000+00:00"}}"""

let private terminalCopy =
    $"""{{
      "eventId":"{eventId}",
      "caseId":"{caseId}",
      "expectedAuthorityRevision":"2",
      "expectedAuthorityHash":"{digest}",
      "installationId":"70000000-0000-4000-8000-000000000001",
      "lineageId":"80000000-0000-4000-8000-000000000001",
      "witnessEpoch":"1",
      "pruneEventId":"{approvalId}",
      "witnessCutoffSequence":"12",
      "witnessCutoffHash":"{digest}",
      "copyInventoryDigest":"{digest}",
      "relevantCopyCount":"0",
      "expectedWriterGeneration":"1",
      "policyId":"synthetic-policy-1",
      "suppressionUntil":"2026-10-01T00:00:00.0000000+00:00",
      "validUntil":"2026-09-30T00:00:00.0000000+00:00"
    }}"""

let input identifier =
    match identifier with
    | "tombstone.review" -> $"""{{"caseId":"{caseId}"}}"""
    | "tombstone.approvePrune" ->
        $"""{{"proposal":{proposal},"approvalId":"{approvalId}","expiresAt":"2026-10-01T00:00:00.0000000+00:00"}}"""
    | "tombstone.approveTerminal" ->
        $"""{{"proposal":{{"kind":"CONFIRM_MANAGED_PAYLOAD_ABSENCE","copy":{terminalCopy}}},"approvalId":"{approvalId}","expiresAt":"2026-10-01T00:00:00.0000000+00:00"}}"""
    | "tombstone.changeHold" ->
        $"""{{"eventId":"{eventId}","caseId":"{caseId}","expectedAuthorityRevision":"2","expectedAuthorityHash":"{digest}","mutation":{{"kind":"RECORD","holdId":"{approvalId}","groundCode":"LEGAL_RETENTION","reviewOn":"2026-10-01"}}}}"""
    | _ -> failtest "Unknown tombstone endpoint."

let private route (endpoint: WebEndpoint) =
    use anonymous = Host.Start()

    let denied =
        anonymous.Send(
            HttpMethod.Post,
            endpoint.Path,
            Some(input endpoint.Identifier),
            Some "application/json",
            None
        )

    Expect.equal denied.Status 401 "Unadmitted caller receives a typed denial"
    Expect.stringContains denied.CacheControl "no-store" "Denial is uncached"
    use denial = document denied

    Expect.equal
        (denial.RootElement.GetProperty("code").GetString())
        "WEB_SESSION_REJECTED"
        "Safe code"

    Expect.equal anonymous.Runtime.TombstoneCalls 0 "No tombstone call precedes admission"
    use host = Host.Start()
    Expect.equal (host.Login().Status) 200 "Synthetic session begins"

    let response =
        host.Send(
            HttpMethod.Post,
            endpoint.Path,
            Some(input endpoint.Identifier),
            Some "application/json",
            Some(host.SessionToken())
        )

    Expect.equal response.Status 200 "Generated route admits the exact body"
    use body = document response

    Expect.equal
        (body.RootElement.GetProperty("endpoint").GetString())
        endpoint.Identifier
        "Exact endpoint"

    let outcome = body.RootElement.GetProperty("outcome")

    Expect.equal
        (outcome.GetProperty("tag").GetString())
        "RESOURCE_UNAVAILABLE"
        "Typed scoped refusal"

    Expect.equal
        (outcome.GetProperty("data").ValueKind)
        System.Text.Json.JsonValueKind.Null
        "No case or approval detail"

    Expect.equal host.Runtime.TombstoneCalls 1 "Exactly this route reached the tombstone facade"

let tests =
    ContractProjection.current().WebEndpoints
    |> List.filter (fun endpoint ->
        endpoint.Identifier.StartsWith("tombstone.", StringComparison.Ordinal))
    |> List.map (fun endpoint ->
        testCase
            $"[CC-WEB-001] endpoint {endpoint.Identifier} dispatches authenticated tombstone route"
            (fun () -> route endpoint))
    |> testList "Web HTTP-v3 TestServer"
