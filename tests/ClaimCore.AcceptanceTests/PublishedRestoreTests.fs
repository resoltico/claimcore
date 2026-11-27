module ClaimCore.AcceptanceTests.PublishedRestoreTests

open ClaimCore.Domain

open System
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Npgsql
open Expecto
open ClaimCore.Wire
open CliCalls

let private operator (fixture: PublishedFixture.Fixture) arguments expected =
    let result = fixture.Administration arguments
    Expect.equal result.ExitCode expected "Published operator command exit"

    Expect.equal
        result.StandardError.Length
        0
        "Operator output does not disclose provider diagnostics"

    use document = JsonDocument.Parse(ReadOnlyMemory<byte> result.StandardOutput)
    document.RootElement.Clone()

let private stop (fixture: PublishedFixture.Fixture) =
    task {
        fixture.RequestWebStop()
        let! exitCode = fixture.WaitForWebExit(TimeSpan.FromSeconds 30.)
        Expect.equal exitCode 0 "Published host drains before database isolation"
    }

let private count (fixture: PublishedFixture.Fixture) statement =
    use source =
        NpgsqlDataSource.Create(
            File.ReadAllText(Path.Combine(fixture.Directory, "owner.connection"))
        )

    use connection = source.OpenConnection()

    use command = new NpgsqlCommand(statement, connection)
    command.ExecuteScalar() |> unbox<int64>

let private sessions fixture =
    count fixture "SELECT count(*) FROM claimcore.sessions"

let private pendingFlows fixture =
    count fixture "SELECT count(*) FROM claimcore.browser_flows"

let private openFrameForDate operationId reference receivedDate =
    let registration =
        ClaimCore.TestSupport.DomainInputs.registrationWith
            [
                BusinessField.IncidentDate, "2026-09-01"
                BusinessField.ReceivedDate, receivedDate
                BusinessField.IncidentCountry, "LV"
                BusinessField.PolicyNumber, "Restore claimant"
                BusinessField.InsurerClaimReference, "Restore insurer"
                BusinessField.ClaimedAmount, "10"
                BusinessField.ClaimedCurrency, "EUR"
            ]
            ClaimCore.TestSupport.DomainInputs.registration

    frame
        "operation.execute"
        (caseSubmission
            operationId
            reference
            0L
            "case.open"
            (ClaimCore.TestSupport.Submissions.fields (RegistrationInput.values registration)))

let private openFrame operationId reference =
    openFrameForDate operationId reference "2026-09-02"

let private bearer
    (fixture: PublishedFixture.Fixture)
    (token: string)
    (installation: string)
    (endpoint: string)
    (input: string)
    =
    task {
        use request =
            new HttpRequestMessage(HttpMethod.Post, fixture.Origin + "/api/" + endpoint)

        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
        request.Headers.Add("ClaimCore-Contract", ContractIdentity.value)
        request.Headers.Add("ClaimCore-Representation", "DATA")
        request.Headers.Add("ClaimCore-Installation", installation)
        request.Content <- new StringContent(input, Encoding.UTF8, "application/json")
        use! response = fixture.Http.SendAsync request

        Expect.equal
            (int response.StatusCode)
            200
            "Retained service bearer reaches the Core boundary"

        let! bytes = response.Content.ReadAsByteArrayAsync()
        use document = JsonDocument.Parse bytes
        return document.RootElement.GetProperty("outcome").Clone()
    }

