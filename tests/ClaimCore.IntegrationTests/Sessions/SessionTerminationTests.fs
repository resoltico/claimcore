module ClaimCore.IntegrationTests.SessionTerminationTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options

let private principal subject =
    PrincipalKey.human "https://issuer.example/" subject
    |> Result.toOption
    |> Option.get

let private cookie (response: HttpResponseMessage) =
    response.Headers.GetValues("Set-Cookie")
    |> Seq.map (fun value -> value.Split(';')[0])
    |> String.concat "; "

let private create (store: ISessionStore) principal =
    store.Create(
        principal,
        None,
        (fun _ -> [| 1uy |]),
        None,
        TimeSpan.FromMinutes 30.,
        TimeSpan.FromHours 8.,
        token
    )
    |> SessionResults.value

let private requestId () =
    OperationIds.fresh () |> OperationId.create |> Option.get

let private ownRequest () =
    {
        OwnSessionTerminationRequest.OperationId = requestId ()
        SettleOnly = None
        ConfirmRetirement = None
    }

let private completed expectedCount self =
    function
    | SessionTerminationResult.Succeeded value ->
        Expect.equal value { Count = expectedCount; Self = self } "Confirmed deletion only"
    | result -> failtestf "Expected confirmed deletion, got %A" result

let private waitForLocks (database: PostgresFixture.Database) expected =
    task {
        use! connection = database.RuntimeSource.OpenConnectionAsync()
        let watch = Diagnostics.Stopwatch.StartNew()
        let mutable count = 0L

        while count < int64 expected && watch.Elapsed < TimeSpan.FromSeconds 5. do
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock'"
                    [||]
                    token

            count <- DatabaseSession.value<int64> 0 rows.Head

            if count < int64 expected then
                do! Task.Delay 20

        Expect.isGreaterThanOrEqual
            count
            (int64 expected)
            "The actual providers reached their serialization lock"
    }

let private register fixture owner number key =
    task {
        let values =
            Map.fold
                (fun values key value -> Map.add key value values)
                (Submissions.principal key)
                (Submissions.profile
                    {
                        DisplayName = "Synthetic person"
                        DigestEmail = None
                        Contact = None
                    })

        let! reply =
            Service.executeSubmission
                owner
                (Submissions.create
                    "people.register"
                    (id fixture number)
                    (Binding.PeopleNew Revision.zero)
                    values)
                token

        acceptedPeople reply
        return ActorId.create (id fixture number) |> Option.get
    }

