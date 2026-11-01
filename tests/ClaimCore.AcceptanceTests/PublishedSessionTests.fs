module ClaimCore.AcceptanceTests.PublishedSessionTests

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open System.Security.Cryptography
open Microsoft.AspNetCore.Authentication
open Expecto
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.DataProtection.KeyManagement
open Microsoft.Extensions.DependencyInjection
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
                rows.Add((reader.GetDateTime 0, reader.GetDateTime 1, reader.GetDouble 2))
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

let private ticketPayloads (fixture: PublishedFixture.Fixture) =
    task {
        use source =
            NpgsqlDataSource.Create(
                File.ReadAllText(Path.Combine(fixture.Directory, "owner.connection"))
            )

        use! connection = source.OpenConnectionAsync()

        use command =
            new NpgsqlCommand("SELECT protected_ticket FROM claimcore.sessions", connection)

        use! reader = command.ExecuteReaderAsync()
        let rows = ResizeArray<byte array>()
        let mutable reading = true

        while reading do
            let! found = reader.ReadAsync()

            if found then
                rows.Add(reader.GetFieldValue<byte array> 0)
            else
                reading <- false

        return List.ofSeq rows
    }

let private newKeyProtectsTicket (fixture: PublishedFixture.Fixture) keyId oldPayload =
    task {
        let! payloads = ticketPayloads fixture
        Expect.equal payloads.Length 2 "Both independently protected live session rows are present"
        let newer = payloads |> List.filter ((<>) oldPayload)
        Expect.equal newer.Length 1 "Only the new ticket differs from the preserved original row"
        let directory = Path.Combine(fixture.Directory, "new-key-only")

        Directory.CreateDirectory(
            directory,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        )
        |> ignore

        try
            let name = "key-" + string keyId + ".xml"

            let original =
                Path.Combine(fixture.WebEnvironment["CLAIMCORE_WEB_KEY_RING_DIRECTORY"], name)

            FixtureFiles.write directory name (File.ReadAllBytes original) |> ignore

            let provider =
                DataProtectionProvider.Create(
                    DirectoryInfo directory,
                    fun builder -> builder.SetApplicationName("ClaimCore.Web") |> ignore
                )
            // These persisted purpose names are deliberately asserted independently of the writer.
            let protector = provider.CreateProtector("ClaimCore.Web.DatabaseSessionTicket.1")
            let clear = protector.Unprotect newer.Head

            try
                Expect.isNotNull
                    (TicketSerializer.Default.Deserialize clear)
                    "The new ticket decrypts with only the replacement key"
            finally
                CryptographicOperations.ZeroMemory clear

            Expect.throwsT<CryptographicException>
                (fun () -> protector.Unprotect oldPayload |> ignore)
                "The old live ticket independently requires a retained old key"
        finally
            Directory.Delete(directory, true)
    }

let private rollover (fixture: PublishedFixture.Fixture) before =
    task {
        let! originalPayloads = ticketPayloads fixture
        Expect.equal originalPayloads.Length 1 "One original protected ticket precedes rollover"

        let directory =
            DirectoryInfo fixture.WebEnvironment["CLAIMCORE_WEB_KEY_RING_DIRECTORY"]

        let existing = Directory.GetFiles(directory.FullName, "key-*.xml") |> Set.ofArray
        Expect.isNonEmpty existing "The live original session has a persisted protection key"
        let services = ServiceCollection()
        services.AddDataProtection().PersistKeysToFileSystem(directory) |> ignore
        use provider = services.BuildServiceProvider()
        let manager = provider.GetRequiredService<IKeyManager>()

        let replacement =
            manager.CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays 90.)

        Expect.isTrue
            (File.Exists(
                Path.Combine(directory.FullName, "key-" + string replacement.KeyId + ".xml")
            ))
            "An explicit new key is persisted"

        Expect.isTrue
            (existing |> Set.forall File.Exists)
            "Rollover retains every previous decryption key"

        let! _ = fixture.ReplaceWebCertificate()
        browserPhase fixture "verify" "after-key-and-certificate-rollover"
        let! retained = lifetimes fixture

        Expect.equal
            retained
            before
            "Rollover retains the original session's expiry and idle policy"

        let statePath = Path.Combine(fixture.Directory, "published-session-state.json")
        let oldState = File.ReadAllBytes statePath
        browserPhase fixture "login" "new-key-login"
        let! two = lifetimes fixture
        Expect.equal two.Length 2 "A fresh real OIDC login coexists with the old live session"
        browserPhase fixture "verify" "new-key-verify"
        do! newKeyProtectsTicket fixture replacement.KeyId originalPayloads.Head
        browserPhase fixture "logout" "new-key-logout"

        FixtureFiles.write fixture.Directory "published-session-state.json" oldState
        |> ignore

        browserPhase fixture "verify" "old-key-still-live"
        let! remaining = lifetimes fixture

        Expect.equal
            remaining
            before
            "Logging out the new ticket does not remove the old live ticket"
    }

let private qualify () =
    task {
        use fixture = new PublishedFixture.Fixture()
        do! fixture.Start()
        browserPhase fixture "login" "primary-login"
        let! before = lifetimes fixture
        Expect.equal before.Length 1 "Real OIDC login creates exactly one server-side ticket"
        do! rollover fixture before
        do! fixture.WithAdditionalWeb(concurrentHosts fixture before)
        peerServed fixture "context"
        peerServed fixture "session.logout"
        fixture.Complete()
    }

[<Tests>]
let tests =
    testCaseAsync
        "published certificate and key rollover retain live tickets across hosts and logout survives stale cookies"
        (async { do! qualify () |> Async.AwaitTask })
