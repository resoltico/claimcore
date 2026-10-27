module ClaimCore.IntegrationTests.GrantPaginationTests

open ClaimCore.TestSupport

open ClaimCore.Protocol

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private page =
    function
    | ReadReply.Succeeded p -> p
    | _ -> failtest "Expected grant page"

let private seedCases (admin: ServiceContext) target =
    task {
        for n in 1..52 do
            let reference = "GRANT-" + string n

            let! opened =
                (Service.execute
                    admin
                    (OperationInput.Case(
                        request (300 + n) reference 0L (AuthoredCaseAct.Open registration)
                    ))
                    (token))

            accepted opened |> ignore


            let! added =
                grant
                    admin
                    (id (400 + n))
                    target
                    Role.CaseReader
                    (GrantTarget.CaseReference reference)
                    true

            acceptedAuthority added


    }

let private eraseCase (database: PostgresFixture.Database) (admin: ServiceContext) =
    task {
        let! steward =
            grant
                admin
                (id 604)
                database.Fixture.Owner
                Role.DataSteward
                GrantTarget.Installation
                true

        acceptedAuthority steward

        let erase =
            {
                OperationId = id 605
                CaseReference = "GRANT-1"
                ExpectedRevision = 1L
                Act = (AuthoredCaseAct.Erase)
            }

        let! erased = (Service.execute admin (OperationInput.Case erase) (token))

        accepted erased |> ignore
    }

let private prepare (database: PostgresFixture.Database) =
    task {
        let! admin = caseWorker database.Fixture

        let target =
            PrincipalKey.service "https://issuer.example/" "grant-target"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            (Service.execute
                admin
                (OperationInput.Authority(authorityRequest (id 200) target AuthorityAct.Register))
                (token))

        acceptedAuthority registered

        do! seedCases admin target

        for n, role in [ 600, Role.CaseReader; 601, Role.CaseHandler; 602, Role.PaymentRecorder ] do
            let! added = grant admin (id n) target role GrantTarget.Installation true
            acceptedAuthority added


        let! actors = (Service.actors admin ({ AfterCursor = None; Limit = 50 }) (token))

        let actorId =
            (page actors).Items |> List.find (fun a -> a.Principal = target) |> _.ActorId

        let! disabled =
            (Service.execute
                admin
                (OperationInput.Authority(authorityRequest (id 603) target (AuthorityAct.Disable)))
                (token))

        acceptedAuthority disabled

        do! eraseCase database admin
        return admin, actorId
    }

let private indexCompatible (database: PostgresFixture.Database) targetId =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()
        use! transaction = connection.BeginTransactionAsync()
        let! _ = DatabaseSession.execute connection "SET LOCAL enable_seqscan=off" [||] token

        let! rows =
            DatabaseSession.rows
                connection
                ("EXPLAIN " + TransactionPages.grantPageSql)
                [| box targetId; box "CASE_READER"; box (id 1); box 51 |]
                token

        let plan = rows |> List.map (DatabaseSession.value<string> 0) |> String.concat "\n"

        Expect.stringContains
            plan
            "grants_unique"
            "Existing ordered unique index can serve the bounded keyset"

        Expect.stringContains plan "Limit" "LIMIT+1 is applied in the database"
        do! transaction.RollbackAsync()
    }

let private changedTarget (admin: ServiceContext) targetId cursor =
    task {
        let target =
            PrincipalKey.service "https://issuer.example/" "grant-target"
            |> Result.defaultWith (string >> failtest)

        let! removed =
            grant admin (id 610) target Role.PaymentRecorder GrantTarget.Installation false

        acceptedAuthority removed

        let! current =
            (Service.grants
                admin
                ({
                    ActorId = targetId
                    AfterCursor = cursor
                    Limit = 50
                })
                (token))

        let current = page current

        Expect.isFalse
            (current.Items |> List.exists (fun g -> g.Role = Role.PaymentRecorder))
            "Later page sees current target grants rather than frozen multi-request state"
    }

let private callerRevocation (database: PostgresFixture.Database) =
    task {
        let! admin, targetId = prepare database

        let target =
            PrincipalKey.service "https://issuer.example/" "reviewer"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            (Service.execute
                admin
                (OperationInput.Authority(authorityRequest (id 620) target AuthorityAct.Register))
                (token))

        acceptedAuthority registered

        let! authorized =
            grant admin (id 621) target Role.Auditor GrantTarget.Installation true

        acceptedAuthority authorized
        let actor = core database.Fixture target

        let query =
            {
                ActorId = targetId
                AfterCursor = None
                Limit = 1
            }

        let! first = (Service.grants actor (query) (token))
        let cursor = (page first).NextCursor

        let! revoked =
            grant admin (id 622) target Role.Auditor GrantTarget.Installation false

        acceptedAuthority revoked

        let! denied = (Service.grants actor ({ query with AfterCursor = cursor }) (token))

        Expect.equal
            denied
            (ReadReply.Rejected Refusal.ResourceUnavailable)
            "Current caller authority precedes token acceptance"
    }

