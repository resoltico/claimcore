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
        Node: LinkedListNode<string>
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

    let nodes = LinkedList<string>()
    let mutable cursor: LinkedListNode<string> option = None

    let expired instant timestamp (entry: TicketEntry) =
        let age = clock.GetElapsedTime(entry.Started, timestamp)
        let inactive = clock.GetElapsedTime(entry.LastActivity, timestamp)

        entry.AbsoluteExpiry <= instant
        || age < TimeSpan.Zero
        || inactive < TimeSpan.Zero
        || age >= entry.AbsoluteLifetime
        || inactive >= idle

    let remove key =
        match tickets.TryGetValue key with
        | false, _ -> ()
        | true, entry ->
            if cursor = Some entry.Node then
                cursor <- entry.Node.Next |> Option.ofObj

            nodes.Remove entry.Node
            tickets.Remove key |> ignore

            if cursor.IsNone then
                cursor <- nodes.First |> Option.ofObj

    let sweep quota excluded instant timestamp =
        let count = min quota nodes.Count

        for _ in 1..count do
            match cursor with
            | None -> ()
            | Some node ->
                cursor <-
                    node.Next
                    |> Option.ofObj
                    |> Option.orElseWith (fun () -> nodes.First |> Option.ofObj)

                if Some node.Value <> excluded && expired instant timestamp tickets[node.Value] then
                    remove node.Value

    let lookup key instant timestamp =
        match tickets.TryGetValue key with
        | true, entry when expired instant timestamp entry ->
            remove key
            None
        | true, entry -> Some entry
        | _ -> None

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

                if tickets.Count >= 10000 then
                    sweep nodes.Count None instant timestamp

                if tickets.Count >= 10000 then
                    invalidOp "The OIDC session store is full."

                tickets.Add(
                    key,
                    {
                        Node = nodes.AddLast(key)
                        Ticket = ticket
                        AbsoluteExpiry = absoluteExpiry
                        AbsoluteLifetime = absoluteExpiry - instant
                        Started = timestamp
                        LastActivity = timestamp
                    }
                )

                if cursor.IsNone then
                    cursor <- nodes.First |> Option.ofObj)

            Task.FromResult(key)

        member _.RenewAsync(key, ticket) =
            lock gate (fun () ->
                let instant, timestamp = clock.GetUtcNow(), clock.GetTimestamp()
                let entry = lookup key instant timestamp
                sweep 32 (Some key) instant timestamp

                match entry with
                | Some value -> tickets[key] <- { value with Ticket = ticket }
                | None -> ())

            Task.CompletedTask

        member _.RetrieveAsync(key) =
            let ticket =
                lock gate (fun () ->
                    let timestamp = clock.GetTimestamp()
                    let instant = clock.GetUtcNow()
                    let found = lookup key instant timestamp
                    sweep 32 (Some key) instant timestamp

                    match found with
                    | Some entry ->
                        entry.LastActivity <- timestamp
                        entry.Ticket
                    | None -> Unchecked.defaultof<AuthenticationTicket>)

            Task.FromResult(ticket)

        member _.RemoveAsync(key) =
            lock gate (fun () -> remove key)
            Task.CompletedTask

module Security =
    let acquireStateDirectory (stateDirectory: string) =
        let path = Path.Combine(stateDirectory, ".claimcore-web.lock")

        match PrivateFileService.openExclusive path with
        | Ok stream -> new StateDirectoryLease(stream)
        | Error _ -> WebStartupDiagnostics.refuse WebStartupProblem.StateLockRefused
