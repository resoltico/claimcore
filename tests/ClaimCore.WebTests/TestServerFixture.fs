module ClaimCore.WebTests.TestServerFixture

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Cryptography
open System.Security.Claims
open System.Security.Cryptography.X509Certificates
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.RateLimiting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.Logging
open ClaimCore.Web
open ClaimCore.WebTests.RouteFixtures
open ClaimCore.WebTests.TestServerServices

let origin = TestServerServices.origin
let credential = TestServerServices.credential
let digest = TestServerServices.digest

type Reply =
    {
        Status: int
        Body: string
        CacheControl: string
        ContentType: string
        DataUseScope: string option
        DataUsePhase: string option
        RealDataReady: string option
    }

let document (reply: Reply) = JsonDocument.Parse(reply.Body)

let private testApplication assets loginPermits =
    let builder =
        WebApplication.CreateBuilder(WebApplicationOptions(WebRootPath = assets))

    builder.Logging.ClearProviders() |> ignore
    builder.WebHost.UseTestServer() |> ignore
    configureServices loginPermits builder.Services
    let application = builder.Build()

    application.Use(Func<HttpContext, RequestDelegate, Task>(RouteSupport.handleFailures))
    |> ignore

    application.Use(
        Func<HttpContext, RequestDelegate, Task>(fun context next ->
            context.Connection.RemoteIpAddress <- IPAddress.Loopback
            HttpHeaders.apply context
            next.Invoke(context))
    )
    |> ignore

    application.UseAuthentication() |> ignore
    application.UseRateLimiter() |> ignore
    application.UseAuthorization() |> ignore
    application

let private oidcConfiguration assets loginPermits =
    let baseConfiguration = testConfiguration assets loginPermits

    { baseConfiguration with
        Oidc =
            Some
                {
                    Issuer = Uri("https://issuer.example.test/realms/synthetic")
                    TrustRoot = None
                    ClientId = "claimcore-web"
                    ClientSecret = "synthetic-secret"
                    ApiAudience = "claimcore-api"
                    ServiceClientId = "claimcore-service"
                    CliClientId = "claimcore-cli"
                }
    }

let private mapSyntheticLogin (application: WebApplication) =
    (application.MapPost(
        "/__test/oidc-login",
        Func<HttpContext, Task<IResult>>(fun context ->
            task {
                let identity = ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme)

                identity.AddClaim(Claim("sub", "synthetic-owner"))
                let principal = ClaimsPrincipal(identity)

                do!
                    context.SignInAsync(
                        CookieAuthenticationDefaults.AuthenticationScheme,
                        principal
                    )

                return Results.Ok()
            })
    ))
        .RequireRateLimiting("login")
    |> ignore

