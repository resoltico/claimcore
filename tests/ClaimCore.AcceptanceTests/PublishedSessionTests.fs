module ClaimCore.AcceptanceTests.PublishedSessionTests

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open System.Text
open System.Text.RegularExpressions
open System.Security.Cryptography
open Microsoft.AspNetCore.Authentication
open Expecto
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.DataProtection.KeyManagement
open Microsoft.Extensions.DependencyInjection
open Npgsql

let private safeFailureMarker (marker: string) =
    Regex.IsMatch(
        marker,
        @"\APUBLISHED_SESSION_(LOGIN|VERIFY|LOGOUT|STALE|INVALID)_(SESSION_UNAVAILABLE|CONTEXT_REFUSED|OWNER_MISSING|CREDENTIALS_MISSING|BOOTSTRAP_NOT_ISOLATED|LOGIN_REFUSED|DELETED_SESSION_AUTHENTICATED|STALE_COOKIE_ADMITTED|RETAINED_SESSION_MISSING|LOGOUT_REFUSED|INVALID_PHASE|TIMEOUT|(?:CONTRACT|CREDENTIALS|STATE|BROWSER|NAVIGATE|USERNAME|PASSWORD|SUBMIT|RETURN|BOOTSTRAP|SESSION|CONTEXT|LOGOUT|REPORT|CLOSE)_TIMEOUT|UNEXPECTED)(?:;step=(?:CONTRACT|CREDENTIALS|STATE|BROWSER|NAVIGATE|USERNAME|PASSWORD|SUBMIT|RETURN|BOOTSTRAP|SESSION|CONTEXT|LOGOUT|REPORT|CLOSE);error=(?:CHECK|TIMEOUT|RESOURCE|TARGET_CLOSED|NETWORK|SYNTAX|TYPE|IO|OTHER);network=(?:NONE|REFUSED|RESET|CLOSED|ABORTED|EMPTY|TIMEOUT|TLS|BLOCKED|INVALID_RESPONSE|HTTP2_PROTOCOL|REDIRECT_LOOP|INVALID_REDIRECT|UNSAFE_PORT|OTHER)(?:;navigation=(?:NONE;navigationTiming=NONE|net::ERR_[A-Z0-9_]{1,64};navigationTiming=(?:BEFORE_FAILURE|CLEANUP)))?)?\z",
        RegexOptions.CultureInvariant ||| RegexOptions.NonBacktracking
    )

let private maximumEvidenceBytes = 16384

let private readFailureTail path =
    use source =
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)

    let count = int (min source.Length (int64 maximumEvidenceBytes))
    let offset = source.Length - int64 count
    let bytes = Array.zeroCreate<byte> count
    source.Seek(offset, SeekOrigin.Begin) |> ignore
    source.ReadExactly(bytes.AsSpan())
    bytes, offset > 0L

// HttpsProxy owns this safe exception-code/stack log; never copy other private fixture files.
let private retainProxyFailure reportDirectory directory name =
    try
        match reportDirectory with
        | None -> ()
        | Some report ->
            let bytes, _ = readFailureTail (Path.Combine(directory, "proxy-failures.log"))
            let output = Path.Combine(report, "browser-failure")
            Directory.CreateDirectory output |> ignore

            FixtureFiles.write output ("published-session-" + name + "-proxy.txt") bytes
            |> ignore
    with _ ->
        // Evidence is best effort and must preserve the original browser failure.
        ()

let private metadataName (value: string | null) =
    match value with
    | null -> false
    | value ->
        value.Length > 0
        && value.Length <= 2048
        && (value
            |> Seq.forall (fun character ->
                Char.IsLetterOrDigit character || "._$+<>`@,[]= -&*|'".Contains character))

// RequestLog.exceptionLine emits only type and method metadata, never formatted exceptions.
let private safeExceptionLine (line: string) =
    try
        use document = JsonDocument.Parse line
        let value = document.RootElement

        if value.ValueKind <> JsonValueKind.Object then
            None
        else
            let properties = value.EnumerateObject() |> Seq.toArray
            let names = properties |> Array.map _.Name |> Set.ofArray

            if properties.Length <> 3 || names <> set [ "kind"; "exceptionType"; "stack" ] then
                None
            else
                let kind = value.GetProperty "kind"
                let exceptionType = value.GetProperty "exceptionType"
                let stack = value.GetProperty "stack"

                if
                    kind.ValueKind <> JsonValueKind.String
                    || kind.GetString() <> "exception"
                    || exceptionType.ValueKind <> JsonValueKind.String
                    || not (metadataName (exceptionType.GetString()))
                    || stack.ValueKind <> JsonValueKind.Array
                then
                    None
                else
                    let frames = stack.EnumerateArray() |> Seq.toArray

                    if
                        frames
                        |> Array.exists (fun frame ->
                            frame.ValueKind <> JsonValueKind.String
                            || not (metadataName (frame.GetString())))
                    then
                        None
                    else
                        Some(
                            JsonSerializer.Serialize
                                {|
                                    kind = "exception"
                                    exceptionType = exceptionType.GetString()
                                    stack = frames |> Array.map _.GetString()
                                |}
                        )
    with _ ->
        None

