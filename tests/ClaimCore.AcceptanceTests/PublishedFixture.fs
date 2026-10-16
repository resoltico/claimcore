module internal ClaimCore.AcceptanceTests.PublishedFixture

open System
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Net.Security
open System.Security.Cryptography.X509Certificates
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Npgsql
open Testcontainers.PostgreSql
open Expecto

let private database (image: string) runId =
    (new PostgreSqlBuilder(image))
        .WithName("claimcore-acceptance-db-" + runId)
        .WithLabel("claimcore.test", "acceptance")
        .WithLabel("claimcore.test-run", runId)
        .WithTmpfsMount("/var/lib/postgresql")
        .WithWaitStrategy(ClaimCore.TestSupport.PostgresReadiness.strategy ())
        .Build()

let private client (certificate: X509Certificate2) =
    let handler = new HttpClientHandler(AllowAutoRedirect = false, UseCookies = false)

    handler.ServerCertificateCustomValidationCallback <-
        fun _ supplied _ errors ->
            use chain = new X509Chain()
            chain.ChainPolicy.TrustMode <- X509ChainTrustMode.CustomRootTrust
            chain.ChainPolicy.CustomTrustStore.Add certificate |> ignore
            chain.ChainPolicy.RevocationMode <- X509RevocationMode.NoCheck

            match supplied with
            | null -> false
            | value ->
                errors &&& SslPolicyErrors.RemoteCertificateNameMismatch = SslPolicyErrors.None
                && chain.Build value

    new HttpClient(handler, Timeout = TimeSpan.FromSeconds(5.))

let private awaitReady (http: HttpClient) (origin: string) (web: PublishedProcesses.Web) =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90.))
        let mutable ready = false

        while not ready do
            if web.HasExited then
                failtest "Published Web exited before readiness; see private fixture logs."

            deadline.Token.ThrowIfCancellationRequested()

            try
                use! response = http.GetAsync(origin + "/health/ready", deadline.Token)
                ready <- response.IsSuccessStatusCode
            with :? HttpRequestException ->
                ()

            if not ready then
                do! Task.Delay(250, deadline.Token)
    }

let private webEnvironment
    directory
    origin
    (identity: IdentityServer.Identity)
    connection
    certificate
    =
    let values = Dictionary<string, string>()

    for name, value in
        [
            "CLAIMCORE_CONNECTION_FILE", connection
            "CLAIMCORE_WEB_CERTIFICATE_PATH", certificate
            "CLAIMCORE_WEB_ORIGIN", origin
            "CLAIMCORE_OIDC_ISSUER", identity.Issuer
            "CLAIMCORE_OIDC_CLIENT_ID", "web"
            "CLAIMCORE_OIDC_CLIENT_SECRET_FILE",
            FixtureFiles.text directory "web.secret" identity.WebSecret
            "CLAIMCORE_OIDC_API_AUDIENCE", "api"
            "CLAIMCORE_OIDC_CLI_CLIENT_ID", "cli"
            "CLAIMCORE_OIDC_CA_CERT_FILE",
            FixtureFiles.text directory "identity.pem" (identity.Root.ExportCertificatePem())
        ] do
        values[name] <- value

    values

let private startPrerequisites (postgres: PostgreSqlContainer) (identity: IdentityServer.Identity) =
    task {
        use startup = new CancellationTokenSource(TimeSpan.FromMinutes 3.)
        use identityHttp = client identity.Root
        identityHttp.MaxResponseContentBufferSize <- 65536L

        let ready (start: CancellationToken -> Task) =
            task {
                try
                    do! start startup.Token
                with error ->
                    startup.Cancel()
                    return raise error
            }

        let databaseReady = ready (fun token -> postgres.StartAsync token)
        let identityReady = ready (fun token -> identity.Start(identityHttp, token) :> Task)
        // Await both tasks even on refusal before fixture teardown disposes their containers.
        do! Task.WhenAll([| databaseReady :> Task; identityReady :> Task |])
    }

let private initialize
    directory
    (postgres: PostgreSqlContainer)
    (identity: IdentityServer.Identity)
    admin
    (environment: Dictionary<string, string>)
    =
    task {
        use source = NpgsqlDataSource.Create(postgres.GetConnectionString())
        use! connection = source.OpenConnectionAsync()

        use command =
            new NpgsqlCommand(
                "CREATE ROLE claimcore_runtime LOGIN PASSWORD 'synthetic-only'",
                connection
            )

        let! _ = command.ExecuteNonQueryAsync()

        environment["CLAIMCORE_ADMIN_CONNECTION_FILE"] <-
            FixtureFiles.text directory "owner.connection" (postgres.GetConnectionString())

        environment["CLAIMCORE_INITIAL_OWNER_PRINCIPAL_FILE"] <-
            FixtureFiles.text
                directory
                "owner.principal"
                (JsonSerializer.Serialize
                    {|
                        operationId = Guid.NewGuid().ToString("D")
                        issuer = identity.Issuer
                        subject = IdentityRealm.ownerSubject
                    |})

        for arguments in [ [ "initialize"; "Etc/UTC" ]; [ "provision-owner" ] ] do
            let result = ProcessRunner.dotnet 60_000 admin arguments environment None
            Expect.equal result.ExitCode 0 ("Published administration " + arguments.Head)

        let settings = NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
        settings.Username <- "claimcore_runtime"
        settings.Password <- "synthetic-only"
        return FixtureFiles.text directory "runtime.connection" settings.ConnectionString
    }

