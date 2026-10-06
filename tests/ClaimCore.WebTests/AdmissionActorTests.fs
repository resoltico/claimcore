module ClaimCore.WebTests.AdmissionActorTests

open System
open System.Net
open System.Security.Claims
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ClaimCore.Web

let private origin = Uri("https://localhost:5443")

let private configuration =
    {
        Issuer = Uri("https://issuer.example.test/realms/claimcore")
        TrustRoot = None
        ClientId = "claimcore-web"
        ClientSecret = "synthetic-client-secret"
        ApiAudience = "claimcore-api"
        ServiceClientId = "claimcore-service"
        CliClientId = "claimcore-cli"
    }

let private antiforgery =
    { new IAntiforgery with
        member _.GetAndStoreTokens _ = invalidOp "Not used"
        member _.GetTokens _ = invalidOp "Not used"
        member _.IsRequestValidAsync _ = Task.FromResult true
        member _.ValidateRequestAsync _ = Task.CompletedTask
        member _.SetCookieTokenAndHeader _ = ()
    }

let private refusingAntiforgery =
    { new IAntiforgery with
        member _.GetAndStoreTokens _ = invalidOp "Not used"
        member _.GetTokens _ = invalidOp "Not used"
        member _.IsRequestValidAsync _ = Task.FromResult false

        member _.ValidateRequestAsync _ =
            Task.FromException(AntiforgeryValidationException("Synthetic CSRF refusal"))

        member _.SetCookieTokenAndHeader _ = ()
    }

let private authenticated claims =
    let principal = ClaimsPrincipal(ClaimsIdentity(claims, "synthetic-authentication"))
    AuthenticateResult.Success(AuthenticationTicket(principal, "synthetic-authentication"))

let private servicePrincipal client subject =
    authenticated [ Claim("azp", client); Claim("sub", subject) ]

let private withAuthentication result action =
    let calls = ref 0

    let authentication =
        { new IAuthenticationService with
            member _.AuthenticateAsync(_, _) =
                calls.Value <- calls.Value + 1
                Task.FromResult result

            member _.ChallengeAsync(_, _, _) = Task.CompletedTask
            member _.ForbidAsync(_, _, _) = Task.CompletedTask
            member _.SignInAsync(_, _, _, _) = Task.CompletedTask
            member _.SignOutAsync(_, _, _) = Task.CompletedTask
        }

    let services = ServiceCollection()
    services.AddSingleton<IAuthenticationService>(authentication) |> ignore
    use provider = services.BuildServiceProvider()
    action provider calls

let private context (provider: IServiceProvider) =
    let request = DefaultHttpContext()
    request.RequestServices <- provider
    request.Request.Scheme <- "https"
    request.Connection.RemoteIpAddress <- IPAddress.Loopback
    request.Request.Headers.Host <- origin.Authority
    request.Request.ContentType <- "application/json"
    request

let private cookieContext provider =
    let request = context provider
    request.Request.Headers.Cookie <- "__Host-ClaimCoreSession=synthetic-opaque"
    request.Request.Headers.Origin <- origin.GetLeftPart(UriPartial.Authority)
    request

let private actorGet request =
    Admission.actorGet configuration (WebBindings.create origin "127.0.0.1" origin.Port) request
    |> _.GetAwaiter().GetResult()

let private actorPostWith selectedAntiforgery request =
    Admission.actorPost
        configuration
        (WebBindings.create origin "127.0.0.1" origin.Port)
        RequestBody.Json
        32
        selectedAntiforgery
        request
    |> _.GetAwaiter().GetResult()

let private actorPost request = actorPostWith antiforgery request

let private bearerIdentity () =
    withAuthentication
        (servicePrincipal "claimcore-service" "service-account")
        (fun provider calls ->
            let request = context provider
            request.Request.Headers.Authorization <- "Bearer synthetic-token"

            match
                actorGet request,
                PrincipalIdentity.service configuration.Issuer "claimcore-service"
            with
            | Ok actual, Ok expected ->
                Expect.equal actual expected "A verified service bearer binds its client identity"
            | _ -> failtest "A verified service bearer must select its client identity"

            Expect.equal calls.Value 1 "One bearer authentication is requested")

    withAuthentication (servicePrincipal "claimcore-cli" "person-1") (fun provider calls ->
        let request = context provider
        request.Request.Headers.Authorization <- "Bearer synthetic-token"

        match actorPost request, PrincipalIdentity.human configuration.Issuer "person-1" with
        | Ok actual, Ok expected ->
            Expect.equal actual expected "A verified interactive bearer binds its human subject"
        | _ -> failtest "A verified interactive bearer must select its human subject"

        Expect.equal calls.Value 1 "One bearer authentication is requested")

let private bearerRefusals () =
    for result in
        [
            AuthenticateResult.Fail("synthetic authentication failure")
            servicePrincipal "unregistered-client" "person-1"
            authenticated [ Claim("azp", "claimcore-cli") ]
        ] do
        withAuthentication result (fun provider calls ->
            let request = context provider
            request.Request.Headers.Authorization <- "Bearer synthetic-token"

            Expect.equal
                (actorPost request)
                (Error AdmissionFailure.SessionRejected)
                "Invalid bearer has no actor"

            Expect.equal calls.Value 1 "Bearer failure never falls back to a cookie")