let private retainWebExceptions reportDirectory directory =
    try
        match reportDirectory with
        | None -> ()
        | Some report ->
            let bytes, clipped = readFailureTail (Path.Combine(directory, "web.stderr"))

            let start =
                if clipped then
                    bytes
                    |> Array.tryFindIndex ((=) 10uy)
                    |> Option.map ((+) 1)
                    |> Option.defaultValue bytes.Length
                else
                    0

            let finish = bytes |> Array.tryFindIndexBack ((=) 10uy) |> Option.defaultValue -1

            let text =
                if finish < start then
                    ""
                else
                    UTF8Encoding(false, true).GetString(bytes.AsSpan(start, finish - start + 1))

            let candidates =
                text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                |> Array.choose safeExceptionLine

            let _, lines =
                candidates
                |> Array.rev
                |> Array.fold
                    (fun (size, lines) line ->
                        let count = Encoding.UTF8.GetByteCount(line) + 1

                        if size + count <= maximumEvidenceBytes then
                            size + count, line :: lines
                        else
                            size, lines)
                    (0, [])

            let output = Path.Combine(report, "browser-failure")
            Directory.CreateDirectory output |> ignore

            let retained =
                if List.isEmpty lines then
                    ""
                else
                    String.concat "\n" lines + "\n"

            FixtureFiles.text output "published-session-web-exceptions.jsonl" retained
            |> ignore
    with _ ->
        // Missing logs, partial writes and output failures never replace the test failure.
        ()

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

    let reportDirectory =
        Environment.GetEnvironmentVariable "CLAIMCORE_TEST_REPORT_DIRECTORY"
        |> Option.ofObj

    let result =
        try
            PublishedProcesses.run 120_000 start None
        with _ ->
            retainProxyFailure reportDirectory fixture.Directory name
            reraise ()

    FixtureFiles.write fixture.Directory (name + ".stdout") result.StandardOutput
    |> ignore

    FixtureFiles.write fixture.Directory (name + ".stderr") result.StandardError
    |> ignore

    if result.ExitCode <> 0 then
        retainProxyFailure reportDirectory fixture.Directory name
        let marker = Encoding.UTF8.GetString(result.StandardError).Trim()

        let safe = safeFailureMarker marker

        Console.WriteLine("Published session failure: " + (if safe then marker else "UNCLASSIFIED"))

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
        let fixture = new PublishedFixture.Fixture()

        try
            use _ownership = fixture
            do! fixture.Start()
            browserPhase fixture "login" "primary-login"
            let! before = lifetimes fixture
            Expect.equal before.Length 1 "Real OIDC login creates exactly one server-side ticket"
            do! rollover fixture before
            do! fixture.WithAdditionalWeb(concurrentHosts fixture before)
            peerServed fixture "context"
            peerServed fixture "session.logout"
            fixture.Complete()
        with error ->
            // Web stderr is materialized by fixture disposal, so read it after that scope ends.
            retainWebExceptions
                (Environment.GetEnvironmentVariable "CLAIMCORE_TEST_REPORT_DIRECTORY"
                 |> Option.ofObj)
                fixture.Directory

            return System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw()
    }

[<Tests>]
let tests =
    testCaseAsync
        "published certificate and key rollover retain live tickets across hosts and logout survives stale cookies"
        (async { do! qualify () |> Async.AwaitTask })

