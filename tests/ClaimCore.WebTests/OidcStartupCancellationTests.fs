module ClaimCore.WebTests.OidcStartupCancellationTests

open System
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Web

type private BlockedMetadata() =
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

let private cancelsBlockedStartup () =
    use handler = new BlockedMetadata()
    use client = new HttpClient(handler, Timeout = TimeSpan.FromSeconds 10.)
    use stop = new CancellationTokenSource()

    let configuration =
        {
            Issuer = Uri("https://issuer.example.test")
            TrustRoot = None
            ClientId = "web"
            ClientSecret = "synthetic"
            ApiAudience = "api"
            ServiceClientId = "service"
            CliClientId = "cli"
        }

    let pending = OidcStartup.verify client configuration stop.Token
    handler.Entered.WaitAsync(TimeSpan.FromSeconds 2.).GetAwaiter().GetResult()
    stop.Cancel()

    Expect.throwsT<OperationCanceledException>
        (fun () -> pending.GetAwaiter().GetResult())
        "Controlled stop is cancellation, not invalid issuer configuration"

let tests =
    testCase "[CC-WEB-001] controlled stop cancels blocked OIDC startup" cancelsBlockedStartup
