namespace ClaimCore.Web

open System
open System.Collections.Generic
open System.IO
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.Win32.SafeHandles
open ClaimCore.HostSecurity
open ClaimCore.Contracts

[<Sealed>]
type StateDirectoryLease internal (handle: SafeFileHandle) =
    interface IDisposable with
        member _.Dispose() = handle.Dispose()

[<NoEquality; NoComparison>]
type private TicketEntry =
    {
        Ticket: AuthenticationTicket
        AbsoluteExpiry: DateTimeOffset
        mutable IdleExpiry: DateTimeOffset
    }

/// The browser cookie contains only a protected random lookup key. The ticket and its identity
/// remain server-side and expire at the earlier of idle and absolute deadlines.
[<Sealed>]
type OidcTicketStore(idle: TimeSpan, absolute: TimeSpan, ?clock: unit -> DateTimeOffset) =
    do
        if idle <= TimeSpan.Zero || absolute < idle then
            invalidArg (nameof idle) "OIDC session lifetimes must be positive and ordered."

    let now = defaultArg clock (fun () -> DateTimeOffset.UtcNow)
    let gate = obj ()
    let tickets = Dictionary<string, TicketEntry>(StringComparer.Ordinal)

    let expire instant =
        tickets
        |> Seq.filter (fun entry ->
            entry.Value.AbsoluteExpiry <= instant || entry.Value.IdleExpiry <= instant)
        |> Seq.map _.Key
        |> Seq.toArray
        |> Array.iter (fun key -> tickets.Remove(key) |> ignore)

    interface ITicketStore with
        member _.StoreAsync(ticket) =
            let key = Guid.NewGuid().ToString("N")
            let instant = now ()
            let expiry = ticket.Properties.ExpiresUtc
            let maximum = instant.Add(absolute)

            let absoluteExpiry =
                if expiry.HasValue then
                    min maximum expiry.Value
                else
                    maximum

            lock gate (fun () ->
                expire instant

                if tickets.Count >= 10000 then
                    invalidOp "The OIDC session store is full."

                tickets.Add(
                    key,
                    {
                        Ticket = ticket
                        AbsoluteExpiry = absoluteExpiry
                        IdleExpiry = min absoluteExpiry (instant.Add(idle))
                    }
                ))

            Task.FromResult(key)

        member _.RenewAsync(key, ticket) =
            lock gate (fun () ->
                expire (now ())

                match tickets.TryGetValue(key) with
                | true, entry -> tickets[key] <- { entry with Ticket = ticket }
                | false, _ -> ())

            Task.CompletedTask

        member _.RetrieveAsync(key) =
            let instant = now ()

            let ticket =
                lock gate (fun () ->
                    expire instant

                    match tickets.TryGetValue(key) with
                    | true, entry ->
                        entry.IdleExpiry <- min entry.AbsoluteExpiry (instant.Add(idle))
                        entry.Ticket
                    | false, _ -> Unchecked.defaultof<AuthenticationTicket>)

            Task.FromResult(ticket)

        member _.RemoveAsync(key) =
            lock gate (fun () -> tickets.Remove(key) |> ignore)
            Task.CompletedTask

module Security =
    let acquireStateDirectory (stateDirectory: string) =
        let path = Path.Combine(stateDirectory, ".claimcore-web.lock")

        match PrivateFileService.openExclusive path with
        | Ok stream -> new StateDirectoryLease(stream)
        | Error _ -> WebStartupDiagnostics.refuse WebStartupProblem.StateLockRefused
