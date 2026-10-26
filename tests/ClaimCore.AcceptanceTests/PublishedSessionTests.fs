module ClaimCore.AcceptanceTests.PublishedSessionTests

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Expecto
open Npgsql

let private browserPhase (fixture: PublishedFixture.Fixture) phase name =
    let arguments =
        [
            Path.Combine(fixture.Root, "web/e2e/published-session-driver.mjs")
            fixture.Root
            fixture.Origin
            fixture.Directory
            phase
            name
        ]

    let start = PublishedProcesses.start "node" arguments fixture.Environment
    start.WorkingDirectory <- fixture.Root
    let result = PublishedProcesses.run 120_000 start None

    FixtureFiles.write fixture.Directory (name + ".stdout") result.StandardOutput
    |> ignore

    FixtureFiles.write fixture.Directory (name + ".stderr") result.StandardError
    |> ignore

    Expect.equal result.ExitCode 0 ("Published browser session phase " + name)

    use report =
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture.Directory, name + ".json")))

    let value = report.RootElement

    Expect.equal
        (value.GetProperty("phase").GetString() |> nonNull)
        phase
        "The requested phase completed"

    Expect.isTrue (value.GetProperty("completed").GetBoolean()) "Browser assertions completed"

    Expect.equal
        (value.GetProperty("authenticated").GetBoolean())
        (phase <> "stale")
        "Session authentication matches the phase"

    Expect.equal
        (value.GetProperty("protectedStatus").GetInt32())
        (if phase = "stale" then 401 else 200)
        "The actual protected endpoint enforced the session"

let private lifetimes (fixture: PublishedFixture.Fixture) =
    task {
        let connectionFile = Path.Combine(fixture.Directory, "owner.connection")
        use source = NpgsqlDataSource.Create(File.ReadAllText connectionFile)
        use! connection = source.OpenConnectionAsync()

        use command =
            new NpgsqlCommand(
                "SELECT created_at,absolute_expiry,idle_seconds FROM claimcore.sessions LIMIT 2",
                connection
            )

        use! reader = command.ExecuteReaderAsync()
        let rows = ResizeArray<DateTime * DateTime * float>()
        let mutable reading = true

        while reading do
            let! found = reader.ReadAsync()

            if found then
                rows.Add(reader.GetDateTime 0, reader.GetDateTime 1, reader.GetDouble 2)
            else
                reading <- false

        return List.ofSeq rows
    }

let private peerServed (fixture: PublishedFixture.Fixture) endpoint =
    let path = Path.Combine(fixture.Directory, "web-session-peer.stderr")

    let matches line =
        try
            use source = JsonDocument.Parse(line: string)
            let value = source.RootElement

            match
                value.TryGetProperty "endpoint",
                value.TryGetProperty "outcome",
                value.TryGetProperty "status"
            with
            | (true, actual), (true, outcome), (true, status) ->
                actual.GetString() = endpoint
                && outcome.GetString() = "SUCCEEDED"
                && status.GetInt32() = 200
            | _ -> false
        with :? JsonException ->
            false

    Expect.isTrue
        (File.ReadLines path |> Seq.exists matches)
        ("The additional published process served " + endpoint)

let private concurrentHosts (fixture: PublishedFixture.Fixture) before (primary, secondary) =
    task {
        secondary ()
        browserPhase fixture "verify" "peer-live"
        primary ()
        browserPhase fixture "verify" "primary-live"
        do! fixture.RestartWeb()
        browserPhase fixture "verify" "primary-restarted"
        let! retained = lifetimes fixture

        Expect.equal
            retained
            before
            "Independent processes and restart retain original absolute expiry and idle policy"

        secondary ()
        browserPhase fixture "logout" "peer-logout"
        let! removed = lifetimes fixture
        Expect.isEmpty removed "Logout on the additional process deletes the shared ticket"
        primary ()
        browserPhase fixture "stale" "primary-stale"
        secondary ()
        browserPhase fixture "stale" "peer-stale"
    }

let private qualify () =
    task {
        use fixture = new PublishedFixture.Fixture()
        do! fixture.Start()
        browserPhase fixture "login" "primary-login"
        let! before = lifetimes fixture
        Expect.equal before.Length 1 "Real OIDC login creates exactly one server-side ticket"
        do! fixture.WithAdditionalWeb(concurrentHosts fixture before)
        peerServed fixture "context"
        peerServed fixture "session.logout"
        fixture.Complete()
    }

[<Tests>]
let tests =
    testCaseAsync
        "concurrent published Web instances share OIDC tickets and logout survives stale cookies"
        (async { do! qualify () |> Async.AwaitTask })