let private cursorIsolation (admin: ServiceContext) (query: GrantsRequest) cursor =
    task {
        let! crossTarget =
            (Service.grants
                admin
                ({ query with
                    ActorId = id 1
                    AfterCursor = cursor
                })
                (token))

        Expect.equal crossTarget (ReadReply.Rejected Refusal.InvalidCursor) "Target is bound"

        let! crossEndpoint =
            (Service.actors admin ({ AfterCursor = cursor; Limit = 50 }) (token))

        Expect.equal crossEndpoint (ReadReply.Rejected Refusal.InvalidCursor) "Endpoint is bound"

        let! missing = (Service.grants admin ({ query with ActorId = id 999 }) (token))

        Expect.equal
            missing
            (ReadReply.Rejected Refusal.ResourceUnavailable)
            "Absent target refuses generically"

    }

let private traversal (database: PostgresFixture.Database) =
    task {
        let! admin, targetId = prepare database

        let query =
            {
                ActorId = targetId
                AfterCursor = None
                Limit = 50
            }

        do! indexCompatible database targetId
        let! first = (Service.grants admin (query) (token))
        let first = page first
        Expect.equal first.Items.Length 50 "Target grants do not expand actor rows"
        Expect.isFalse first.Actor.Enabled "Disabled target header is reviewable"

        let! last =
            (Service.grants
                admin
                ({ query with
                    AfterCursor = first.NextCursor
                })
                (token))

        let last = page last
        let all = first.Items @ last.Items
        Expect.equal all.Length 55 "All case and installation grants traverse"
        Expect.equal (Set.ofList all).Count 55 "No duplicated or omitted grant"

        Expect.contains
            all
            {
                Role = Role.CaseReader
                Scope = GrantTarget.CaseReference "GRANT-1"
            }
            "Erased scope reference remains reviewable"

        Expect.isNone last.NextCursor "End follows last included grant"

        do! cursorIsolation admin query first.NextCursor
        do! changedTarget admin targetId first.NextCursor
        let! _ = verified database
        return ()
    }

let private boundaries (database: PostgresFixture.Database) =
    task {
        let! admin, targetId = prepare database
        let mutable cursor = None
        let mutable all = []
        let mutable complete = false

        while not complete do
            let! result =
                (Service.grants
                    admin
                    ({
                        ActorId = targetId
                        AfterCursor = cursor
                        Limit = 1
                    })
                    (token))

            let current = page result
            all <- all @ current.Items
            cursor <- current.NextCursor
            complete <- cursor.IsNone

        Expect.equal all.Length 55 "All role/null/case keyset boundaries traverse with LIMIT+1"
        let reader = all |> List.filter (fun g -> g.Role = Role.CaseReader)

        Expect.equal
            (List.last reader).Scope
            GrantTarget.Installation
            "NULL installation comes last within role"

        Expect.equal (Set.ofList all).Count 55 "No duplicate across nullable position boundaries"
    }

let private changeTarget (admin: ServiceContext) =
    task {
        let target =
            PrincipalKey.service "https://issuer.example/" "grant-target"
            |> Result.defaultWith (string >> failtest)

        let! enabled =
            (Service.execute
                admin
                (OperationInput.Authority(authorityRequest (id 630) target (AuthorityAct.Enable)))
                (token))

        acceptedAuthority enabled

        let! removed =
            grant admin (id 631) target Role.CaseHandler GrantTarget.Installation false

        acceptedAuthority removed
    }

let private concurrentSnapshot (database: PostgresFixture.Database) =
    task {
        let! admin, targetId = prepare database

        let entered =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        use release = new ManualResetEventSlim(false)

        let store =
            PaginationTests.PausedPageStore(database.Fixture.Store, entered, release)

        let actor =
            Service.create
                BuildIdentity.current.Version
                database.Fixture.InstallationId
                (store :> IStore)
                database.Fixture.Calendar

                database.Fixture.Owner

        let query =
            {
                ActorId = targetId
                AfterCursor = None
                Limit = 50
            }

        let reading =
            Task.Run<ReadReply<AuthorityGrantPage>>(
                Func<Task<ReadReply<AuthorityGrantPage>>>(fun () ->
                    (Service.grants actor (query) (token)))
            )

        try
            do! entered.Task.WaitAsync(TimeSpan.FromSeconds 3.0)

            do! changeTarget admin
        finally
            release.Set()

        let! prior = reading
        let prior = page prior
        Expect.isFalse prior.Actor.Enabled "Target header comes from pre-change snapshot"

        Expect.contains
            prior.Items
            {
                Role = Role.CaseHandler
                Scope = GrantTarget.Installation
            }
            "Rows share the target-header snapshot"

        let! fresh = (Service.grants admin (query) (token))
        let fresh = page fresh
        Expect.isTrue fresh.Actor.Enabled "Fresh page observes enabled target"

        Expect.isFalse
            (fresh.Items |> List.exists (fun g -> g.Role = Role.CaseHandler))
            "Fresh page observes removed grant"
    }

[<Tests>]
let tests =
    testList
        "bounded grant review"
        [
            testCaseAsync
                "target header authorization and bounded rows share a paused RR snapshot"
                (run concurrentSnapshot)
            testCaseAsync "revoked caller cannot continue target review" (run callerRevocation)
            testCaseAsync
                "real target traversal exceeds fifty and retains erased and disabled scopes"
                (run traversal)
            testCaseAsync
                "role and nullable case positions traverse without omission"
                (run boundaries)
        ]
