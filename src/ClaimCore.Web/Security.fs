namespace ClaimCore.Web

open System
open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies

[<NoEquality; NoComparison>]
type private TicketEntry =
    {
        Node: LinkedListNode<string>
        Ticket: AuthenticationTicket
        AbsoluteExpiry: DateTimeOffset
        AbsoluteLifetime: TimeSpan
        Started: int64
        LastActivity: int64
        LastActivityInstant: DateTimeOffset
        InactiveAtStart: TimeSpan
    }

/// The browser cookie contains only a protected random lookup key. The ticket and its identity
/// remain server-side and expire at the earlier of idle and absolute deadlines.
[<Sealed>]
type internal OidcTicketStore
    (
        idle: TimeSpan,
        absolute: TimeSpan,
        ?timeProvider: TimeProvider,
        ?persistence: TicketPersistence
    ) =
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

        let inactive =
            entry.InactiveAtStart + clock.GetElapsedTime(entry.LastActivity, timestamp)

        entry.LastActivityInstant > instant
        || entry.AbsoluteExpiry <= instant
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

            persistence |> Option.iter (fun storage -> storage.Remove key)
            nodes.Remove entry.Node
            tickets.Remove key |> ignore

            if cursor.IsNone then
                cursor <- nodes.First |> Option.ofObj

    let persist key entry =
        persistence
        |> Option.iter (fun storage ->
            storage.Write(key, entry.Ticket, entry.AbsoluteExpiry, entry.LastActivityInstant))

    do
        persistence
        |> Option.iter (fun storage ->
            let instant, timestamp = clock.GetUtcNow(), clock.GetTimestamp()

            for key, ticket, expiry, activity in storage.Load() do
                if activity <= instant && expiry > instant && instant - activity < idle then
                    tickets.Add(
                        key,
                        {
                            Node = nodes.AddLast key
                            Ticket = ticket
                            AbsoluteExpiry = expiry
                            AbsoluteLifetime = min absolute (expiry - instant)
                            Started = timestamp
                            LastActivity = timestamp
                            LastActivityInstant = activity
                            InactiveAtStart = instant - activity
                        }
                    )
                else
                    storage.Remove key

            cursor <- nodes.First |> Option.ofObj)

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

                persistence
                |> Option.iter (fun storage -> storage.Write(key, ticket, absoluteExpiry, instant))

                tickets.Add(
                    key,
                    {
                        Node = nodes.AddLast(key)
                        Ticket = ticket
                        AbsoluteExpiry = absoluteExpiry
                        AbsoluteLifetime = absoluteExpiry - instant
                        Started = timestamp
                        LastActivity = timestamp
                        LastActivityInstant = instant
                        InactiveAtStart = TimeSpan.Zero
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
                | Some value ->
                    let renewed = { value with Ticket = ticket }
                    persist key renewed
                    tickets[key] <- renewed
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
                        let touched =
                            { entry with
                                LastActivity = timestamp
                                LastActivityInstant = instant
                                InactiveAtStart = TimeSpan.Zero
                            }

                        persist key touched
                        tickets[key] <- touched
                        touched.Ticket
                    | None -> Unchecked.defaultof<AuthenticationTicket>)

            Task.FromResult(ticket)

        member _.RemoveAsync(key) =
            lock gate (fun () -> remove key)
            Task.CompletedTask