[<Tests>]
let failureMarkerTests =
    testCase "published session failure detail admits only closed categories" (fun () ->
        for marker in
            [
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED"
                "PUBLISHED_SESSION_LOGIN_RETURN_TIMEOUT"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=SESSION;error=SYNTAX;network=NONE"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=CONTEXT;error=NETWORK;network=RESET"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=RETURN;error=NETWORK;network=INVALID_RESPONSE"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=RETURN;error=NETWORK;network=HTTP2_PROTOCOL"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=RETURN;error=NETWORK;network=REDIRECT_LOOP"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=RETURN;error=NETWORK;network=INVALID_REDIRECT"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=RETURN;error=NETWORK;network=UNSAFE_PORT"
                "PUBLISHED_SESSION_LOGIN_CONTEXT_REFUSED;step=CONTEXT;error=CHECK;network=NONE"
                "PUBLISHED_SESSION_LOGIN_BOOTSTRAP_TIMEOUT;step=BOOTSTRAP;error=TIMEOUT;network=NONE"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=NAVIGATE;error=NETWORK;network=OTHER;navigation=net::ERR_HTTP_RESPONSE_CODE_FAILURE;navigationTiming=CLEANUP"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=RETURN;error=NETWORK;network=HTTP2_PROTOCOL;navigation=net::ERR_HTTP2_PROTOCOL_ERROR;navigationTiming=BEFORE_FAILURE"
                "PUBLISHED_SESSION_LOGIN_CONTEXT_REFUSED;step=CONTEXT;error=CHECK;network=NONE;navigation=NONE;navigationTiming=NONE"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=NAVIGATE;error=NETWORK;network=OTHER;navigation=net::ERR_"
                + String('A', 64)
                + ";navigationTiming=BEFORE_FAILURE"
            ] do
            Expect.isTrue (safeFailureMarker marker) "Literal safe evidence is admitted"

        for marker in
            [
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=PRIVATE;error=OTHER;network=NONE"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=SESSION;error=private;network=NONE"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=SESSION;error=NETWORK;network=ECONNRESET"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=RETURN;error=NETWORK;network=net::ERR_INVALID_HTTP_RESPONSE"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=RETURN;error=NETWORK;network=net::ERR_HTTP2_PROTOCOL_ERROR"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=RETURN;error=NETWORK;network="
                + String('A', 513)
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=RETURN;error=NETWORK;network=REDIRECT_LOOP;private=secret"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=SESSION;error=OTHER;network=NONE;private=secret"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=SESSION;error=OTHER;network=https://private.invalid"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED\nprivate-marker"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=SESSION;error=OTHER;network=NONE\n"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=NAVIGATE;error=NETWORK;network=OTHER;navigation=net::ERR_;navigationTiming=BEFORE_FAILURE"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=NAVIGATE;error=NETWORK;network=OTHER;navigation=net::ERR_"
                + String('A', 65)
                + ";navigationTiming=BEFORE_FAILURE"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=NAVIGATE;error=NETWORK;network=OTHER;navigation= net::ERR_FAILED;navigationTiming=BEFORE_FAILURE"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=NAVIGATE;error=NETWORK;network=OTHER;navigation=net::ERR_failed;navigationTiming=BEFORE_FAILURE"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=NAVIGATE;error=NETWORK;network=OTHER;navigation=net::ERR_FAILED\n;navigationTiming=BEFORE_FAILURE"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=NAVIGATE;error=NETWORK;network=OTHER;navigation=net::ERR_FAILED;navigationTiming=BEFORE_FAILURE;private=secret"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=NAVIGATE;error=NETWORK;network=OTHER;navigation=https://private.invalid;navigationTiming=BEFORE_FAILURE"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;navigation=net::ERR_FAILED;navigationTiming=BEFORE_FAILURE"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=NAVIGATE;error=NETWORK;network=OTHER;navigation=NONE;navigationTiming=CLEANUP"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=NAVIGATE;error=NETWORK;network=OTHER;navigation=net::ERR_FAILED;navigationTiming=NONE"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=NAVIGATE;error=NETWORK;network=OTHER;navigation=net::ERR_FAILED;navigationTiming=UNKNOWN"
                "PUBLISHED_SESSION_LOGIN_UNEXPECTED;step=NAVIGATE;error=NETWORK;network=OTHER;navigation=net::ERR_FAILED"
            ] do
            Expect.isFalse
                (safeFailureMarker marker)
                "Unapproved fields and captured content stay private")