let private bearerShapeRefusals () =
    withAuthentication (servicePrincipal "claimcore-cli" "person-1") (fun provider calls ->
        let refused configure expected =
            let request = context provider
            request.Request.Headers.Authorization <- "Bearer synthetic-token"
            configure request

            Expect.equal
                (actorPost request)
                (Error expected)
                "Bearer transport is refused before authentication"

        refused
            (fun request -> request.Request.Scheme <- "http")
            AdmissionFailure.UntrustedConnection

        refused
            (fun request ->
                request.Request.Headers.Origin <- origin.GetLeftPart(UriPartial.Authority))
            AdmissionFailure.OriginRejected

        refused
            (fun request -> request.Request.ContentType <- "text/plain")
            AdmissionFailure.UnsupportedMediaType

        refused
            (fun request -> request.Request.ContentLength <- Nullable 33L)
            AdmissionFailure.BodyTooLarge

        Expect.equal calls.Value 0 "Malformed bearer transport cannot reach authentication")

let private browserIdentity () =
    withAuthentication (authenticated [ Claim("sub", "person-2") ]) (fun provider calls ->
        let request = cookieContext provider

        match actorPost request, PrincipalIdentity.human configuration.Issuer "person-2" with
        | Ok actual, Ok expected ->
            Expect.equal actual expected "A browser cookie binds its human subject"
        | _ -> failtest "A valid browser cookie must select its human subject"

        Expect.isOk (actorGet request) "A browser cookie admits a read"
        Expect.equal calls.Value 2 "Each browser request rechecks its ticket")

    for result in [ AuthenticateResult.Fail("synthetic cookie failure"); authenticated [] ] do
        withAuthentication result (fun provider calls ->
            let request = cookieContext provider

            Expect.equal
                (actorPost request)
                (Error AdmissionFailure.SessionRejected)
                "Invalid cookie has no actor"

            Expect.equal calls.Value 1 "Cookie refusal reaches authentication once")

let private browserShapeRefusals () =
    withAuthentication (authenticated [ Claim("sub", "person-2") ]) (fun provider calls ->
        let refused configure expected =
            let request = cookieContext provider
            configure request

            Expect.equal
                (actorPost request)
                (Error expected)
                "Browser transport refuses unsafe shape"

        refused
            (fun request -> request.Request.Scheme <- "http")
            AdmissionFailure.UntrustedConnection

        refused
            (fun request -> request.Request.Headers.Remove("Origin") |> ignore)
            AdmissionFailure.OriginRejected

        refused
            (fun request -> request.Request.Headers["Sec-Fetch-Site"] <- "cross-site")
            AdmissionFailure.FetchMetadataRejected

        refused
            (fun request -> request.Request.ContentType <- "text/plain")
            AdmissionFailure.UnsupportedMediaType

        refused
            (fun request -> request.Request.ContentLength <- Nullable 33L)
            AdmissionFailure.BodyTooLarge

        let csrfRequest = cookieContext provider

        Expect.equal
            (actorPostWith refusingAntiforgery csrfRequest)
            (Error AdmissionFailure.AntiforgeryRejected)
            "CSRF refusal precedes actor authentication"

        Expect.equal calls.Value 0 "Unsafe browser transport cannot reach authentication")

let private getShapeRefusals () =
    withAuthentication (servicePrincipal "claimcore-cli" "person-1") (fun provider calls ->
        let refused configure expected =
            let request = context provider
            request.Request.Headers.Authorization <- "Bearer synthetic-token"
            configure request
            Expect.equal (actorGet request) (Error expected) "GET transport refuses unsafe shape"

        refused
            (fun request -> request.Request.Scheme <- "http")
            AdmissionFailure.UntrustedConnection

        refused
            (fun request ->
                request.Request.Headers.Origin <- origin.GetLeftPart(UriPartial.Authority))
            AdmissionFailure.OriginRejected

        refused
            (fun request -> request.Connection.RemoteIpAddress <- IPAddress.Parse("192.0.2.1"))
            AdmissionFailure.UntrustedConnection

        Expect.equal calls.Value 0 "Unsafe GET transport cannot reach authentication")

let tests =
    testList
        "Web actor admission after synthetic authentication"
        [
            testCase
                "[CC-WEB-001] bearer actor admission binds verified client and subject"
                bearerIdentity
            testCase
                "[CC-WEB-001] failed or malformed bearer identities never select an actor"
                bearerRefusals
            testCase
                "[CC-WEB-001] bearer transport refuses unsafe shapes before authentication"
                bearerShapeRefusals
            testCase
                "[CC-WEB-001] browser cookie admission binds and rechecks a human actor"
                browserIdentity
            testCase
                "[CC-WEB-001] browser cookie refuses unsafe transport before actor lookup"
                browserShapeRefusals
            testCase
                "[CC-WEB-001] bearer GET rejects unsafe transport before actor lookup"
                getShapeRefusals
        ]
