module ClaimCore.WebTests.SessionKeyTests

open ClaimCore.Contract

open System
open System.IO
open System.Net
open System.Net.Http
open System.Security.Claims
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open ClaimCore.Web

let private issue (context: HttpContext) =
    task {
        let identity =
            ClaimsIdentity([ Claim(ClaimTypes.NameIdentifier, "synthetic-owner") ], "Cookies")

        context.User <- ClaimsPrincipal identity
        do! context.SignInAsync("Cookies", context.User)
        let antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>()
        let tokens = antiforgery.GetAndStoreTokens context
        do! context.Response.WriteAsync(tokens.RequestToken |> nonNull)
    }
    :> Task

let private check (context: HttpContext) =
    task {
        if not (context.User.Identity |> nonNull).IsAuthenticated then
            context.Response.StatusCode <- 401
        else
            let antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>()

            try
                do! antiforgery.ValidateRequestAsync context
                context.Response.StatusCode <- 204
            with :? AntiforgeryValidationException ->
                context.Response.StatusCode <- 400
    }
    :> Task

let private host directory =
    task {
        let builder = WebApplication.CreateBuilder()
        builder.Logging.ClearProviders() |> ignore
        builder.WebHost.UseTestServer() |> ignore

        SessionKeys.configure
            (SessionKeys.admit directory)
            (TimeSpan.FromMinutes 30.)
            builder.Services

        AuthMiddleware.configure
            {
                Issuer = Uri "https://issuer.example.test/realms/claimcore"
                TrustRoot = None
                ClientId = "claimcore-web"
                ClientSecret = "synthetic-secret"
                ApiAudience = "claimcore-api"
                CliClientId = "claimcore-cli"
            }
            (TimeSpan.FromMinutes 30.)
            (TimeSpan.FromHours 8.)
            builder.Services

        builder.Services.AddAntiforgery(fun options ->
            options.HeaderName <- "X-ClaimCore-Antiforgery")
        |> ignore

        let app = builder.Build()
        app.UseAuthentication() |> ignore
        app.MapGet("/issue", Func<HttpContext, Task>(issue)) |> ignore
        app.MapPost("/check", Func<HttpContext, Task>(check)) |> ignore

        app.MapPost(
            "/logout",
            Func<HttpContext, Task>(fun context -> context.SignOutAsync("Cookies"))
        )
        |> ignore

        do! app.StartAsync()
        return app
    }

let private verify directory cookies token expected =
    task {
        use! app = host directory
        use client = app.GetTestClient()
        client.BaseAddress <- Uri "https://localhost"
        use request = new HttpRequestMessage(HttpMethod.Post, "/check")
        request.Headers.Add("Cookie", (cookies: string))
        request.Headers.Add("X-ClaimCore-Antiforgery", (token: string))
        use! response = client.SendAsync request

        Expect.equal
            response.StatusCode
            expected
            "Cookie and antiforgery survive only their persisted key ring"
    }

let private logout directory cookies =
    task {
        use! resumed = host directory
        use client = resumed.GetTestClient()
        client.BaseAddress <- Uri "https://localhost"
        use request = new HttpRequestMessage(HttpMethod.Post, "/logout")
        request.Headers.Add("Cookie", (cookies: string))
        use! response = client.SendAsync request
        Expect.equal response.StatusCode HttpStatusCode.OK "Production logout succeeds."
        do! resumed.StopAsync()
    }

let private restart () =
    task {
        let directory = Directory.CreateTempSubdirectory("claimcore-session-keys-")

        try
            let shared =
                Directory.CreateDirectory(Path.Combine(directory.FullName, "shared")).FullName

            let sibling =
                Directory.CreateDirectory(Path.Combine(directory.FullName, "sibling")).FullName

            use! app = host shared
            use client = app.GetTestClient()
            client.BaseAddress <- Uri "https://localhost"
            use! response = client.GetAsync "/issue"
            let! token = response.Content.ReadAsStringAsync()

            let cookies =
                response.Headers.GetValues("Set-Cookie")
                |> Seq.map (fun value -> value.Split(';')[0])
                |> String.concat "; "

            do! app.StopAsync()
            do! verify shared cookies token HttpStatusCode.NoContent
            do! verify sibling cookies token HttpStatusCode.Unauthorized
            do! logout shared cookies
            do! verify shared cookies token HttpStatusCode.Unauthorized
        finally
            directory.Delete(true)
    }

type private SessionClock() =
    inherit TimeProvider()
    let mutable instant = DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero)
    let mutable timestamp = 0L
    override _.GetUtcNow() = instant
    override _.GetTimestamp() = timestamp
    override _.TimestampFrequency = TimeSpan.TicksPerSecond

    member _.Advance duration =
        instant <- instant.Add duration
        timestamp <- timestamp + duration.Ticks

    member _.Rollback() = instant <- instant.AddMinutes(-1.)

