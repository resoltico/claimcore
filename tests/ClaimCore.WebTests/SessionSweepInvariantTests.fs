module ClaimCore.WebTests.SessionSweepInvariantTests

open System
open System.Collections
open System.Reflection
open System.Security.Claims
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open ClaimCore.Web

let private ticket () =
    AuthenticationTicket(
        ClaimsPrincipal(ClaimsIdentity("synthetic")),
        AuthenticationProperties(),
        "Cookies"
    )

let private membership (store: OidcTicketStore) expected =
    let counts =
        typeof<OidcTicketStore>.GetFields(BindingFlags.Instance ||| BindingFlags.NonPublic)
        |> Array.choose (fun field ->
            match field.GetValue(store) with
            | :? ICollection as entries -> Some entries.Count
            | _ -> None)
        |> Array.sort
        |> Array.toList

    Expect.equal
        counts
        [ expected; expected ]
        "Dictionary and sweep list retain exactly one member per live key."

let private randomized () =
    let store = OidcTicketStore(TimeSpan.FromMinutes 30., TimeSpan.FromHours 8.)
    let tickets = store :> ITicketStore
    let keys = ResizeArray<string>()
    let random = Random(42)
    let mutable live = Set.empty
    let value = ticket ()

    for step in 1..2000 do
        if keys.Count = 0 || step % 3 = 0 then
            let key = tickets.StoreAsync(value).Result
            keys.Add key
            live <- live.Add key
        else
            let key = keys[random.Next(keys.Count)]

            match random.Next(3) with
            | 0 -> tickets.RenewAsync(key, value).Wait()
            | 1 ->
                tickets.RemoveAsync(key).Wait()
                live <- live.Remove key
            | _ ->
                Expect.equal
                    (tickets.RetrieveAsync(key).Result |> isNull)
                    (not (live.Contains key))
                    "Requested-key state is exact after rotating and deleting cursor members."

        membership store live.Count

    for key in keys do
        tickets.RemoveAsync(key).Wait()

    membership store 0

let private concurrentRemoval () =
    let store = OidcTicketStore(TimeSpan.FromMinutes 30., TimeSpan.FromHours 8.)
    let tickets = store :> ITicketStore
    let value = ticket ()
    let keys = [ for _ in 1..100 -> tickets.StoreAsync(value).Result ]

    keys
    |> List.map (fun key ->
        Task.Run(fun () ->
            for _ in 1..20 do
                tickets.RenewAsync(key, value).Wait()
                tickets.RetrieveAsync(key).Result |> ignore

            tickets.RemoveAsync(key).Wait()
            tickets.RenewAsync(key, value).Wait()))
    |> List.toArray
    |> Task.WhenAll
    |> _.GetAwaiter().GetResult()

    for key in keys do
        Expect.isNull
            (tickets.RetrieveAsync(key).Result)
            "Concurrent renewal cannot resurrect removed authority."

    membership store 0

let tests =
    testList
        "session sweep invariants"
        [
            testCase
                "[CC-WEB-001] randomized renewal and cursor removal retain one sweep node per key"
                randomized
            testCase
                "[CC-WEB-001] concurrent ticket renewal and removal cannot resurrect a key"
                concurrentRemoval
        ]
