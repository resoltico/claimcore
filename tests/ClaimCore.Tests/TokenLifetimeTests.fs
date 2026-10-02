module ClaimCore.Tests.TokenLifetimeTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Cli

type private TokenClock() =
    inherit TimeProvider()
    let mutable utc = DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)
    let mutable elapsed = 0L
    override _.GetUtcNow() = utc
    override _.GetTimestamp() = elapsed
    override _.TimestampFrequency = TimeSpan.TicksPerSecond
    member _.Elapsed(value: TimeSpan) = elapsed <- elapsed + value.Ticks
    member _.Jump(value: TimeSpan) = utc <- utc.Add(value)

let private response seconds =
    $"""{{"access_token":"synthetic-token","token_type":"Bearer","expires_in":{seconds}}}"""

let private parsed (clock: TokenClock) =
    OAuthTokenClient.parseTokenResponseAt
        clock
        (clock.GetUtcNow())
        (clock.GetTimestamp())
        (Encoding.UTF8.GetBytes(response 60))
    |> Result.defaultWith (fun _ -> failtest "Synthetic token reply must parse.")

let private rollbackBudget () =
    let clock = TokenClock()
    let token = parsed clock
    Expect.isTrue token.CanReuse "Positive reuse control"
    clock.Jump(TimeSpan.FromHours -2.)
    clock.Elapsed(TimeSpan.FromSeconds 29.)
    Expect.isTrue token.CanReuse "Inside conservative elapsed budget"
    clock.Elapsed(TimeSpan.FromSeconds 1.)
    Expect.isFalse token.CanReuse "Exact elapsed safety boundary despite wall rollback"
    clock.Jump(TimeSpan.FromHours 2.)
    Expect.isFalse token.CanReuse "Wall correction does not restore exhausted reuse budget"

let private forwardWall () =
    let clock = TokenClock()
    let token = parsed clock
    clock.Jump(TimeSpan.FromHours 1.)
    Expect.isFalse token.CanReuse "UTC expiry still guards suspend or forward corrections"

let private delayedDelivery () =
    for seconds, expected in [ 14., true; 15., false; 16., false ] do
        let clock = TokenClock()

        use handler =
            { new HttpMessageHandler() with
                override _.SendAsync(_, _) =
                    clock.Elapsed(TimeSpan.FromSeconds seconds)
                    clock.Jump(TimeSpan.FromHours -2.)
                    let reply = new HttpResponseMessage(HttpStatusCode.OK)
                    reply.Content <- new StringContent(response 15)
                    Task.FromResult(reply)
            }

        use client = new HttpClient(handler, false)

        let endpoints =
            {
                Authorization = Uri("https://issuer.example.test/authorize")
                Token = Uri("https://issuer.example.test/token")
                Jwks = Uri("https://issuer.example.test/keys")
            }

        let grant =
            OAuthGrant.AuthorizationCode(
                "synthetic-code",
                Uri("http://127.0.0.1:49152/"),
                "synthetic-verifier"
            )

        let result =
            OAuthTokenClient.acquireWithClock
                clock
                client
                endpoints
                "synthetic-client"
                grant
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Expect.equal
            (Result.isOk result)
            expected
            "Delivery uses dispatch time, not parse completion"

        match result with
        | Ok token ->
            Expect.isTrue token.CanUse "Successful short reply retains initial validity"
            Expect.isFalse token.CanReuse "Short-lived token cannot enter the reuse margin"
        | Error reason ->
            Expect.equal reason "OIDC_TOKEN_UNAVAILABLE" "No secret or provider details"

let tests =
    testList
        "CLI token lifetimes"
        [
            testCase "[CC-CLI-003] rollback cannot extend elapsed token reuse" rollbackBudget
            testCase "[CC-CLI-003] forward wall jumps retain conservative UTC expiry" forwardWall
            testCase
                "[CC-CLI-003] delayed token delivery cannot create fresh reuse lifetime"
                delayedDelivery
        ]