type Host
    private
    (
        application: WebApplication,
        client: HttpClient,
        runtime: RuntimeStub,
        assets: string,
        certificate: X509Certificate2
    ) =
    let mutable cookies = Map.empty<string, string>

    let acceptCookies (response: HttpResponseMessage) =
        match response.Headers.TryGetValues("Set-Cookie") with
        | true, values ->
            for value in values do
                let pair = value.Split(';', 2)[0]
                let name = pair.Split('=', 2)[0]
                cookies <- Map.add name pair cookies
        | false, _ -> ()

    let createRequest
        (method: HttpMethod)
        (path: string)
        (body: string option)
        (mediaType: string option)
        (token: string option)
        (headers: (string * string) list)
        =
        let request = new HttpRequestMessage(method, path)
        request.Headers.Host <- origin.Authority

        if headers |> List.exists (fun (name, _) -> name = "Origin") |> not then
            request.Headers.TryAddWithoutValidation(
                "Origin",
                origin.GetLeftPart(UriPartial.Authority)
            )
            |> ignore

        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin")
        |> ignore

        if not cookies.IsEmpty then
            let value = cookies |> Map.toList |> List.map snd |> String.concat "; "
            request.Headers.TryAddWithoutValidation("Cookie", value) |> ignore

        token
        |> Option.iter (fun value ->
            request.Headers.TryAddWithoutValidation("X-ClaimCore-Antiforgery", value)
            |> ignore)

        headers
        |> List.iter (fun (name, value) ->
            request.Headers.TryAddWithoutValidation(name, value) |> ignore)

        body
        |> Option.iter (fun value ->
            let content = new ByteArrayContent(Encoding.UTF8.GetBytes(value))

            mediaType
            |> Option.iter (fun value ->
                content.Headers.ContentType <- MediaTypeHeaderValue.Parse(value))

            request.Content <- content)

        request

    let readReply (response: HttpResponseMessage) =
        acceptCookies response

        let header name =
            match response.Headers.TryGetValues(name) with
            | true, values -> values |> Option.ofObj |> Option.bind Seq.tryHead
            | _ -> None

        {
            Status = int response.StatusCode
            Body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            CacheControl =
                response.Headers.CacheControl
                |> Option.ofObj
                |> Option.map string
                |> Option.defaultValue ""
            ContentType =
                response.Content.Headers.ContentType
                |> Option.ofObj
                |> Option.map string
                |> Option.defaultValue ""
            DataUseScope = header "X-ClaimCore-Data-Use-Scope"
            DataUsePhase = header "X-ClaimCore-Data-Use-Phase"
            RealDataReady = header "X-ClaimCore-Real-Data-Ready"
        }

    member _.Runtime = runtime

    member _.SendWithHeaders
        (
            method: HttpMethod,
            path: string,
            body: string option,
            mediaType: string option,
            token: string option,
            headers: (string * string) list
        ) =
        use request = createRequest method path body mediaType token headers

        use response = client.SendAsync(request).GetAwaiter().GetResult()
        readReply response

    member _.SendBytes(method, path, bytes: byte array, mediaType, token) =
        use request = createRequest method path None None token []
        let content = new ByteArrayContent(bytes)
        content.Headers.ContentType <- MediaTypeHeaderValue.Parse(mediaType)
        request.Content <- content
        use response = client.SendAsync(request).GetAwaiter().GetResult()
        readReply response

    member this.Send(method, path, body, mediaType, token) =
        this.SendWithHeaders(method, path, body, mediaType, token, [])

    member this.SessionToken() =
        use snapshot =
            this.Send(HttpMethod.Get, "/api/v3/session", None, None, None) |> document

        snapshot.RootElement
            .GetProperty("outcome")
            .GetProperty("data")
            .GetProperty("antiforgeryToken")
            .GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Synthetic session token is required.")

    member this.Login() =
        this.Send(HttpMethod.Post, "/__test/oidc-login", Some "{}", Some "application/json", None)

    static member Start(?loginPermits: int, ?invalidExportMetadata: bool, ?oidcEnabled: bool) =
        let assets = Directory.CreateTempSubdirectory("claimcore-web-testserver-").FullName

        File.WriteAllText(
            Path.Combine(assets, "index.html"),
            "<!doctype html><title>Synthetic ClaimCore</title>"
        )

        let application = testApplication assets (defaultArg loginPermits 5)

        let runtime =
            RuntimeStub(invalidExportMetadata = defaultArg invalidExportMetadata false)

        let configuration =
            if defaultArg oidcEnabled true then
                oidcConfiguration assets (defaultArg loginPermits 5)
            else
                testConfiguration assets (defaultArg loginPermits 5)

        mapSyntheticLogin application

        HostRoutes.map
            assets
            configuration
            (fun _ -> runtime.ActorCore)
            (fun () -> "SYNTHETIC_ONLY", "ACTIVE", false)
            application

        application.StartAsync().GetAwaiter().GetResult()
        let client = application.GetTestClient()
        client.BaseAddress <- origin
        new Host(application, client, runtime, assets, configuration.Certificate)

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            application.StopAsync().GetAwaiter().GetResult()
            application.DisposeAsync().AsTask().GetAwaiter().GetResult()
            certificate.Dispose()
            Directory.Delete(assets, true)