let private scopeAndPermission (database: PostgresFixture.Database) =
    task {
        let! owner = bootstrap database.Fixture

        let store =
            SessionStore(database.RuntimeSource, database.Installation) :> ISessionStore

        let human = principal "same-subject"

        let service =
            PrincipalKey.service "https://issuer.example/" "same-subject"
            |> Result.toOption
            |> Option.get

        let other = principal "other-subject"
        let! humanId = register database.Fixture owner 20 human
        let! _ = register database.Fixture owner 21 service
        let! humanTicket = create store human
        let! serviceTicket = create store service
        let! otherTicket = create store other

        use! restricted = database.RuntimeSource.OpenConnectionAsync()
        let mutable refused = None

        try
            let! _ =
                DatabaseSession.execute
                    restricted
                    "UPDATE claimcore.sessions SET principal_key=$1 WHERE session_key=$2"
                    [| box (PrincipalKey.naturalKey other); box humanTicket |]
                    token

            ()
        with :? PostgresException as error ->
            refused <- Some error.SqlState

        Expect.equal
            refused
            (Some "42501")
            "Runtime writers cannot retarget a stored ticket's principal index"
        // A registered actor without roles still owns self termination.
        let! own =
            Service.terminateOwnSessions (core database.Fixture human) (ownRequest ()) token

        completed 1L true own
        let! removed = store.Retrieve(humanTicket, token) |> SessionResults.payload
        Expect.isNone removed "Self deletion removes this principal"
        let! untouched = store.Retrieve(serviceTicket, token) |> SessionResults.payload
        Expect.isSome untouched "HUMAN and SERVICE with equal issuer/subject remain distinct"
        let! untouched = store.Retrieve(otherTicket, token) |> SessionResults.payload
        Expect.isSome untouched "Another principal remains authenticated"
        let! replacementTicket = create store human
        let deputy = principal "deputy-owner"
        let! _ = register database.Fixture owner 22 deputy

        let assignment =
            {
                GrantInput.Role = Role.Owner
                Scope = GrantTarget.Installation
            }

        let! granted = grant owner (id database.Fixture 23) deputy assignment true
        acceptedPeople granted
        let deputyService = core database.Fixture deputy

        let! offer =
            Service.grants
                deputyService
                {
                    ActorId = ActorId.value humanId
                    AfterCursor = None
                    Limit = 20
                }
                token

        match offer with
        | Outcome.Succeeded page ->
            Expect.isTrue page.CanTerminateSessions "Owner action is supplied by the core"
        | value -> failtestf "%A" value

        let! revoked = grant owner (id database.Fixture 24) deputy assignment false
        acceptedPeople revoked

        let! denied =
            Service.terminatePersonSessions
                deputyService
                {
                    ActorId = ActorId.value humanId
                    OperationId = requestId ()
                    SettleOnly = None
                    ConfirmRetirement = None
                }
                token

        Expect.equal
            denied
            (SessionTerminationResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Revoked display-time permission cannot authorize deletion"

        let! deniedRetirement =
            Service.terminatePersonSessions
                deputyService
                {
                    ActorId = ActorId.value humanId
                    OperationId = requestId ()
                    SettleOnly = None
                    ConfirmRetirement = Some true
                }
                token

        Expect.equal deniedRetirement denied "Retirement rechecks the same current owner authority"

        let! confirmed =
            Service.terminatePersonSessions
                owner
                {
                    ActorId = ActorId.value humanId
                    OperationId = requestId ()
                    SettleOnly = None
                    ConfirmRetirement = None
                }
                token

        completed 1L false confirmed

        for taskName, number, revision in [ "people.disable", 25, 1L; "people.enable", 26, 2L ] do
            let! reply =
                Service.executeSubmission
                    owner
                    (Submissions.create
                        taskName
                        (id database.Fixture number)
                        (Binding.People(humanId, Revision.create revision |> Option.get))
                        Map.empty)
                    token

            acceptedPeople reply

        let! stillDeleted =
            store.Retrieve(replacementTicket, token) |> SessionResults.payload

        Expect.isNone
            stillDeleted
            "Disabling and re-enabling an actor never recreates deleted sessions"

        let! ownerTicket = create store database.Fixture.Owner

        let! ownerPerson =
            database.Fixture.Store.Read
                {
                    CancellationToken = token
                    Work = fun tx -> PersonAcquisition.principal tx database.Fixture.Owner
                }

        let ownerId = ownerPerson |> Result.toOption |> Option.flatten |> Option.get |> _.Id

        let! selfFromPerson =
            Service.terminatePersonSessions
                owner
                {
                    ActorId = ActorId.value ownerId
                    OperationId = requestId ()
                    SettleOnly = None
                    ConfirmRetirement = None
                }
                token

        completed 1L true selfFromPerson
        let! ownerRemoved = store.Retrieve(ownerTicket, token) |> SessionResults.payload
        Expect.isNone ownerRemoved "Owner termination targeting the caller reports self scope"
        let! _ = store.Renew(humanTicket, human, [| 2uy |], (fun _ -> true), token)
        let! stillRemoved = store.Retrieve(humanTicket, token) |> SessionResults.payload
        Expect.isNone stillRemoved "Renewal cannot recreate a deleted ticket"
    }

let private creationCut (database: PostgresFixture.Database) =
    task {
        let actor = principal "synthetic-human"
        let services = core database.Fixture actor

        let first =
            SessionStore(database.RuntimeSource, database.Installation) :> ISessionStore

        let second =
            SessionStore(database.RuntimeSource, database.Installation) :> ISessionStore

        let! oldFlow = first.StartFlow token
        let! before = create first actor

        use! holder = database.OwnerSource.OpenConnectionAsync()
        use! locked = holder.BeginTransactionAsync()

        let! _ =
            DatabaseSession.rows
                holder
                "SELECT session_key FROM claimcore.sessions WHERE session_key=$1 FOR UPDATE"
                [| box before |]
                token

        let revocation = Service.terminateOwnSessions services (ownRequest ()) token
        do! waitForLocks database 1
        let racingFlow = second.StartFlow token
        do! waitForLocks database 2
        do! locked.CommitAsync()

        let! revoked = revocation
        completed 1L true revoked
        let! afterCutFlow = racingFlow
        let! oldTicket = second.Retrieve(before, token) |> SessionResults.payload
        Expect.isNone oldTicket "The cut is already effective on the other host"

        let now = DateTimeOffset.UtcNow

        let proof =
            {
                IssuedAt = now
                AuthenticatedAt = now
            }

        let complete (store: ISessionStore) id =
            store.Create(
                actor,
                Some(id, proof),
                (fun _ -> [| 1uy |]),
                None,
                TimeSpan.FromMinutes 30.,
                TimeSpan.FromHours 8.,
                token
            )

        let refused id =
            task {
                try
                    let! _ = complete first id
                    failtest "A retired or reused sign-in flow cannot issue a ticket"
                with :? System.Security.Authentication.AuthenticationException ->
                    ()
            }

        do! refused oldFlow
        let! newer = complete second afterCutFlow
        let! current = first.Retrieve(newer.Value, token) |> SessionResults.payload
        Expect.isSome current "A flow queued on the other side of the committed cut is admitted"
        do! refused afterCutFlow

        // The opposite order: the callback already owns browser order and is
        // waiting for its principal when termination attempts to take the cut.
        let! earlierFlow = first.StartFlow token
        use! principalHeld = holder.BeginTransactionAsync()
        let! _ = PrincipalLock.acquire holder actor token
        let callback = complete first earlierFlow
        do! waitForLocks database 1
        let laterCut = Service.terminateOwnSessions services (ownRequest ()) token
        do! waitForLocks database 2
        do! principalHeld.CommitAsync()
        let! issued = callback
        let! cut = laterCut
        completed 2L true cut
        let! ended = second.Retrieve(issued.Value, token) |> SessionResults.payload
        Expect.isNone ended "A callback committed before the cut is counted and retired"
    }

let private heldLedgerHead (database: PostgresFixture.Database) =
    task {
        let store =
            SessionStore(database.RuntimeSource, database.Installation) :> ISessionStore

        let actor = principal "unrelated-head"
        use! connection = database.OwnerSource.OpenConnectionAsync(token)
        use! held = connection.BeginTransactionAsync(token)
        let! _ = Transactions.head connection token

        let! flow = (store.StartFlow token).WaitAsync(TimeSpan.FromSeconds 3.)
        let now = DateTimeOffset.UtcNow

        let create =
            store.Create(
                actor,
                Some(
                    flow,
                    {
                        IssuedAt = now
                        AuthenticatedAt = now
                    }
                ),
                (fun _ -> [| 1uy |]),
                None,
                TimeSpan.FromMinutes 30.,
                TimeSpan.FromHours 8.,
                token
            )

        let! ticket = create.WaitAsync(TimeSpan.FromSeconds 3.)
        let! found = store.Retrieve(ticket.Value, token) |> SessionResults.payload
        Expect.isSome found "Both real storage phases finish while the unrelated head is held"
        do! held.RollbackAsync token
    }

let private staleStatusOnly (database: PostgresFixture.Database) =
    task {
        let! owner = bootstrap database.Fixture

        let store =
            SessionStore(database.RuntimeSource, database.Installation) :> ISessionStore

        let human = principal "identity-audit-status-only"
        let! statusTicket = create store human
        let! _ = register database.Fixture owner 89 human
        let! flow = store.StartFlow token
        do! Task.Delay(TimeSpan.FromSeconds 3., token)
        let now = DateTimeOffset.UtcNow
        let mutable evidence = None

        let! fresh =
            store.Create(
                human,
                Some(
                    flow,
                    {
                        IssuedAt = now
                        AuthenticatedAt = now
                    }
                ),
                (fun credential ->
                    match credential with
                    | BrowserCredential.Protected _ ->
                        evidence <- Some credential
                        [| 1uy |]
                    | _ -> failtest "The positive control requires an actual protected ticket"),
                None,
                TimeSpan.FromMinutes 30.,
                TimeSpan.FromHours 8.,
                token
            )
            |> SessionResults.value

        let actor = core database.Fixture human
        let old = now.AddMinutes(-1.)
        let command = ownRequest ()

        for credential in
            [
                VerifiedCredential.Browser BrowserCredential.StatusOnly
                VerifiedCredential.HumanBearer
                    {
                        IssuedAt = old
                        AuthenticatedAt = old
                    }
            ] do
            let caller = Service.withCredential credential actor
            let! result = Service.terminateOwnSessions caller command token

            Expect.equal
                result
                (SessionTerminationResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
                "A bound account needs current proof and cannot fall back to its principal"

        let! retained = store.Retrieve(fresh, token) |> SessionResults.payload
        let! retainedStatus = store.Retrieve(statusTicket, token) |> SessionResults.payload
        Expect.isSome retained "The fresh protected ticket survives"
        Expect.isSome retainedStatus "Refused termination has no effect on any ticket"

        let qualified =
            actor
            |> Service.withCredential (VerifiedCredential.Browser(Option.get evidence))

        let! accepted = Service.terminateOwnSessions qualified command token
        completed 2L true accepted
        let! ended = store.Retrieve(fresh, token) |> SessionResults.payload

        Expect.isNone
            ended
            "A roleless account with current protected proof can end its account sessions"
    }

let private retrieveRenewAndAdmittedWork (database: PostgresFixture.Database) =
    task {
        let directory = SessionHosts.directory ()
        let actor = principal "synthetic-human"
        let services = core database.Fixture actor

        let admitted =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let finish =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let extra (app: WebApplication) =
            app.MapGet(
                "/admitted",
                Func<HttpContext, Task>(fun context ->
                    task {
                        if
                            context.User.Identity
                            |> Option.ofObj
                            |> Option.exists _.IsAuthenticated
                        then
                            admitted.TrySetResult() |> ignore
                            do! finish.Task
                            context.Response.StatusCode <- 204
                        else
                            context.Response.StatusCode <- 401
                    })
            )
            |> ignore

        try
            use! first = SessionHosts.host directory database extra
            use firstClient = first.GetTestClient()
            use! second = SessionHosts.host directory database (fun _ -> ())
            use secondClient = second.GetTestClient()
            use! issued = firstClient.GetAsync "https://localhost/issue"
            issued.EnsureSuccessStatusCode() |> ignore

            let admittedWork =
                SessionHosts.request firstClient (cookie issued) HttpMethod.Get "/admitted"

            do! admitted.Task.WaitAsync(TimeSpan.FromSeconds 5.)
            let! result = Service.terminateOwnSessions services (ownRequest ()) token
            completed 1L true result
            finish.TrySetResult() |> ignore
            let! completedWork = admittedWork

            Expect.equal
                completedWork
                HttpStatusCode.NoContent
                "Previously admitted work may finish"

            use! _issuedForRetrieval = firstClient.GetAsync "https://localhost/issue"
            use! connection = database.OwnerSource.OpenConnectionAsync()

            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT session_key FROM claimcore.sessions WHERE principal_key=$1"
                    [| box (PrincipalKey.naturalKey actor) |]
                    token

            let key = DatabaseSession.value<string> 0 (List.exactlyOne rows)

            let firstStore =
                first.Services
                    .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                    .Get("Cookies")
                    .SessionStore
                |> nonNull

            let secondStore =
                second.Services
                    .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                    .Get("Cookies")
                    .SessionStore
                |> nonNull

            let! ticket = firstStore.RetrieveAsync key
            Expect.isNotNull ticket "Real protected ticket is available before deletion"

            use! retrieveLock = connection.BeginTransactionAsync()

            let! _ =
                DatabaseSession.rows
                    connection
                    "SELECT session_key FROM claimcore.sessions WHERE session_key=$1 FOR UPDATE"
                    [| box key |]
                    token

            let retrieval = secondStore.RetrieveAsync key
            do! waitForLocks database 1
            let revocation = Service.terminateOwnSessions services (ownRequest ()) token
            do! waitForLocks database 2
            do! retrieveLock.CommitAsync()
            let! retrieved = retrieval
            Expect.isNotNull retrieved "Retrieval admitted before the revoke cut may finish"
            let! result = revocation
            completed 1L true result
            let! missing = firstStore.RetrieveAsync key
            Expect.isNull missing "Retrieval after revocation commit refuses the old ticket"
            use! issued = firstClient.GetAsync "https://localhost/issue"

            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT session_key FROM claimcore.sessions WHERE principal_key=$1"
                    [| box (PrincipalKey.naturalKey actor) |]
                    token

            let key = DatabaseSession.value<string> 0 (List.exactlyOne rows)
            let! ticket = firstStore.RetrieveAsync key
            use! held = connection.BeginTransactionAsync()

            let! _ =
                DatabaseSession.rows
                    connection
                    "SELECT session_key FROM claimcore.sessions WHERE session_key=$1 FOR UPDATE"
                    [| box key |]
                    token

            let renewal = secondStore.RenewAsync(key, nonNull ticket)
            do! waitForLocks database 1
            let revocation = Service.terminateOwnSessions services (ownRequest ()) token
            do! waitForLocks database 2
            do! held.CommitAsync()
            do! renewal
            let! result = revocation
            completed 1L true result
            do! secondStore.RenewAsync(key, nonNull ticket)
            let! missing = firstStore.RetrieveAsync key
            Expect.isNull missing "Deleted rows are not resurrected by a later renewal"

            let! status =
                SessionHosts.request secondClient (cookie issued) HttpMethod.Get "/state"

            Expect.equal
                status
                HttpStatusCode.Unauthorized
                "Another host refuses the deleted cookie"

            use! mismatched = firstClient.GetAsync "https://localhost/issue"

            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT session_key FROM claimcore.sessions WHERE principal_key=$1"
                    [| box (PrincipalKey.naturalKey actor) |]
                    token

            let mismatchedKey = DatabaseSession.value<string> 0 (List.exactlyOne rows)
            let other = principal "metadata-other"

            let! _ =
                DatabaseSession.execute
                    connection
                    "UPDATE claimcore.sessions SET principal_key=$2 WHERE session_key=$1"
                    [| box mismatchedKey; box (PrincipalKey.naturalKey other) |]
                    token

            let! refused =
                SessionHosts.request secondClient (cookie mismatched) HttpMethod.Get "/state"

            Expect.equal
                refused
                HttpStatusCode.Unauthorized
                "Decrypted ticket identity must match its stored principal index"

            let! absent = firstStore.RetrieveAsync mismatchedKey
            Expect.isNull absent "Metadata mismatch removes the invalid ticket"
            use! _current = firstClient.GetAsync "https://localhost/issue"

            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT session_key FROM claimcore.sessions WHERE principal_key=$1"
                    [| box (PrincipalKey.naturalKey actor) |]
                    token

            let currentKey = DatabaseSession.value<string> 0 (List.exactlyOne rows)
            let! validTicket = firstStore.RetrieveAsync currentKey

            let otherTicket =
                Microsoft.AspNetCore.Authentication.AuthenticationTicket(
                    System.Security.Claims.ClaimsPrincipal(
                        System.Security.Claims.ClaimsIdentity(
                            [ System.Security.Claims.Claim("sub", "metadata-other") ],
                            "oidc"
                        )
                    ),
                    "Cookies"
                )

            let! otherKey = secondStore.StoreAsync otherTicket

            let! _ =
                DatabaseSession.execute
                    connection
                    "UPDATE claimcore.sessions SET protected_ticket=(SELECT protected_ticket FROM claimcore.sessions WHERE session_key=$2) WHERE session_key=$1"
                    [| box currentKey; box otherKey |]
                    token

            do! secondStore.RenewAsync(currentKey, nonNull validTicket)
            let! absent = firstStore.RetrieveAsync currentKey
            Expect.isNull absent "Renew validates the previous decrypted ticket under the row lock"
            let! untouched = firstStore.RetrieveAsync otherKey

            Expect.isNotNull
                untouched
                "Association refusal never deletes another principal's session"
        finally
            finish.TrySetResult() |> ignore
            Directory.Delete(directory, true)
    }

let private rollbackAndUnknown (database: PostgresFixture.Database) =
    task {
        let actor = principal "termination-uncertainty"
        let services = core database.Fixture actor

        let store =
            SessionStore(database.RuntimeSource, database.Installation) :> ISessionStore

        let! key = create store actor

        do!
            sql
                database
                "CREATE FUNCTION claimcore.reject_session_delete() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic late deletion failure'; END $$; CREATE TRIGGER reject_session_delete AFTER DELETE ON claimcore.sessions FOR EACH STATEMENT EXECUTE FUNCTION claimcore.reject_session_delete()"
                [||]

        let! failed = Service.terminateOwnSessions services (ownRequest ()) token

        Expect.equal
            failed
            (SessionTerminationResult.Failed CoreFault.StoreUnavailable)
            "Late failure does not claim completion"

        let! retained = store.Retrieve(key, token) |> SessionResults.payload
        Expect.isSome retained "Late failure rolls deletion back"

        do!
            sql
                database
                "DROP TRIGGER reject_session_delete ON claimcore.sessions; DROP FUNCTION claimcore.reject_session_delete()"
                [||]

        let settings =
            NpgsqlConnectionStringBuilder(PostgresFixture.ownerConnectionString database)

        use proxy =
            new CommitProxy.Proxy(
                nonNull settings.Host,
                settings.Port,
                CommitProxy.Drop.AfterCommit
            )

        settings.Host <- "127.0.0.1"
        settings.Port <- proxy.Port
        settings.SslMode <- SslMode.Disable
        settings.GssEncryptionMode <- GssEncryptionMode.Disable
        settings.Pooling <- false
        use source = NpgsqlDataSource.Create settings.ConnectionString

        let uncertainService =
            Service.create
                BuildIdentity.current.Version
                database.Fixture.InstallationId
                (LedgerStore(source, database.Installation, database.Cipher, Transactions.timeouts)
                :> IStore)
                database.Fixture.Calendar
                actor

        let request = ownRequest ()
        let! unknown = Service.terminateOwnSessions uncertainService request token

        Expect.equal
            unknown
            SessionTerminationResult.OutcomeUnknown
            "Lost commit acknowledgement has no count or ledger proof"

        let! old = store.Retrieve(key, token) |> SessionResults.payload
        Expect.isNone old "The synthetic after-commit cut actually landed"
        let! newer = create store actor

        let! missing =
            Service.terminateOwnSessions
                services
                { ownRequest () with
                    SettleOnly = Some true
                }
                token

        Expect.equal
            missing
            SessionTerminationResult.OutcomeUnknown
            "Settlement without a saved result does not start a cut"

        let! untouched = store.Retrieve(newer, token) |> SessionResults.payload
        Expect.isSome untouched "Settlement without a saved result preserves newer tickets"

        let! retried =
            Service.terminateOwnSessions services { request with SettleOnly = Some true } token

        completed 1L true retried

        let! conflict =
            Service.terminateOwnSessions
                (core database.Fixture (principal "other-principal"))
                request
                token

        Expect.equal
            conflict
            (SessionTerminationResult.Rejected Refusal.IdempotencyConflict)
            "The original request cannot change intent"

        let expired =
            {
                SettleOnly = None
                ConfirmRetirement = None
                OwnSessionTerminationRequest.OperationId =
                    Guid.CreateVersion7(DateTimeOffset.UtcNow - TimeSpan.FromHours 1.)
                    |> OperationId.create
                    |> Option.get
            }

        let! obsolete = Service.terminateOwnSessions services expired token

        match obsolete with
        | SessionTerminationResult.Rejected(Refusal.OperationClockUnavailable _ | Refusal.OperationIdOutOfWindow _) ->
            ()
        | other -> failtestf "Expired request cannot start another cut: %A" other

        let! retained = store.Retrieve(newer, token) |> SessionResults.payload

        Expect.isSome
            retained
            "Uncertainty does not automatically repeat deletion against newer sessions"
    }

let private retirementCutoff (database: PostgresFixture.Database) =
    task {
        let actor = principal "retirement-cutoff"
        let services = core database.Fixture actor

        let store =
            SessionStore(database.RuntimeSource, database.Installation) :> ISessionStore

        let retirement () =
            task {
                match! Service.context services () token with
                | Outcome.Succeeded { TerminationScope = Some scope } -> return scope
                | other -> return failtestf "Expected read-only requester scope: %A" other
            }

        let! initial = retirement ()
        Expect.isNone initial.RetirementCutoff "Reading context establishes no frontier"

        let issued =
            DateTimeOffset.FromUnixTimeMilliseconds(initial.ObservedAt.ToUnixTimeMilliseconds())

        let at time =
            { ownRequest () with
                OperationId = Guid.CreateVersion7 time |> OperationId.create |> Option.get
            }

        let older = at (issued.AddMilliseconds -1.)
        let equal = at issued

        let settle request =
            Service.terminateOwnSessions services { request with SettleOnly = Some true } token

        let! olderCut = Service.terminateOwnSessions services older token
        let! equalCut = Service.terminateOwnSessions services equal token
        completed 0L true olderCut
        completed 0L true equalCut
        let! laterTicket = create store actor
        let! before = retirement ()

        // Supply synthetic provider time to the real SQL retirement transaction.
        // Subsequent service requests keep using the actual, earlier PostgreSQL clock.
        let forward = issued.AddMinutes 10.

        let applyForward commit =
            Transactions.write database.RuntimeSource Transactions.timeouts token (fun connection ->
                task {
                    let! _ = Transactions.head connection token
                    let! _ = SessionRows.lockOrder connection token
                    do! SessionRows.pruneResults connection forward token
                    return (), commit
                })

        let! transient = applyForward false
        Expect.equal transient (Ok()) "The synthetic forward transaction rolls back"
        let! afterRollback = retirement ()

        Expect.equal
            afterRollback.RetirementCutoff
            before.RetirementCutoff
            "A rolled-back cutoff is invisible"

        let! stillRecoverable = settle older
        completed 0L true stillRecoverable

        let! committed = applyForward true
        Expect.equal committed (Ok()) "The same cutoff commits"
        let! afterCommit = retirement ()

        Expect.isLessThan
            afterCommit.ObservedAt
            forward
            "The live provider clock remains behind the synthetic observation"

        Expect.equal
            afterCommit.RetirementCutoff
            (Some issued)
            "Read-only context exposes the committed cutoff"

        let! retired = settle older

        match retired with
        | SessionTerminationResult.Rejected(Refusal.OperationClockUnavailable(cutoff, _)) ->
            Expect.equal
                cutoff
                issued
                "The committed cutoff still refuses after the clock rolls back"
        | other -> failtestf "Expected durable expiry refusal: %A" other

        let! boundary = settle equal
        completed 0L true boundary

        let! entries =
            DatabaseSession.read database.RuntimeSource token (fun connection ->
                DatabaseSession.rows
                    connection
                    "SELECT request_id FROM claimcore.session_termination_results ORDER BY requested_at"
                    [||]
                    token)

        Expect.equal
            (entries |> List.map (DatabaseSession.value<Guid> 0))
            [ OperationId.value equal.OperationId ]
            "Pruning removes only results strictly before the frontier"

        let! confirmation =
            Service.terminateOwnSessions
                services
                { ownRequest () with
                    ConfirmRetirement = Some true
                    SettleOnly = Some true
                }
                token

        Expect.equal
            confirmation
            SessionTerminationResult.RetirementConfirmed
            "Explicit confirmation has its own successful outcome"

        let! final = retirement ()

        Expect.equal
            final.RetirementCutoff
            (Some issued)
            "Confirmation at the rolled-back instant cannot lower the frontier"

        let! retained = store.Retrieve(laterTicket, token) |> SessionResults.payload
        Expect.isSome retained "Retirement preserves later browser tickets"
    }

[<Tests>]
let tests =
    testList
        "targeted browser session termination"
        [
            testCaseAsync
                "canonical principal scopes and action-time owner permission"
                (run scopeAndPermission)
            testCaseAsync
                "real hosts serialize creation and revocation at the commit cut"
                (run creationCut)
            testCaseAsync
                "browser starts and callbacks complete while an unrelated write holds the head"
                (run heldLedgerHead)
            testCaseAsync
                "status-only and stale bearer proof preserve newly protected tickets"
                (run staleStatusOnly)
            testCaseAsync
                "real hosts retain admitted work and never resurrect deleted renewals"
                (run retrieveRenewAndAdmittedWork)
            testCaseAsync
                "committed retirement survives clock rollback and preserves cutoff equality"
                (run retirementCutoff)
            testCaseAsync
                "late failure rolls back and uncertain acknowledgement never repeats deletion"
                (run rollbackAndUnknown)
        ]
