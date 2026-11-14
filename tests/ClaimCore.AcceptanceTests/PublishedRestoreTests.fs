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

let private sessions (fixture: PublishedFixture.Fixture) =
    use source =
        NpgsqlDataSource.Create(
            File.ReadAllText(Path.Combine(fixture.Directory, "owner.connection"))
        )

    use connection = source.OpenConnection()

    use command =
        new NpgsqlCommand("SELECT count(*) FROM claimcore.sessions", connection)

    command.ExecuteScalar() |> unbox<int64>

let private openFrame operationId reference =
    let registration =
        ClaimCore.TestSupport.Inputs.registrationWith
            [
                BusinessField.IncidentDate, "2026-09-01"
                BusinessField.IncidentNotificationDate, "2026-09-02"
                BusinessField.IncidentCountry, "LV"
                BusinessField.ClaimantName, "Restore claimant"
                BusinessField.InsurerName, "Restore insurer"
                BusinessField.ClaimedAmount, "10"
                BusinessField.ClaimedCurrency, "EUR"
            ]
            ClaimCore.TestSupport.Inputs.registration

    frame "operation.execute" (caseSubmission operationId reference 0L (CaseAct.Open registration))

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

let private qualify () =
    task {
        use fixture = new PublishedFixture.Fixture()
        do! fixture.Start()
        setup fixture
        PublishedSessionTests.browserPhase fixture "login" "restore-login"
        Expect.equal (sessions fixture) 1L "Real login supplies a restorable server-side ticket"
        let! retainedBearer = fixture.ServiceToken()

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

        do! fixture.RestoreDatabase missingHistoryBackup
        operator fixture [ "maintenance"; "on" ] 0 |> ignore

        let missing =
            operator fixture [ "admit-restored-installation"; "--checkpoint"; checkpoint ] 5

        Expect.equal
            (missing.GetProperty("finding").GetString())
            "EXPECTED_POSITION"
            "Restore cannot manufacture missing accepted history"

        Expect.equal (sessions fixture) 1L "Refused admission preserves the restored ticket"
        let missingHead = operator fixture [ "head" ] 0

        Expect.isLessThan
            (missingHead.GetProperty("operationSequence").GetInt64())
            (trusted.GetProperty("operationSequence").GetInt64())
            "Missing acceptance is independently observed"

        do! fixture.RestoreDatabase completeBackup
        operator fixture [ "maintenance"; "on" ] 0 |> ignore
        let lowered = operator fixture [ "verify"; "--checkpoint"; checkpoint ] 5

        Expect.equal
            (lowered.GetProperty("finding").GetString())
            "EXPIRY_FRONTIER"
            "Backup loses only the later trusted expiry observation"

        let admitted =
            operator fixture [ "admit-restored-installation"; "--checkpoint"; checkpoint ] 0

        Expect.equal
            (admitted.GetProperty("invalidatedSessions").GetInt64())
            1L
            "Published admission invalidates the restored ticket"

        Expect.equal (sessions fixture) 0L "Session invalidation is committed before reopening"
        let head = operator fixture [ "head" ] 0

        Expect.isTrue
            (JsonElement.DeepEquals(head, trusted))
            "Admission preserves accepted history and raises the exact trusted frontier"

        operator fixture [ "verify"; "--checkpoint"; checkpoint ] 0 |> ignore
        operator fixture [ "maintenance"; "off" ] 0 |> ignore
        do! fixture.RestartWeb()
        PublishedSessionTests.browserPhase fixture "stale" "restore-stale-cookie"

        let! found =
            bearer
                fixture
                retainedBearer
                installation
                "operation.get"
                (JsonSerializer.Serialize {| operationId = acceptedId |})

        Expect.equal
            (found.GetProperty("kind").GetString())
            "ACCEPTED"
            "A pre-restore bearer still observes the accepted operation"

        let! absent =
            bearer
                fixture
                retainedBearer
                installation
                "operation.get"
                (JsonSerializer.Serialize {| operationId = unseenId |})

        Expect.equal
            (absent.GetProperty("kind").GetString())
            "NOT_ACCEPTED"
            "Restart preserves the final absence for retained bearer callers"

        let expiredFrame = openFrame unseenId "RESTORE-MUST-NOT-APPEAR"
        use expired = JsonDocument.Parse(ReadOnlyMemory<byte> expiredFrame)

        let! rejected =
            bearer
                fixture
                retainedBearer
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

        fixture.Complete()
    }

[<Tests>]
let tests =
    testCaseAsync
        "published restore refuses missing history and preserves cookie bearer and CLI boundaries across restart"
        (async { do! qualify () |> Async.AwaitTask })