let private checkLookupCapacity
    (fixture: PublishedFixture.Fixture)
    (bearerToken: string)
    installation
    =
    task {
        use owner =
            NpgsqlDataSource.Create(
                File.ReadAllText(Path.Combine(fixture.Directory, "owner.connection"))
            )

        use runtime =
            NpgsqlDataSource.Create(
                File.ReadAllText(fixture.WebEnvironment["CLAIMCORE_CONNECTION_FILE"])
            )

        use! connection = owner.OpenConnectionAsync()
        use! transaction = connection.BeginTransactionAsync()

        use hold =
            new NpgsqlCommand(
                "SELECT count(*) FROM claimcore.lock_ledger_head()",
                connection,
                transaction
            )

        let! _ = hold.ExecuteScalarAsync()

        let get id =
            bearer
                fixture
                bearerToken
                installation
                "operation.get"
                (JsonSerializer.Serialize {| operationId = id |})

        let expired () =
            Guid.CreateVersion7(DateTimeOffset.UtcNow.AddMinutes(-11.))

        do!
            task {
                use witness =
                    NpgsqlDataSource.Create(
                        File.ReadAllText(
                            Path.Combine(fixture.Directory, "witness-owner.connection")
                        )
                    )

                use! blocking = witness.OpenConnectionAsync()
                use! lockTransaction = blocking.BeginTransactionAsync()

                use command =
                    new NpgsqlCommand(
                        "LOCK TABLE claimcore_witness.installations IN ACCESS EXCLUSIVE MODE",
                        blocking,
                        lockTransaction
                    )

                let! _ = command.ExecuteNonQueryAsync()
                let! withdrawn = (get (expired ())).WaitAsync(TimeSpan.FromSeconds 3.)

                Expect.equal
                    (withdrawn.GetProperty("kind").GetString())
                    "FAILED"
                    "An expired published lookup with a stalled witness cannot claim final absence"

                use! ready = fixture.Http.GetAsync(fixture.Origin + "/health/ready")

                Expect.equal
                    (int ready.StatusCode)
                    200
                    "The published local readiness route still works"

                do! lockTransaction.RollbackAsync()
            }

        let confirmation = get (expired ())
        use! observe = runtime.OpenConnectionAsync()

        use waiting =
            new NpgsqlCommand(
                "SELECT EXISTS(SELECT FROM pg_stat_activity WHERE pid<>pg_backend_pid() AND wait_event_type='Lock' AND query LIKE '%lock_ledger_head%')",
                observe
            )

        let deadline = Diagnostics.Stopwatch.StartNew()
        let mutable queued = false

        while not queued && deadline.Elapsed < TimeSpan.FromSeconds 3. do
            let! value = waiting.ExecuteScalarAsync()
            queued <- unbox<bool> value

            if not queued then
                do! Task.Delay 20

        Expect.isTrue queued "The published request reaches a real PostgreSQL head waiter"
        let! short = (get (Guid.CreateVersion7())).WaitAsync(TimeSpan.FromSeconds 2.)

        Expect.equal
            (short.GetProperty("kind").GetString())
            "PENDING"
            "The one read permit is available to an unrelated lookup"

        do! transaction.CommitAsync()
        let! final = confirmation

        Expect.equal
            (final.GetProperty("kind").GetString())
            "NOT_ACCEPTED"
            "The head waiter confirms absence after the lock releases"
    }