type Fixture() =
    let postgresImage = ClaimCore.TestSupport.PostgresBaseline.image ()
    let ownedDirectory = FixtureCleanup.OwnedDirectory()
    let directory = ownedDirectory.Path
    let root = ClaimCore.TestSupport.RepositoryRoot.find ()
    let postgres = database postgresImage (Guid.NewGuid().ToString("N"))
    let origin = "https://localhost:" + string (FixtureFiles.port ())
    let backend = "https://localhost:" + string (FixtureFiles.port ())
    let identity = new IdentityServer.Identity(origin)
    let certificate = FixtureFiles.certificate ()
    let http = client certificate
    let mutable web: PublishedProcesses.Web option = None
    let mutable commitProxy: CommitReplyProxy.Proxy option = None
    let proxy = new HttpsProxy.Proxy(origin, backend, certificate, http, directory)
    let mutable completed = false
    let mutable cli = ""
    let mutable admin = ""
    let mutable webBinary = ""
    let mutable webSettings = Dictionary<string, string>()
    let adminEnvironment = Dictionary<string, string>()
    let serviceEnvironment = Dictionary<string, string>()

    member _.Directory = directory
    member _.Root = root
    member _.Origin = origin
    member _.Backend = backend
    member _.Issuer = identity.Issuer
    member _.Cli = cli
    member _.WebBinary = webBinary
    member _.WebEnvironment = webSettings
    member _.Http = http
    member _.Environment = serviceEnvironment

    member _.ArmCommitReplyLoss() =
        commitProxy
        |> Option.defaultWith (fun () -> invalidOp "The owned database proxy is not started.")
        |> _.Arm()

    member _.Administration arguments =
        ProcessRunner.dotnet 60_000 admin arguments adminEnvironment None

    member _.Complete() = completed <- true

    member _.Start() =
        task {
            cli <- PublishedProcesses.publish root directory "ClaimCore.Cli"
            admin <- PublishedProcesses.publish root directory "ClaimCore.Database"
            webBinary <- PublishedProcesses.publish root directory "ClaimCore.Web"
            do! startPrerequisites postgres identity
            let! runtime = initialize directory postgres identity admin adminEnvironment
            let settings = NpgsqlConnectionStringBuilder(File.ReadAllText runtime)
            let relay = new CommitReplyProxy.Proxy(settings.Host |> nonNull, settings.Port)
            commitProxy <- Some relay
            settings.Host <- "127.0.0.1"
            settings.Port <- relay.Port
            settings.SslMode <- SslMode.Disable

            let proxiedRuntime =
                FixtureFiles.text directory "proxied-runtime.connection" settings.ConnectionString

            let pfx =
                FixtureFiles.write directory "web.pfx" (certificate.Export(X509ContentType.Pfx))

            webSettings <- webEnvironment directory origin identity proxiedRuntime pfx
            webSettings["CLAIMCORE_WEB_LISTEN_PORT"] <- string (Uri(backend).Port)
            let values = webSettings
            let host = new PublishedProcesses.Web(webBinary, values, directory)
            web <- Some host
            do! proxy.Start()
            do! awaitReady http origin host
            serviceEnvironment["CLAIMCORE_SERVICE_URL"] <- origin + "/"
            serviceEnvironment["CLAIMCORE_OIDC_ISSUER"] <- identity.Issuer
            serviceEnvironment["CLAIMCORE_OIDC_CLIENT_ID"] <- "service"
            serviceEnvironment["CLAIMCORE_CLI_AUTH_MODE"] <- "automation"

            serviceEnvironment["CLAIMCORE_OIDC_CLIENT_SECRET_FILE"] <-
                FixtureFiles.text directory "service.secret" identity.ServiceSecret

            serviceEnvironment["CLAIMCORE_CLI_OIDC_TRUST_ROOT_FILE"] <-
                values["CLAIMCORE_OIDC_CA_CERT_FILE"]

            serviceEnvironment["CLAIMCORE_CLI_SERVICE_TRUST_ROOT_FILE"] <-
                FixtureFiles.text directory "web.pem" (certificate.ExportCertificatePem())

            FixtureFiles.write directory "credentials.json" identity.Credentials |> ignore

            FixtureFiles.text
                directory
                "principals.json"
                (JsonSerializer.Serialize
                    {|
                        issuer = identity.Issuer
                        ownerSubject = IdentityRealm.ownerSubject
                        stewardSubject = IdentityRealm.stewardSubject
                        serviceClientId = "service"
                    |})
            |> ignore
        }

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(
                task {
                    try
                        FixtureCleanup.finish
                            [
                                (fun () ->
                                    web
                                    |> Option.iter (fun value -> (value :> IDisposable).Dispose()))
                                (fun () ->
                                    (proxy :> IAsyncDisposable)
                                        .DisposeAsync()
                                        .AsTask()
                                        .GetAwaiter()
                                        .GetResult())
                                (fun () ->
                                    (identity :> IAsyncDisposable)
                                        .DisposeAsync()
                                        .AsTask()
                                        .GetAwaiter()
                                        .GetResult())
                                (fun () ->
                                    commitProxy
                                    |> Option.iter (fun value -> (value :> IDisposable).Dispose()))
                                (fun () ->
                                    postgres.DisposeAsync().AsTask().GetAwaiter().GetResult())
                                http.Dispose
                                certificate.Dispose
                                (fun () -> ownedDirectory.RemoveGenerated directory)
                            ]
                    with error ->
                        Console.Error.WriteLine(
                            "Synthetic acceptance evidence retained: " + directory
                        )

                        System.Runtime.ExceptionServices.ExceptionDispatchInfo
                            .Capture(error)
                            .Throw()

                    if completed then
                        Directory.Delete(directory, true)
                    else
                        Console.Error.WriteLine(
                            "Synthetic acceptance evidence retained: " + directory
                        )
                }
            )
