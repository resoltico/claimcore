module internal ClaimCore.IntegrationTests.GrowthSessions

open ClaimCore.TestSupport

open System
open System.Diagnostics
open System.Security.Claims
open System.Threading.Tasks
open Expecto
open ClaimCore.Contract
open ClaimCore.Postgres
open ClaimCore.Web
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies

let private store (database: PostgresFixture.Database) protection =
    OidcTicketStore(
        TimeSpan.FromMinutes 30.,
        TimeSpan.FromHours 8.,
        SessionStore(database.RuntimeSource),
        protection,
        ignore
    )
    :> ITicketStore

let private keys (store: ITicketStore) =
    task {
        let found = ResizeArray<string>()

        for index in 0..499 do
            let identity =
                ClaimsIdentity([ Claim("sub", "synthetic-session-" + string index) ], "oidc")

            let! key =
                store.StoreAsync(AuthenticationTicket(ClaimsPrincipal identity, "Cookies"))

            found.Add key

        return found.ToArray()
    }

let private lane (store: ITicketStore) (keys: string array) index =
    task {
        let times = ResizeArray<float>()

        for turn in 0..99 do
            let key = keys[(index + turn * 32) % keys.Length]
            let watch = Stopwatch.StartNew()
            let! ticket = store.RetrieveAsync key

            Expect.isNotNull
                ticket
                "Concurrent session retrieval authenticates its synthetic ticket"

            if turn % 10 = 0 then
                do! store.RenewAsync(key, nonNull ticket)

            times.Add watch.Elapsed.TotalMilliseconds

        return times.ToArray()
    }

let run database protection =
    task {
        let storage = store database protection
        let! active = keys storage
        let watch = Stopwatch.StartNew()
        let! lanes = Task.WhenAll [| for index in 0..31 -> lane storage active index |]
        let latencies = lanes |> Array.concat |> Array.sort

        for key in active do
            do! storage.RemoveAsync key

        return
            {|
                sessions = active.Length
                concurrency = 32
                requests = latencies.Length
                seconds = watch.Elapsed.TotalSeconds
                p50Milliseconds = latencies[latencies.Length / 2]
                p95Milliseconds = latencies[int (float (latencies.Length - 1) * 0.95)]
                p99Milliseconds = latencies[int (float (latencies.Length - 1) * 0.99)]
                unexpectedFailures = 0
            |}
    }
