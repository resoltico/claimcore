module internal ClaimCore.IntegrationTests.SessionHosts

open ClaimCore.TestSupport

open System
open System.IO
open System.Net
open System.Net.Http
open System.Security.Claims
open System.Threading.Tasks
open Expecto
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.Web
open Microsoft.AspNetCore.Antiforgery
open Microsoft.Extensions.DependencyInjection
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.Logging

let private issue (context: HttpContext) =
    task {
        let principal =
            ClaimsPrincipal(
                ClaimsIdentity([ Claim(ClaimTypes.NameIdentifier, "synthetic-human") ], "Cookies")
            )

        context.User <- principal
        do! context.SignInAsync("Cookies", principal)
        let protection = context.RequestServices.GetRequiredService<IAntiforgery>()
        let tokens = protection.GetAndStoreTokens context
        do! context.Response.WriteAsync(nonNull tokens.RequestToken)
    }
    :> Task

let private check (context: HttpContext) =
    task {
        try
            let protection = context.RequestServices.GetRequiredService<IAntiforgery>()
            do! protection.ValidateRequestAsync context
            context.Response.StatusCode <- 204
        with :? AntiforgeryValidationException ->
            context.Response.StatusCode <- 400
    }
    :> Task

let private routes (app: WebApplication) =
    app.MapGet("/issue", Func<HttpContext, Task>(issue)) |> ignore

    app.MapGet(
        "/state",
        Func<HttpContext, IResult>(fun context ->
            if context.User.Identity |> Option.ofObj |> Option.exists _.IsAuthenticated then
                Results.NoContent()
            else
                Results.Unauthorized())
    )
    |> ignore

    app.MapPost("/logout", Func<HttpContext, Task>(fun context -> context.SignOutAsync("Cookies")))
    |> ignore

    app.MapPost("/check", Func<HttpContext, Task>(check)) |> ignore


let private host directory (database: PostgresFixture.Database) =
    task {
        let builder = WebApplication.CreateBuilder()
        builder.Logging.ClearProviders() |> ignore
        builder.WebHost.UseTestServer() |> ignore

        SessionKeys.configure
            (SessionKeys.admit directory)
            (TimeSpan.FromMinutes 30.)
            (SessionStore(database.RuntimeSource))
            builder.Services

        AuthMiddleware.configure
            {
                Issuer = Uri "https://issuer.example/"
                TrustRoot = None
                ClientId = "synthetic-web"
                ClientSecret = "synthetic-secret"
                ApiAudience = "synthetic-api"
                CliClientId = "synthetic-cli"
            }
            (TimeSpan.FromHours 8.)
            builder.Services

        builder.Services.AddAntiforgery(fun options ->
            options.HeaderName <- "X-ClaimCore-Antiforgery")
        |> ignore

        let app = builder.Build()
        app.UseAuthentication() |> ignore

        routes app

        do! app.StartAsync()
        return app
    }

let private request (client: HttpClient) (cookie: string) method path =
    task {
        use message = new HttpRequestMessage(method, "https://localhost" + path)
        message.Headers.Add("Cookie", cookie)
        use! response = client.SendAsync message
        return response.StatusCode
    }

let private verifyAntiforgery (client: HttpClient) (cookie: string) (token: string) =
    task {
        use message = new HttpRequestMessage(HttpMethod.Post, "https://localhost/check")
        message.Headers.Add("Cookie", cookie)
        message.Headers.Add("X-ClaimCore-Antiforgery", token)
        use! response = client.SendAsync message

        Expect.equal
            response.StatusCode
            HttpStatusCode.NoContent
            "Antiforgery proof survives host replacement with the same key ring"
    }

let private directory () =
    let temporary = Path.GetTempPath()

    let physical =
        if
            OperatingSystem.IsMacOS()
            && (temporary.StartsWith("/var/", StringComparison.Ordinal)
                || temporary.StartsWith("/tmp/", StringComparison.Ordinal))
        then
            "/private" + temporary
        else
            temporary

    let directory =
        Path.Combine(physical, "claimcore-session-hosts-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(
        directory,
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
    )
    |> ignore

    directory

