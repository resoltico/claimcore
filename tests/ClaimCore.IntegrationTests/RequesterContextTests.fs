module ClaimCore.IntegrationTests.RequesterContextTests

open System
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Domain
open ClaimCore.Protocol
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected successful request administration: %A" other

let private principal kind subject =
    PrincipalKey.ofKind kind "https://issuer.example/" subject
    |> Result.defaultWith (string >> failtest)

let private rows (database: PostgresFixture.Database) statement =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()
        return! DatabaseSession.rows connection statement [||] token
    }

let private unregistered claims outcome =
    match outcome with
    | Outcome.Succeeded {
                            Requester = RequesterState.NotRegistered requester
                        } ->
        Expect.equal
            requester.Claims
            claims
            "Only independently admitted authentication hints are returned"
    | other -> failtestf "Expected NOT_REGISTERED requester: %A" other

let private lifecycle (database: PostgresFixture.Database) =
    task {
        let! owner = bootstrap database.Fixture
        let human = principal PrincipalKind.Human "requesting"
        let service = principal PrincipalKind.Service "requesting"

        let claims =
            RequesterClaims.admit
                (Some "Author hint")
                (Some "username")
                (Some "hint@example.invalid")

        let caller = core database.Fixture human |> Service.withClaims claims
        let! initial = Service.context caller () token
        unregistered claims initial
        let! serviceInitial = Service.context (core database.Fixture service) () token
        unregistered RequesterClaims.empty serviceInitial

        let! before =
            rows
                database
                "SELECT principal_kind,name,first_seen,last_seen FROM claimcore.access_request ORDER BY principal_kind"

        Expect.equal
            before.Length
            2
            "Human and service requests with the same issuer/subject remain distinct"

        let! refreshed = Service.context (core database.Fixture human) () token
        unregistered RequesterClaims.empty refreshed

        let! after =
            rows
                database
                "SELECT principal_kind,name,first_seen,last_seen FROM claimcore.access_request ORDER BY principal_kind"

        Expect.isNone
            (DatabaseSession.optional<string> 1 after.Head)
            "Missing refreshed hints clear older hints"

        Expect.equal after.Head[2] before.Head[2] "Refresh preserves first-seen time"

        let! registered =
            Service.execute
                owner
                (OperationInput.Authority(
                    Inputs.authorityRequest
                        (id 710)
                        human
                        (AuthorityAct.Register Inputs.ownerProfile)
                ))
                token

        match registered.Outcome with
        | ExecuteResult.Accepted _ -> ()
        | other -> failtestf "Registration failed: %A" other

        let! remaining = rows database "SELECT principal_kind FROM claimcore.access_request"

        Expect.equal
            (remaining |> List.map (DatabaseSession.value<string> 0))
            [ "SERVICE" ]
            "Registration deletes exactly the matching kind-aware request"

        let! noRoles = Service.context (core database.Fixture human) () token

        match noRoles with
        | Outcome.Succeeded {
                                Requester = RequesterState.NoRoles value
                            } ->
            Expect.equal
                value.Self.PrincipalKind
                PrincipalKind.Human
                "Registered identity is retained"
        | other -> failtestf "Expected NO_ROLES: %A" other

        let! denied =
            Service.actors
                (core database.Fixture human)
                {
                    Text = None
                    AfterCursor = None
                    Limit = 10
                }
                token

        Expect.equal
            denied
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Own requester state does not disclose any other endpoint"

        let! _ = verified database
        return ()
    }