[<Tests>]
let proxyFailureEvidenceTests =
    testCase "published session failure retains only the bounded safe proxy tail" (fun () ->
        let directory = FixtureFiles.directory ()
        let report = Path.Combine(directory, "reports")

        try
            FixtureFiles.text directory "owner.principal" "private sibling must never be copied"
            |> ignore

            retainProxyFailure (Some report) directory "missing"

            Expect.isFalse
                (Directory.Exists report)
                "Missing proxy evidence never replaces the browser failure"

            let prefix = Encoding.UTF8.GetBytes("old diagnostic prefix\n")
            let tail = Array.create 20000 65uy
            let bytes = Array.append prefix tail
            FixtureFiles.write directory "proxy-failures.log" bytes |> ignore
            retainProxyFailure (Some report) directory "primary-login"

            let output =
                Path.Combine(
                    report,
                    "browser-failure",
                    "published-session-primary-login-proxy.txt"
                )

            let actual = File.ReadAllBytes output
            Expect.equal actual.Length 16384 "The retained tail has a fixed16KiB ceiling"

            Expect.sequenceEqual
                actual
                bytes[bytes.Length - 16384 ..]
                "Only the newest proxy bytes survive"

            Expect.equal
                (Directory.GetFiles(Path.Combine(report, "browser-failure")).Length)
                1
                "No sibling private files are copied"

            Expect.equal
                (File.GetUnixFileMode output)
                (UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                "Evidence stays private until explicit artifact upload"

            let unavailable =
                FixtureFiles.text directory "not-a-directory" "owned synthetic file"

            retainProxyFailure (Some unavailable) directory "io-failure"
            retainProxyFailure None directory "unconfigured"
        finally
            Directory.Delete(directory, true))

[<Tests>]
let webExceptionEvidenceTests =
    testCase
        "published session web exception evidence admits only owned metadata records"
        (fun () ->
            let valid =
                """{"kind":"exception","exceptionType":"System.InvalidOperationException","stack":["ClaimCore.Web.AuthMiddleware+oidc@90-1.MoveNext"]}"""

            Expect.isSome
                (safeExceptionLine valid)
                "The owned RequestLog metadata shape is admitted"

            for invalid in
                [
                    """{"kind":"exception","exceptionType":"System.Exception","stack":[],"message":"private-marker"}"""
                    """{"kind":"exception","kind":"exception","exceptionType":"System.Exception","stack":[]}"""
                    """{"kind":"exception","exceptionType":"System.Exception: private-marker","stack":[]}"""
                    """{"kind":"exception","exceptionType":"https://private.invalid","stack":[]}"""
                    """{"kind":"exception","exceptionType":"System.Exception","stack":["at Method in /private/file"]}"""
                    """{"kind":"exception","exceptionType":"System.Exception","stack":[{"private":"marker"}]}"""
                    """{"kind":"request","exceptionType":"System.Exception","stack":[]}"""
                    "info: framework formatted private-marker"
                    "[]"
                ] do
                Expect.isNone
                    (safeExceptionLine invalid)
                    "Formatted content, unknown fields and non-metadata values are rejected"

            let directory = FixtureFiles.directory ()
            let report = Path.Combine(directory, "reports")

            try
                retainWebExceptions (Some report) directory

                Expect.isFalse
                    (Directory.Exists report)
                    "Missing stderr cannot mask the original failure"

                FixtureFiles.text
                    directory
                    "owner.principal"
                    "private sibling must never be copied"
                |> ignore

                let content =
                    String('X', 20000)
                    + "\n"
                    + valid
                    + "\n"
                    + """{"kind":"exception","exceptionType":"System.Exception","stack":[],"private":"private-marker"}"""
                    + "\nframework private-marker\n"
                    + valid

                FixtureFiles.text directory "web.stderr" content |> ignore
                retainWebExceptions (Some report) directory

                let output =
                    Path.Combine(
                        report,
                        "browser-failure",
                        "published-session-web-exceptions.jsonl"
                    )

                let bytes = File.ReadAllBytes output

                Expect.isTrue
                    (bytes.Length <= 16384)
                    "Retained structured evidence has a16KiB ceiling"

                let retained = Encoding.UTF8.GetString bytes
                let lines = retained.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                Expect.equal lines.Length 1 "Only the complete owned exception line is retained"

                Expect.isSome
                    (safeExceptionLine lines[0])
                    "The retained line still satisfies the strict shape"

                Expect.isFalse
                    (retained.Contains "private-marker")
                    "Private fields/framework text never reach the artifact"

                Expect.equal
                    (File.GetUnixFileMode output)
                    (UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                    "Structured evidence is privately written"

                let unavailable =
                    FixtureFiles.text directory "not-a-directory" "owned synthetic file"

                retainWebExceptions (Some unavailable) directory
                retainWebExceptions None directory
            finally
                Directory.Delete(directory, true))