let private persistedExpiry scenario =
    let directory = Directory.CreateTempSubdirectory("claimcore-session-expiry-")

    try
        let protection = DataProtectionProvider.Create directory
        let clock = SessionClock()
        let storage = TicketPersistence(directory, protection)

        let create () =
            OidcTicketStore(
                TimeSpan.FromMinutes 30.,
                TimeSpan.FromHours 8.,
                timeProvider = clock,
                persistence = storage
            )
            :> ITicketStore

        let ticket =
            AuthenticationTicket(
                ClaimsPrincipal(ClaimsIdentity("synthetic")),
                AuthenticationProperties(),
                "Cookies"
            )

        let original = create ()
        let key = original.StoreAsync(ticket).Result

        match scenario with
        | "idle" -> clock.Advance(TimeSpan.FromMinutes 31.)
        | "absolute" ->
            for _ in 1..23 do
                clock.Advance(TimeSpan.FromMinutes 20.)
                original.RetrieveAsync(key).Result |> ignore

            clock.Advance(TimeSpan.FromMinutes 20.)
        | _ -> clock.Rollback()

        Expect.isNull
            (create().RetrieveAsync(key).Result)
            "Restart never extends a session deadline or accepts backwards time."
    finally
        directory.Delete true

let private protectedFiles () =
    let directory = Directory.CreateTempSubdirectory("claimcore-session-files-")

    try
        let protection = DataProtectionProvider.Create directory
        let storage = TicketPersistence(directory, protection)

        let ticket =
            AuthenticationTicket(
                ClaimsPrincipal(
                    ClaimsIdentity([ Claim("sub", "synthetic-private-identity") ], "synthetic")
                ),
                AuthenticationProperties(),
                "Cookies"
            )

        let key = String.replicate 32 "a"
        storage.Write(key, ticket, DateTimeOffset.UtcNow.AddHours 1., DateTimeOffset.UtcNow)
        let file = Path.Combine(directory.FullName, "sessions", key + ".ticket")

        Expect.equal
            (File.GetUnixFileMode file)
            (UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            "Ticket is private."

        Expect.isFalse
            (File.ReadAllText(file).Contains "synthetic-private-identity")
            "Identity is encrypted at rest."

        let renamed =
            Path.Combine(directory.FullName, "sessions", String.replicate 32 "b" + ".ticket")

        File.Move(file, renamed)
        Expect.isEmpty (storage.Load()) "A ticket cannot be rebound to another lookup key."
        storage.Write(key, ticket, DateTimeOffset.UtcNow.AddHours 1., DateTimeOffset.UtcNow)
        File.WriteAllBytes(file, [| 0uy; 1uy; 2uy |])
        Expect.isEmpty (storage.Load()) "Corrupted ciphertext refuses authentication."
    finally
        directory.Delete true

let private sessionTraffic count (store: ITicketStore) =
    task {
        let ticket =
            AuthenticationTicket(
                ClaimsPrincipal(ClaimsIdentity("synthetic")),
                AuthenticationProperties(),
                "Cookies"
            )

        let! keys = [| for _ in 1..count -> store.StoreAsync ticket |] |> Task.WhenAll

        let read key =
            task {
                let watch = Diagnostics.Stopwatch.StartNew()
                let! found = store.RetrieveAsync key
                Expect.isNotNull found "Live session survives durable activity update"
                return watch.Elapsed.TotalMilliseconds
            }

        let! times =
            [| for key in keys -> Task.Factory.StartNew(fun () -> read key).Unwrap() |]
            |> Task.WhenAll

        Array.sortInPlace times

        return
            {|
                sessions = count
                p50Milliseconds = times[count / 2]
                maximumMilliseconds = Array.last times
            |}
    }

let private sessionLoad () =
    task {
        let directory = Directory.CreateTempSubdirectory("claimcore-session-latency-")

        try
            let protection = DataProtectionProvider.Create directory
            let persistence = TicketPersistence(directory, protection)

            let store =
                OidcTicketStore(
                    TimeSpan.FromMinutes 30.,
                    TimeSpan.FromHours 8.,
                    persistence = persistence
                )
                :> ITicketStore

            let results = ResizeArray<_>()

            for count in [ 1; 8; 24 ] do
                let! result = sessionTraffic count store
                results.Add result

            let output =
                Path.Combine(
                    ClaimCore.TestSupport.RepositoryRoot.find (),
                    "artifacts/verification/sessions"
                )

            Directory.CreateDirectory output |> ignore

            File.WriteAllBytes(
                Path.Combine(output, Guid.NewGuid().ToString("N") + ".json"),
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes
                    {|
                        storage = "local-temporary-directory"
                        platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription
                        results = results
                    |}
            )
        finally
            directory.Delete true
    }

[<Tests>]
let tests =
    testList
        "persisted browser sessions"
        [
            testCaseAsync
                "durable ticket activity latency at one eight and twenty four sessions"
                (async { do! sessionLoad () |> Async.AwaitTask })
            testTask
                "[CC-WEB-001] production session and antiforgery survive restart, isolate sibling rings and retain logout" {
                do! restart ()
            }
            testCase "[CC-WEB-001] persisted sessions keep idle expiry" (fun () ->
                persistedExpiry "idle")
            testCase "[CC-WEB-001] persisted sessions keep absolute expiry" (fun () ->
                persistedExpiry "absolute")
            testCase "[CC-WEB-001] persisted sessions refuse backwards time" (fun () ->
                persistedExpiry "rollback")
            testCase
                "[CC-WEB-001] persisted tickets are private, encrypted and lookup-bound"
                protectedFiles
        ]