let private qualify () =
    task {
        use fixture = new PublishedFixture.Fixture()
        do! fixture.Start()
        setup fixture
        do! stop fixture
        fixture.WebEnvironment["CLAIMCORE_WEB_CORE_PERMITS"] <- "1"
        do! fixture.RestartWeb()
        PublishedSessionTests.browserPhase fixture "login" "restore-login"
        Expect.equal (sessions fixture) 1L "Real login supplies a restorable server-side ticket"
        use! challenge = fixture.Http.GetAsync(fixture.Origin + "/auth/login")

        Expect.equal
            (int challenge.StatusCode)
            302
            "Open a real browser sign-in and leave the callback pending"

        Expect.equal (pendingFlows fixture) 1L "The original database retains the pending flow"
        let! retainedBearer = fixture.ServiceToken()

        let admittedInstallation =
            (operator fixture [ "head" ] 0).GetProperty("installationId").GetString()
            |> nonNull

        do! checkLookupCapacity fixture retainedBearer admittedInstallation


        do! stop fixture
        let! missingHistoryBackup = fixture.BackupDatabase "before-accepted-operation"
        do! fixture.RestartWeb()
        let issuedAt = DateTimeOffset.UtcNow.AddMinutes(-9.5)
        let acceptedId = Guid.CreateVersion7(issuedAt).ToString("D")
        let unseenId = Guid.CreateVersion7(issuedAt).ToString("D")
        let acceptedFrame = openFrame acceptedId "RESTORE-ACCEPTED"
        run fixture fixture.Environment acceptedFrame 0 |> require "ACCEPTED" |> ignore

        let pending =
            verb fixture fixture.Environment [ "operation"; "get"; unseenId ] 4
            |> require "PENDING"

        let finalAt =
            DateTimeOffset.Parse(
                pending.GetProperty("finalAt").GetString() |> nonNull,
                Globalization.CultureInfo.InvariantCulture
            )

        do! stop fixture
        let! completeBackup = fixture.BackupDatabase "before-expiry-observation"
        do! fixture.RestartWeb()
        PublishedSessionTests.browserPhase fixture "verify" "restore-before-loss"
        let remaining = finalAt - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds 250.

        Expect.isLessThanOrEqual
            remaining
            (TimeSpan.FromSeconds 45.)
            "Pending witness uses a bounded real expiry window"

        if remaining > TimeSpan.Zero then
            do! Task.Delay remaining

        verb fixture fixture.Environment [ "operation"; "get"; unseenId ] 2
        |> require "NOT_ACCEPTED"
        |> ignore

        let trusted = operator fixture [ "head" ] 0

        Expect.notEqual
            (trusted.GetProperty("expiredBefore").ValueKind)
            JsonValueKind.Null
            "Final absence committed durable expiry"

        let checkpoint =
            FixtureFiles.text
                fixture.Directory
                "restore-trusted.checkpoint.json"
                (trusted.GetRawText())

        let installation = trusted.GetProperty("installationId").GetString() |> nonNull
        do! stop fixture

        fixture.FenceRestore(
            Guid.Parse installation,
            Guid.Parse "10000000-0000-4000-8000-000000000199"
        )

        do! fixture.RestoreDatabase missingHistoryBackup

        operator fixture [ "maintenance"; "acquire"; "0"; "10000000-0000-4000-8000-000000000099" ] 0
        |> ignore

        let missing =
            operator
                fixture
                [
                    "admit-restored-installation"
                    "--checkpoint"
                    checkpoint
                    "--pause-generation"
                    "1"
                    "--pause-id"
                    "10000000-0000-4000-8000-000000000099"
                ]
                5

        Expect.equal
            (missing.GetProperty("finding").GetString())
            "EXPECTED_POSITION"
            "Restore cannot manufacture missing accepted history"

        Expect.equal (sessions fixture) 1L "Refused admission preserves the restored ticket"

        Expect.equal
            (pendingFlows fixture)
            1L
            "Refused admission preserves the pending flow in the restored copy"

        let missingHead = operator fixture [ "head" ] 0

        Expect.isLessThan
            (missingHead.GetProperty("operationSequence").GetInt64())
            (trusted.GetProperty("operationSequence").GetInt64())
            "Missing acceptance is independently observed"

        do! fixture.RestoreDatabase completeBackup

        operator fixture [ "maintenance"; "acquire"; "0"; "10000000-0000-4000-8000-000000000099" ] 0
        |> ignore

        let lowered = operator fixture [ "verify"; "--checkpoint"; checkpoint ] 5

        Expect.equal
            (lowered.GetProperty("finding").GetString())
            "EXPIRY_FRONTIER"
            "Backup loses only the later trusted expiry observation"

        let admitted =
            operator
                fixture
                [
                    "admit-restored-installation"
                    "--checkpoint"
                    checkpoint
                    "--pause-generation"
                    "1"
                    "--pause-id"
                    "10000000-0000-4000-8000-000000000099"
                ]
                0

        Expect.equal
            (admitted.GetProperty("invalidatedSessions").GetInt64())
            1L
            "Published admission invalidates the restored ticket"

        Expect.equal (sessions fixture) 0L "Session invalidation is committed before reopening"

        Expect.equal
            (pendingFlows fixture)
            0L
            "Pre-restore flow evidence is retired before reopening"

        let head = operator fixture [ "head" ] 0

        Expect.isTrue
            (JsonElement.DeepEquals(head, trusted))
            "Admission preserves accepted history and raises the exact trusted frontier"

        operator fixture [ "verify"; "--checkpoint"; checkpoint ] 0 |> ignore

        operator fixture [ "maintenance"; "resume"; "1"; "10000000-0000-4000-8000-000000000099" ] 0
        |> ignore

        do! fixture.RestartWeb()
        PublishedSessionTests.browserPhase fixture "stale" "restore-stale-cookie"

        let! stale =
            bearer
                fixture
                retainedBearer
                installation
                "operation.get"
                (JsonSerializer.Serialize {| operationId = acceptedId |})

        Expect.equal
            (stale.GetProperty("kind").GetString())
            "REJECTED"
            "Pre-restore bearer evidence cannot disclose an accepted operation"

        // Whole-second issuer timestamps must advance past the witnessed floor and lead bound.
        do! Task.Delay(TimeSpan.FromSeconds 3.)
        let! freshBearer = fixture.ServiceToken()

        let! found =
            bearer
                fixture
                freshBearer
                installation
                "operation.get"
                (JsonSerializer.Serialize {| operationId = acceptedId |})

        Expect.equal
            (found.GetProperty("kind").GetString())
            "ACCEPTED"
            "A genuinely post-restore bearer observes the accepted operation"

        let! absent =
            bearer
                fixture
                freshBearer
                installation
                "operation.get"
                (JsonSerializer.Serialize {| operationId = unseenId |})

        Expect.equal
            (absent.GetProperty("kind").GetString())
            "NOT_ACCEPTED"
            "Fresh callers observe the preserved final absence"

        let expiredFrame = openFrame unseenId "RESTORE-MUST-NOT-APPEAR"
        use expired = JsonDocument.Parse(ReadOnlyMemory<byte> expiredFrame)

        let! rejected =
            bearer
                fixture
                freshBearer
                installation
                "operation.execute"
                (expired.RootElement.GetProperty("input").GetRawText())

        Expect.equal
            (rejected.GetProperty("kind").GetString())
            "REJECTED"
            "An unseen expired command cannot execute after restore"

        Expect.equal
            (rejected.GetProperty("rejection").GetProperty("code").GetString())
            "OPERATION_CLOCK_UNAVAILABLE"
            "Durable expiry protects bearer execution"

        let refusal = rejected.GetProperty("rejection")

        Expect.equal
            (refusal.GetProperty("phase").GetString())
            "NOT_STARTED"
            "Frontier refusal reports no mutation attempt"

        let observedHead = operator fixture [ "head" ] 0

        Expect.equal
            (refusal.GetProperty("expiredBefore").GetString())
            (observedHead.GetProperty("expiredBefore").GetString())
            "Bearer refusal exposes the actual committed cutoff"

        let cliRefusal =
            run fixture fixture.Environment expiredFrame 2 |> require "REJECTED"

        Expect.equal
            (cliRefusal.GetProperty("code").GetString())
            "OPERATION_CLOCK_UNAVAILABLE"
            "Published CLI enforces the restored frontier"

        run fixture fixture.Environment acceptedFrame 0
        |> require "ALREADY_ACCEPTED"
        |> ignore

        let finalHead = operator fixture [ "head" ] 0

        for field in [ "installationId"; "operationSequence"; "operationHash" ] do
            Expect.isTrue
                (JsonElement.DeepEquals(finalHead.GetProperty field, trusted.GetProperty field))
                "Refused writes and exact replay preserve the accepted prefix"

        let frontier (head: JsonElement) =
            DateTimeOffset.Parse(
                head.GetProperty("expiredBefore").GetString() |> nonNull,
                Globalization.CultureInfo.InvariantCulture
            )

        Expect.isGreaterThanOrEqual
            (frontier finalHead)
            (frontier trusted)
            "Later final reads never lower trusted expiry"

        let laterCase =
            openFrameForDate
                (Guid.CreateVersion7().ToString("D"))
                "RESTORE-CACHE-SECOND"
                "2026-10-02"

        run fixture fixture.Environment laterCase 0 |> require "ACCEPTED" |> ignore

        let report (cursor: string option) =
            let input: ClaimCore.Application.Contracts.CaseSummaryRequest =
                {
                    Selection =
                        {
                            Filter = CaseListFilter.empty<string>
                            GroupBy = Some CaseGrouping.ReceivedMonth
                        }
                    Page = { AfterCursor = cursor; Limit = 1 }
                }

            let data =
                Api.caseSummary.Input.Write input
                |> ClaimCore.Json.CanonicalWriter.bytes
                |> Encoding.UTF8.GetString

            bearer fixture freshBearer installation "case.summary" data

        let! firstPage = report None

        Expect.equal
            (firstPage.GetProperty("kind").GetString())
            "SUCCEEDED"
            "Populate a report in the running server"

        let cursor =
            firstPage.GetProperty("data").GetProperty("nextCursor").GetString() |> nonNull

        let! warm = report (Some cursor)

        do! stop fixture
        do! fixture.RestartWeb()

        let! cold = report (Some cursor)

        Expect.equal
            (cold.GetProperty("kind").GetString())
            "SUCCEEDED"
            "The old continuation is still authorized by the fresh process"

        Expect.isTrue
            (JsonElement.DeepEquals(
                warm.GetProperty("data").GetProperty("rows"),
                cold.GetProperty("data").GetProperty("rows")
            ))
            "The fresh process reproduces the same report page through its cold-cache fallback"

        fixture.Complete()
    }

[<Tests>]
let tests =
    testCaseAsync
        "published restore refuses missing history and preserves cookie bearer and CLI boundaries across restart"
        (async { do! qualify () |> Async.AwaitTask })
