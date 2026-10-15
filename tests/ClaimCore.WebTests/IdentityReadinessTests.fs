module ClaimCore.WebTests.IdentityReadinessTests

open System
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Expecto

let private issuer = Uri "https://identity.example.test/realms/expected"

let private metadata actual =
    $"""{{"issuer":"{actual}","authorization_endpoint":"https://identity.example.test/auth","token_endpoint":"https://identity.example.test/token","jwks_uri":"https://identity.example.test/keys"}}"""

type private Responses(statuses: HttpStatusCode list, actualIssuer: string) =
    inherit HttpMessageHandler()
    let mutable calls = 0
    member _.Calls = calls

    override _.SendAsync(_, _) =
        let status = List.item (min calls (statuses.Length - 1)) statuses
        calls <- calls + 1
        let response = new HttpResponseMessage(status)
        response.Content <- new StringContent(metadata actualIssuer)
        Task.FromResult response

type private InvalidTls() =
    inherit HttpMessageHandler()
    let mutable calls = 0
    member _.Calls = calls

    override _.SendAsync(_, _) =
        calls <- calls + 1

        Task.FromException<HttpResponseMessage>(
            HttpRequestException(
                HttpRequestError.SecureConnectionError,
                "Synthetic private provider detail"
            )
        )

type private Blocked() =
    inherit HttpMessageHandler()

    let entered =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    member _.Entered = entered.Task

    override _.SendAsync(_, ct) =
        task {
            entered.TrySetResult() |> ignore
            do! Task.Delay(Timeout.Infinite, ct)
            return new HttpResponseMessage()
        }

let private importedRealm () =
    use handler =
        new Responses(
            [
                HttpStatusCode.ServiceUnavailable
                HttpStatusCode.NotFound
                HttpStatusCode.OK
            ],
            issuer.AbsoluteUri
        )

    use client = new HttpClient(handler)

    IdentityReadiness.wait client issuer CancellationToken.None
    |> _.GetAwaiter().GetResult()

    Expect.equal handler.Calls 3 "Wait for expected realm metadata, not merely the TCP port"

let private invalidMetadata () =
    use handler =
        new Responses([ HttpStatusCode.OK ], "https://identity.example.test/realms/other")

    use client = new HttpClient(handler)

    Expect.throwsT<InvalidOperationException>
        (fun () ->
            IdentityReadiness.wait client issuer CancellationToken.None
            |> _.GetAwaiter().GetResult())
        "A successful HTTP response with the wrong issuer fails closed"

    Expect.equal handler.Calls 1 "Permanent metadata failure is not retried"

let private invalidTls () =
    use handler = new InvalidTls()
    use client = new HttpClient(handler)

    Expect.throwsT<InvalidOperationException>
        (fun () ->
            IdentityReadiness.wait client issuer CancellationToken.None
            |> _.GetAwaiter().GetResult())
        "TLS refusal is not treated as realm import readiness"

    Expect.equal handler.Calls 1 "TLS validation failure is not retried"

let private cancellation () =
    use handler = new Blocked()
    use client = new HttpClient(handler)
    use cancelled = new CancellationTokenSource()
    let pending = IdentityReadiness.wait client issuer cancelled.Token
    handler.Entered.WaitAsync(TimeSpan.FromSeconds 2.).GetAwaiter().GetResult()
    cancelled.Cancel()

    Expect.throwsT<OperationCanceledException>
        (fun () -> pending.GetAwaiter().GetResult())
        "Fixture cancellation interrupts prerequisite discovery"

[<Tests>]
let tests =
    testList
        "owned identity prerequisite"
        [
            testCase "waits for imported expected-realm metadata" importedRealm
            testCase "permanent issuer mismatch refuses without retry" invalidMetadata
            testCase "permanent TLS validation refuses without retry" invalidTls
            testCase "blocked metadata readiness is cancellable" cancellation
        ]
