module ClaimCore.WebTests.TestServerBoundaryTests

open System
open System.Net.Http
open System.Text.Json
open Expecto
open ClaimCore.Application
open ClaimCore.Web
open ClaimCore.WebTests.RouteFixtures
open ClaimCore.WebTests.TestServerFixture
open ClaimCore.WebTests.TestServerOutcomeValues

let private sessionExpiry () =
    let now = DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero)
    let registry = SessionRegistry(TimeSpan.FromMinutes(10.), TimeSpan.FromMinutes(25.))
    let idle = registry.Create(now)
    Expect.isTrue (registry.IsCurrent(idle, now.AddMinutes(9.))) "Activity extends idle expiry"

    Expect.isFalse
        (registry.IsCurrent(idle, now.AddMinutes(20.)))
        "Idle expiry revokes a quiet session"

    let absolute = registry.Create(now)

    for minute in [ 9.; 18.; 24. ] do
        Expect.isTrue
            (registry.IsCurrent(absolute, now.AddMinutes(minute)))
            "Activity may extend idle only within the absolute lifetime"

    Expect.isFalse
        (registry.IsCurrent(absolute, now.AddMinutes(25.)))
        "Absolute expiry cannot be extended by activity"

    let untouched = registry.Create(now)
    Expect.equal registry.StoredCount 1 "An untouched session is retained until expiration"
    let replacement = registry.Create(now.AddMinutes(26.))
    Expect.equal registry.StoredCount 1 "A new login sweeps expired session entries"

    Expect.isFalse
        (registry.IsCurrent(untouched, now.AddMinutes(26.)))
        "Expired authority stays revoked"

    Expect.isTrue
        (registry.IsCurrent(replacement, now.AddMinutes(26.)))
        "New authority remains live"

    use host = Host.Start()
    authenticated host |> ignore
    host.Sessions.RevokeAll()
    let denied = host.Send(HttpMethod.Get, "/api/v2/definition", None, None, None)
    Expect.equal denied.Status 401 "Definition route trusts only current registry membership"

let private commandDraft kind values =
    $"""{{"operationId":"{operationId:D}","caseReference":"WEB-V2-001","expectedRevision":"0","command":{{"kind":"{kind}","values":{values}}}}}"""

let private commandVariants () =
    use host = Host.Start()
    let token = authenticated host

    let registration =
        """{"incidentDate":"2026-09-01","incidentNotificationDate":"2026-09-02","incidentCountry":"Latvia","claimantName":"Synthetic claimant","insurerName":"Synthetic insurer","claimedAmount":"12.34","claimedCurrency":"EUR"}"""

    let decision =
        """{"paymentDecisionDate":"2026-09-03","payableAmount":"12.34","payableCurrency":"EUR"}"""

    for kind, values in
        [
            "OPEN", registration
            "AMEND_REGISTRATION", registration
            "DECIDE", decision
            "WITHDRAW_DECISION", "{}"
            "RECORD_PAYMENT", """{"paymentDate":"2026-09-04"}"""
            "CLEAR_PAYMENT", "{}"
            "CLOSE", "{}"
            "REOPEN", "{}"
        ] do
        let body = commandDraft kind values

        postJson host token "command.prepare" body
        |> tagged "command.prepare" "CANCELLED_BEFORE_ADMISSION"
        |> ignore

    Expect.equal host.Runtime.CoreCalls 8 "Every Domain command kind passes one Web draft decoder"

let private invalidUtf8 () =
    use host = Host.Start()
    let token = authenticated host

    let response =
        host.SendBytes(
            HttpMethod.Post,
            WebContract.jsonPath "case.get",
            [| 0xFFuy |],
            "application/json",
            Some token
        )

    Expect.equal response.Status 400 "Malformed UTF-8 is a typed not-started host refusal"
    use body = document response

    Expect.equal
        (body.RootElement.GetProperty("code").GetString())
        "WEB_INVALID_REQUEST"
        "No decoding alias"

    Expect.equal
        (body.RootElement.GetProperty("executionPhase").GetString())
        "NOT_STARTED"
        "Invalid bytes prove no case query started"

    Expect.equal host.Runtime.CoreCalls 0 "Invalid bytes never enter the typed core"

let private pageWithCursor cursor =
    RecoveryQueryOutcome.RecoverySucceeded
        {
            View = RecoveryListView.Pending
            Items = []
            NextCursor = Some cursor
            PendingPreparationCount = 0
            PendingCanonicalRequestBytes = 0L
            MaximumPendingPreparations = 1024
            MaximumPendingCanonicalRequestBytes = 64L * 1024L * 1024L
            NearCapacity = false
        }

let private expectCursor cursor (response: JsonElement) =
    Expect.equal
        (response.GetProperty("nextCursor").GetString())
        cursor
        "Opaque cursor is returned intact"

let private rejectedCursorOutcome =
    RecoveryQueryOutcome.RecoveryRejected RecoveryRejection.OperationIdRequired

let private recoveryCursor () =
    use host = Host.Start()
    let token = authenticated host
    let cursor = "opaque.after.1"

    host.Runtime.RecoveryListOutcome <- Some(pageWithCursor cursor)

    let first =
        postJson host token "recovery.list" """{"limit":10}"""
        |> tagged "recovery.list" "SUCCEEDED"

    expectCursor cursor first

    let second =
        postJson host token "recovery.list" $"""{{"cursor":"{cursor}","limit":10}}"""
        |> tagged "recovery.list" "SUCCEEDED"

    expectCursor cursor second

    Expect.equal
        host.Runtime.LastRecoveryCursor
        (Some(Some cursor))
        "Exact cursor reaches core Recovery.List"

    host.Runtime.RecoveryListOutcome <- Some rejectedCursorOutcome

    let rejected =
        postJson host token "recovery.list" """{"cursor":"malformed","limit":10}"""
        |> tagged "recovery.list" "REJECTED"

    Expect.equal
        (rejected.GetProperty("code").GetString())
        "INVALID_RECOVERY_INPUT"
        "Core cursor refusal remains typed"

let tests =
    testList
        "Web HTTP-v2 TestServer"
        [
            testCase
                "[CC-WEB-001] session registry enforces idle and absolute expiry with deterministic time"
                sessionExpiry
            testCase
                "[CC-WEB-001] exact command draft transport accepts all eight semantic command variants"
                commandVariants
            testCase
                "[CC-WEB-001] invalid UTF-8 is refused before query decoding and core dispatch"
                invalidUtf8
            testCase
                "[CC-WEB-001] recovery list cursor round-trips opaquely and malformed cursors are typed refusals"
                recoveryCursor
        ]