let private bounded (database: PostgresFixture.Database) =
    task {
        do!
            sql
                database
                "INSERT INTO claimcore.access_request(principal_kind,issuer,subject,first_seen,last_seen) SELECT 'HUMAN','https://issuer.example/','expired-'||n,clock_timestamp()-INTERVAL '31 days',clock_timestamp()-INTERVAL '31 days' FROM generate_series(1,120)n"
                [||]

        let! _ =
            Service.context (core database.Fixture (principal PrincipalKind.Human "fresh")) () token

        let! aged =
            rows
                database
                "SELECT count(*) FROM claimcore.access_request WHERE last_seen<clock_timestamp()-INTERVAL '30 days'"

        Expect.equal
            (DatabaseSession.value<int64> 0 aged.Head)
            70L
            "An opportunistic expiry pass deletes exactly the bounded50 oldest rows"

        do!
            sql
                database
                "TRUNCATE claimcore.access_request; INSERT INTO claimcore.access_request(principal_kind,issuer,subject,first_seen,last_seen) SELECT 'HUMAN','https://issuer.example/','capacity-'||n,clock_timestamp()-INTERVAL '1 hour',clock_timestamp()-INTERVAL '1 hour' FROM generate_series(1,1000)n"
                [||]

        let callers =
            [ 1..12 ]
            |> List.map (fun n ->
                Service.context
                    (core database.Fixture (principal PrincipalKind.Human ($"concurrent-{n}")))
                    ()
                    token)

        let! results = Task.WhenAll callers
        results |> Array.iter (unregistered RequesterClaims.empty)
        let! capped = rows database "SELECT count(*) FROM claimcore.access_request"

        Expect.equal
            (DatabaseSession.value<int64> 0 capped.Head)
            1000L
            "Concurrent different principals cannot exceed the global request cap"
    }

let private lockRace (database: PostgresFixture.Database) =
    task {
        let! _ = bootstrap database.Fixture
        let target = principal PrincipalKind.Human "waiting-registration"

        let entered =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let release =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let original = database.Fixture.Store

        let held =
            { new IStore with
                member _.Context query = original.Context query
                member _.Read operation = original.Read operation

                member _.Write operation =
                    original.Write
                        { operation with
                            Work =
                                fun tx ->
                                    task {
                                        let! result = operation.Work tx
                                        entered.SetResult()
                                        do! release.Task
                                        return result
                                    }
                        }
            }

        let fixture = { database.Fixture with Store = held }

        let registering =
            Service.execute
                (core fixture fixture.Owner)
                (OperationInput.Authority(
                    Inputs.authorityRequest
                        (id 711)
                        target
                        (AuthorityAct.Register Inputs.ownerProfile)
                ))
                token

        do! entered.Task.WaitAsync(TimeSpan.FromSeconds 10.)
        let waiting = Service.context (core database.Fixture target) () token

        try
            let mutable blocked = false
            let deadline = DateTimeOffset.UtcNow.AddSeconds 5.

            while not blocked && DateTimeOffset.UtcNow < deadline do
                let! locks =
                    rows
                        database
                        "SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event='advisory'"

                blocked <- DatabaseSession.value<int64> 0 locks.Head > 0L

                if not blocked then
                    do! Task.Delay 10

            Expect.isTrue
                blocked
                "The real context query waits on the principal lock while registration is uncommitted"
        finally
            release.TrySetResult() |> ignore

        let! accepted = registering

        match accepted.Outcome with
        | ExecuteResult.Accepted _ -> ()
        | other -> failtestf "Registration: %A" other

        let! context = waiting.WaitAsync(TimeSpan.FromSeconds 5.)

        match context with
        | Outcome.Succeeded { Requester = RequesterState.NoRoles _ } -> ()
        | other ->
            failtestf "Post-lock authority snapshot must see committed registration: %A" other

        let! retained =
            rows
                database
                "SELECT count(*) FROM claimcore.access_request WHERE subject='waiting-registration'"

        Expect.equal
            (DatabaseSession.value<int64> 0 retained.Head)
            0L
            "No stale snapshot can reinsert the deleted registration request"
    }