let run (database: PostgresFixture.Database) =
    task {
        let directory = directory ()

        try
            use! first = host directory database
            use firstClient = first.GetTestClient()
            use! issued = firstClient.GetAsync "https://localhost/issue"
            issued.EnsureSuccessStatusCode() |> ignore

            let cookie =
                issued.Headers.GetValues("Set-Cookie")
                |> Seq.map (fun value -> value.Split(';')[0])
                |> String.concat "; "

            let! token = issued.Content.ReadAsStringAsync()
            use! second = host directory database
            use secondClient = second.GetTestClient()
            let! shared = request secondClient cookie HttpMethod.Get "/state"

            Expect.equal
                shared
                HttpStatusCode.NoContent
                "Second Web host uses the shared committed session"

            let! rejected = request secondClient cookie HttpMethod.Post "/check"
            Expect.equal rejected HttpStatusCode.BadRequest "Missing antiforgery proof refuses"
            do! verifyAntiforgery secondClient cookie token
            do! first.StopAsync()
            use! restarted = host directory database
            use restartedClient = restarted.GetTestClient()
            let! restored = request restartedClient cookie HttpMethod.Get "/state"

            Expect.equal
                restored
                HttpStatusCode.NoContent
                "Restart with the same key ring retains the database session"

            do! verifyAntiforgery restartedClient cookie token
            let! _ = request secondClient cookie HttpMethod.Post "/logout"
            let! removed = request restartedClient cookie HttpMethod.Get "/state"

            Expect.equal
                removed
                HttpStatusCode.Unauthorized
                "Logout on one Web host revokes another host's later retrieval"

            use! fresh = restartedClient.GetAsync "https://localhost/issue"

            let restoredCookie =
                fresh.Headers.GetValues("Set-Cookie")
                |> Seq.map (fun value -> value.Split(';')[0])
                |> String.concat "; "

            let exec arguments =
                task {
                    let! result = database.Container.ExecAsync arguments

                    Expect.equal
                        result.ExitCode
                        (Nullable 0L)
                        "Owned session backup/restore succeeds"
                }

            do!
                exec
                    [|
                        "pg_dump"
                        "-U"
                        "postgres"
                        "-d"
                        "postgres"
                        "-n"
                        "claimcore"
                        "-f"
                        "/tmp/foundation-sessions.sql"
                    |]

            let! count =
                ProjectionRebuild.invalidateRestoredSessions
                    database.OwnerSource
                    System.Threading.CancellationToken.None

            Expect.equal count 1L "Only the new session exists"
            let! invalidated = request restartedClient restoredCookie HttpMethod.Get "/state"

            Expect.equal
                invalidated
                HttpStatusCode.Unauthorized
                "Invalidation rejects an existing cookie"

            use! owner = database.OwnerSource.OpenConnectionAsync()

            let! _ =
                DatabaseSession.execute
                    owner
                    "DROP SCHEMA claimcore CASCADE"
                    [||]
                    System.Threading.CancellationToken.None

            do!
                exec
                    [|
                        "psql"
                        "-U"
                        "postgres"
                        "-d"
                        "postgres"
                        "-v"
                        "ON_ERROR_STOP=1"
                        "-f"
                        "/tmp/foundation-sessions.sql"
                    |]

            let! resurrected = request restartedClient restoredCookie HttpMethod.Get "/state"

            Expect.equal
                resurrected
                HttpStatusCode.NoContent
                "A real backup restore resurrects the session before invalidation"

            let! _ =
                ProjectionRebuild.invalidateRestoredSessions
                    database.OwnerSource
                    System.Threading.CancellationToken.None

            let! refusedRestore = request restartedClient restoredCookie HttpMethod.Get "/state"

            Expect.equal
                refusedRestore
                HttpStatusCode.Unauthorized
                "The pre-restore cookie cannot authenticate after explicit restored-session invalidation"

        finally
            Directory.Delete(directory, true)
    }
