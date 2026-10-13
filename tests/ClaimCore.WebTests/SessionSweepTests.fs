module ClaimCore.WebTests.SessionSweepTests

open System
open System.Security.Claims
open Expecto
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open ClaimCore.Web

type private EvaluationClock() =
    inherit TimeProvider()
    let mutable timestamp = 0L
    let mutable evaluations = 0

    override _.GetUtcNow() =
        DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero)

    override _.GetTimestamp() = timestamp

    override _.TimestampFrequency =
        evaluations <- evaluations + 1
        TimeSpan.TicksPerSecond

    member _.Reset() = evaluations <- 0
    member _.Evaluations = evaluations / 2

    member _.Expire() =
        timestamp <- TimeSpan.FromHours(10.).Ticks

let private ticket () =
    AuthenticationTicket(
        ClaimsPrincipal(ClaimsIdentity("synthetic")),
        AuthenticationProperties(),
        "Cookies"
    )

let private bounded count =
    testCase
        ($"[CC-WEB-001] session lookup and renewal examine at most 33 entries at capacity {count}")
        (fun () ->
            let clock = EvaluationClock()

            let store =
                OidcTicketStore(
                    TimeSpan.FromMinutes 30.,
                    TimeSpan.FromHours 8.,
                    timeProvider = clock
                )
                :> ITicketStore

            let value = ticket ()
            let keys = [ for _ in 1..count -> store.StoreAsync(value).Result ]

            for key in [ keys.Head; "absent" ] do
                clock.Reset()
                store.RetrieveAsync(key).Result |> ignore

                Expect.isLessThanOrEqual
                    clock.Evaluations
                    33
                    "Lookup cost is independent of occupancy."

                clock.Reset()
                store.RenewAsync(key, value).Wait()

                Expect.isLessThanOrEqual
                    clock.Evaluations
                    33
                    "Renew does not create another sweep node."

            store.RemoveAsync(keys.Head).Wait()
            store.RenewAsync(keys.Head, value).Wait()

            Expect.isNull
                (store.RetrieveAsync(keys.Head).Result)
                "Removal cannot be renewed into existence.")

let private capacity () =
    let clock = EvaluationClock()

    let store =
        OidcTicketStore(TimeSpan.FromMinutes 30., TimeSpan.FromHours 8., timeProvider = clock)
        :> ITicketStore

    let value = ticket ()
    let keys = [ for _ in 1..10000 -> store.StoreAsync(value).Result ]

    Expect.throws
        (fun () -> store.StoreAsync(value) |> ignore)
        "Valid sessions cannot be evicted to admit another."

    Expect.isNotNull
        (store.RetrieveAsync(keys[9999]).Result)
        "Capacity refusal preserves existing sessions."

    clock.Expire()
    let fresh = store.StoreAsync(value).Result

    Expect.isNotNull
        (store.RetrieveAsync(fresh).Result)
        "Insertion-only full sweep reclaims expired entries."

    Expect.isNull
        (store.RetrieveAsync(keys.Head).Result)
        "Requested expiry is exact even after sweeping."

let tests =
    testList
        "bounded ticket sweeps"
        ([ 1; 100; 10000 ]
         |> List.map bounded
         |> fun tests ->
             tests
             @ [
                 testCase
                     "[CC-WEB-001] full ticket store reclaims expiry without evicting authority"
                     capacity
             ])
