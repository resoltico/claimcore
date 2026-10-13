module ClaimCore.WebTests.IngressTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ClaimCore.Web

let private services () =
    let services = ServiceCollection()
    services.AddOptions() |> ignore
    services.AddLogging() |> ignore
    services.AddSingleton<DynamicIngress>() |> ignore
    services.BuildServiceProvider()

let private request provider path =
    let context = DefaultHttpContext()
    context.RequestServices <- provider
    context.Request.Path <- PathString(path)
    context.Request.Method <- "GET"
    context.Response.Body <- new MemoryStream()
    context

let private boundedCallback () =
    use provider = services ()

    let release =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable authenticated = 0

    let next =
        RequestDelegate(fun _ ->
            authenticated <- authenticated + 1
            release.Task :> Task)

    let held =
        [|
            for _ in 1..24 -> RateLimits.ingress (request provider "/signin-oidc") next
        |]

    try
        let rejected = request provider "/auth/login"
        RateLimits.ingress rejected next |> _.GetAwaiter().GetResult()

        Expect.equal
            rejected.Response.StatusCode
            429
            "One global budget refuses before authentication."

        Expect.equal authenticated 24 "Callback and login cannot mint independent budgets."
        let mutable visited = 0

        let readiness () =
            visited <- visited + 1
            "SYNTHETIC_ONLY", "ACTIVE", false

        let gated = RequestDelegate(fun context -> RateLimits.ingress context next)

        let probes =
            [|
                for _ in 1..1000 do
                    let probe = request provider "/health/ready"
                    probe.Request.Headers.Cookie <- "synthetic=session"
                    yield HostRoutes.health readiness probe gated
            |]

        Task.WhenAll(probes).GetAwaiter().GetResult()
        Expect.equal visited 1000 "Every exact probe terminates in memory."
        Expect.equal authenticated 24 "Cookie-bearing probes never reach authentication."
        let lookalike = request provider "/health/ready/extra"
        HostRoutes.health readiness lookalike gated |> _.GetAwaiter().GetResult()
        Expect.equal lookalike.Response.StatusCode 429 "Health prefixes do not exempt dynamic work."
    finally
        release.TrySetResult(()) |> ignore
        Task.WhenAll(held).GetAwaiter().GetResult()

let tests =
    testCase
        "[CC-WEB-001] global ingress includes callback work while exact health stays cheap"
        boundedCallback