let private administration (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! owner = bootstrap fixture
        let human = principal PrincipalKind.Human "admin-target"
        let service = principal PrincipalKind.Service "admin-target"
        let audit = principal PrincipalKind.Human "request-auditor"

        let! registered =
            Service.execute
                owner
                (OperationInput.Authority(
                    Inputs.authorityRequest
                        (id 720)
                        audit
                        (AuthorityAct.Register Inputs.ownerProfile)
                ))
                token

        acceptedAuthority registered

        let! granted =
            grant
                owner
                (id 721)
                audit
                {
                    Role = Role.Auditor
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority granted

        for target in [ human; service ] do
            let! _ = Service.context (core fixture target) () token
            ()

        do!
            sql
                database
                "INSERT INTO claimcore.access_request(principal_kind,issuer,subject,first_seen,last_seen) VALUES ('HUMAN','https://issuer.example/','expired-admin',clock_timestamp()-INTERVAL '31 days',clock_timestamp()-INTERVAL '31 days')"
                [||]

        let! before = verified database
        let! first = Service.accessRequests owner { AfterCursor = None; Limit = 1 } token
        let first = read first
        Expect.equal first.Items.Length 1 "Owner page uses the requested limit"

        let cursor =
            first.NextCursor
            |> Option.defaultWith (fun () -> failtest "Two live requests need a continuation")

        let! second =
            Service.accessRequests owner { AfterCursor = Some cursor; Limit = 1 } token

        let second = read second
        Expect.equal second.Items.Length 1 "Cursor reaches the second identity"

        Expect.equal
            (first.Items @ second.Items |> List.map _.Principal |> Set.ofList)
            (set[human
                 service])
            "Kind-aware cursor traversal hides expired requests and visits both identities"

        Expect.isTrue
            (first.Items.Head.LastSeen >= second.Items.Head.LastSeen)
            "Requests are newest first"

        Expect.isNone second.NextCursor "The live traversal ends"

        let! foreign =
            Service.accessRequests
                (Service.create
                    BuildIdentity.current.Version
                    (id 999)
                    fixture.Store
                    fixture.Calendar
                    fixture.Owner)
                { AfterCursor = Some cursor; Limit = 1 }
                token

        Expect.equal
            foreign
            (Outcome.Rejected Refusal.InvalidCursor)
            "Cursor binds the installation"

        for target in [ audit; human; principal PrincipalKind.Human "unknown-manager" ] do
            let actor = core fixture target

            let! denied =
                Service.accessRequests
                    actor
                    {
                        AfterCursor = Some "malformed"
                        Limit = 0
                    }
                    token

            Expect.equal
                denied
                (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
                "Only MANAGE_AUTHORITY may inspect requests, including diagnostics"

            let! denied = Service.dismissAccessRequest actor (PrincipalKey.toInput human) token

            Expect.equal
                denied
                (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
                "READ_AUTHORITY and an authenticated identity do not permit dismissal"

        let! dismissed =
            Service.dismissAccessRequest owner (PrincipalKey.toInput human) token

        Expect.equal dismissed (Outcome.Succeeded()) "Owner dismisses one key"
        let! listed = Service.accessRequests owner { AfterCursor = None; Limit = 50 } token

        Expect.equal
            ((read listed).Items |> List.map _.Principal)
            [ service ]
            "Dismiss does not remove the other principal kind"

        let! replay = Service.dismissAccessRequest owner (PrincipalKey.toInput human) token
        Expect.equal replay (Outcome.Succeeded()) "Repeated dismissal is harmless"
        let! after = verified database
        Expect.equal after before "Request listing and dismissal never append a ledger entry"
        let! _ = Service.context (core fixture human) () token

        let! revisited =
            Service.accessRequests owner { AfterCursor = None; Limit = 50 } token

        Expect.equal
            (read revisited).Items.Length
            2
            "A dismissed caller may request access on a later visit"
    }

[<Tests>]
let tests =
    testList
        "requester context cache"
        [
            testCaseAsync
                "owner-only paging and dismissal retain privacy and no ledger effects"
                (run administration)
            testCaseAsync
                "own states refresh hints and preserve kind-aware identities"
                (run lifecycle)
            testCaseAsync "expiry is bounded and concurrent capacity stays capped" (run bounded)
            testCaseAsync
                "authority snapshot follows principal lock after registration commits"
                (run lockRace)
        ]
