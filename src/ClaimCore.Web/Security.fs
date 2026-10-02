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
        AbsoluteLifetime: TimeSpan
        Started: int64
        mutable LastActivity: int64
    }

/// The browser cookie contains only a protected random lookup key. The ticket and its identity
/// remain server-side and expire at the earlier of idle and absolute deadlines.
[<Sealed>]
type OidcTicketStore(idle: TimeSpan, absolute: TimeSpan, ?timeProvider: TimeProvider) =
    do
        if idle <= TimeSpan.Zero || absolute < idle then
            invalidArg (nameof idle) "OIDC session lifetimes must be positive and ordered."

    let clock = defaultArg timeProvider TimeProvider.System
    let gate = obj ()
    let tickets = Dictionary<string, TicketEntry>(StringComparer.Ordinal)

    let expire instant timestamp =
        let expired (entry: TicketEntry) =
            let age = clock.GetElapsedTime(entry.Started, timestamp)
            let inactive = clock.GetElapsedTime(entry.LastActivity, timestamp)

            entry.AbsoluteExpiry <= instant
            || age < TimeSpan.Zero
            || inactive < TimeSpan.Zero
            || age >= entry.AbsoluteLifetime
            || inactive >= idle

        tickets
        |> Seq.filter (fun entry -> expired entry.Value)
        |> Seq.map _.Key
        |> Seq.toArray
        |> Array.iter (fun key -> tickets.Remove(key) |> ignore)

    interface ITicketStore with
        member _.StoreAsync(ticket) =
            let key = Guid.NewGuid().ToString("N")

            lock gate (fun () ->
                let instant = clock.GetUtcNow()
                let timestamp = clock.GetTimestamp()
                let expiry = ticket.Properties.ExpiresUtc
                let maximum = instant.Add(absolute)

                let absoluteExpiry =
                    if expiry.HasValue then
                        min maximum expiry.Value
                    else
                        maximum

                expire instant timestamp

                if tickets.Count >= 10000 then
                    invalidOp "The OIDC session store is full."

                tickets.Add(
                    key,
                    {
                        Ticket = ticket
                        AbsoluteExpiry = absoluteExpiry
                        AbsoluteLifetime = absoluteExpiry - instant
                        Started = timestamp
                        LastActivity = timestamp
                    }
                ))

            Task.FromResult(key)

        member _.RenewAsync(key, ticket) =
            lock gate (fun () ->
                expire (clock.GetUtcNow()) (clock.GetTimestamp())

                match tickets.TryGetValue(key) with
                | true, entry -> tickets[key] <- { entry with Ticket = ticket }
                | false, _ -> ())

            Task.CompletedTask

        member _.RetrieveAsync(key) =
            let ticket =
                lock gate (fun () ->
                    let timestamp = clock.GetTimestamp()
                    expire (clock.GetUtcNow()) timestamp

                    match tickets.TryGetValue(key) with
                    | true, entry ->
                        entry.LastActivity <- timestamp
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
