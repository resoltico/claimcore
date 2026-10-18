module ClaimCore.WebTests.SessionKeyTests

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
        SessionKeys.configure (SessionKeys.admit directory) builder.Services
        builder.Services.AddAuthentication("Cookies").AddCookie() |> ignore

        builder.Services.AddAntiforgery(fun options ->
            options.HeaderName <- "X-ClaimCore-Antiforgery")
        |> ignore

        let app = builder.Build()
        app.UseAuthentication() |> ignore
        app.MapGet("/issue", Func<HttpContext, Task>(issue)) |> ignore
        app.MapPost("/check", Func<HttpContext, Task>(check)) |> ignore
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
        finally
            directory.Delete(true)
    }

[<Tests>]
let tests =
    testTask "persisted cookie and antiforgery keys survive host restart and isolate sibling rings" {
        do! restart ()
    }
