module ClaimCore.IntegrationTests.ResolutionTests

open ClaimCore.TestSupport

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None

let private run action =
    async {
        let! database = PostgresFixture.create () |> Async.AwaitTask

        try
            do! action database |> Async.AwaitTask
        finally
            database.Fixture.Dispose()
    }

let private resolve (actor: ServiceContext) id =
    (Core.resolve actor ({ OperationId = id }) (token))

let private waiters (database: PostgresFixture.Database) count =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()
        let watch = Stopwatch.StartNew()
        let mutable found = 0L

        while found < count && watch.Elapsed < TimeSpan.FromSeconds 3. do
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE 'SELECT sequence,entry_hash,observed_at FROM claimcore.lock_ledger_head%'"
                    [||]
                    token

            found <- DatabaseSession.value<int64> 0 rows.Head

            if found < count then
                do! Task.Delay 10

        Expect.equal found count "Owned writers reached the head lock in the intended order"
    }

let private delayedExecute (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture
        use! connection = database.OwnerSource.OpenConnectionAsync()
        use! transaction = connection.BeginTransactionAsync()
        let! _ = Transactions.head connection token
        // Queue resolve first so lock acquisition order is deterministic, while execute also waits.
        let resolving = resolve actor (id 101)
        do! waiters database 1L

        let input =
            OperationInput.Case(request 101 "FENCED" 0L (AuthoredCaseAct.Open registration))

        let executing = (Core.execute actor (input) (token))
        do! waiters database 2L
        do! transaction.CommitAsync()
        let! resolved = resolving
        Expect.equal resolved ResolveReply.NotAccepted "A committed fence is a final answer"
        let! executed = executing

        Expect.equal
            executed.Outcome
            (ExecuteResult.Rejected Refusal.OperationResolved)
            "Delayed execute must never append"

        let! repeated = resolve actor (id 101)
        Expect.equal repeated resolved "Resolution is idempotent"
        let! verified = Verification.verify database.OwnerSource None token

        Expect.equal
            (VerificationSupport.position verified).Sequence
            3L
            "Only bootstrap and grants exist"
    }

let private revokeReadRoles (actor: ServiceContext) principal =
    task {
        for operation, role in [ id 111, Role.CaseHandler; id 114, Role.PaymentRecorder ] do
            let! reply = grant actor operation principal role GrantTarget.Installation false
            acceptedAuthority reply
    }

let private receipts (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture

        let! created =
            (Core.execute
                actor
                (OperationInput.Case(request 110 "RESOLVED" 0L (AuthoredCaseAct.Open registration)))
                (token))

        let receipt = accepted created
        let! resolved = resolve actor (id 110)

        Expect.equal
            resolved
            (ResolveReply.Accepted receipt)
            "Committed operation returns its receipt"

        do! revokeReadRoles actor database.Fixture.Owner

        let! withheld = resolve actor (id 110)

        match withheld with
        | ResolveReply.Accepted receipt ->
            Expect.equal
                (caseReceipt receipt).Snapshot
                SnapshotDisclosure.Withheld
                "Author without read access still learns acceptance"
        | _ -> failtest "Author must receive a receipt"

        let! steward =
            grant
                actor
                (id 112)
                database.Fixture.Owner
                Role.DataSteward
                GrantTarget.Installation
                true

        acceptedAuthority steward

        let! erased =
            (Core.execute
                actor
                (OperationInput.Case(request 113 "RESOLVED" 1L AuthoredCaseAct.Erase))
                (token))

        accepted erased |> ignore
        let! resolvedErased = resolve actor (id 110)

        match resolvedErased with
        | ResolveReply.Accepted receipt ->
            Expect.equal
                (caseReceipt receipt).Snapshot
                SnapshotDisclosure.Erased
                "Erasure remains visible without payload"
        | _ -> failtest "Erased author receipt must remain available"
    }

let private foreign (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture

        let principal =
            PrincipalKey.human "https://issuer.example/" "another"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            (Core.execute
                actor
                (OperationInput.Authority(authorityRequest (id 120) principal AuthorityAct.Register))
                (token))

        acceptedAuthority registered
        let other = core database.Fixture principal

        let input =
            OperationInput.Case(request 121 "FOREIGN" 0L (AuthoredCaseAct.Open registration))

        let! created = (Core.execute actor (input) (token))
        let receipt = accepted created
        let! hidden = resolve other (id 121)
        Expect.equal hidden ResolveReply.NotAccepted "Foreign acceptance discloses nothing"
        let! replayed = (Core.execute actor (input) (token))

        Expect.equal
            replayed.Outcome
            (ExecuteResult.AlreadyAccepted receipt)
            "Foreign fence cannot disturb author replay"

        let! fence = resolve other (id 122)

        Expect.equal
            fence
            ResolveReply.NotAccepted
            "Another actor's future ID is independently fenced"

        let! acceptedOtherId =
            (Core.execute
                actor
                (OperationInput.Case(
                    request 122 "OTHER-FENCE" 0L (AuthoredCaseAct.Open registration)
                ))
                (token))

        accepted acceptedOtherId |> ignore

    }

let private actorAdmission (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture

        let principal =
            PrincipalKey.human "https://issuer.example/" "disabled"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            (Core.execute
                actor
                (OperationInput.Authority(authorityRequest (id 120) principal AuthorityAct.Register))
                (token))

        acceptedAuthority registered
        let other = core database.Fixture principal

        let! disabled =
            (Core.execute
                actor
                (OperationInput.Authority(
                    authorityRequest (id 123) principal (AuthorityAct.Disable)
                ))
                (token))

        acceptedAuthority disabled
        let! refused = resolve other (id 121)

        Expect.equal
            refused
            (ResolveReply.Rejected Refusal.ResourceUnavailable)
            "Disabled actor cannot resolve"

        let! nil = resolve actor Guid.Empty

        Expect.equal
            nil
            (ResolveReply.Rejected(
                Refusal.InvalidInput(InputTarget.OperationId, Violation.OperationIdRequired)
            ))
            "Nil identity is invalid"
    }

let private authorityFence (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture

        let target =
            PrincipalKey.human "https://issuer.example/" "fenced-authority"
            |> Result.defaultWith (string >> failtest)

        let! resolved = resolve actor (id 130)
        Expect.equal resolved ResolveReply.NotAccepted "Authority identity is fenced"

        let! rejected =
            (Core.execute
                actor
                (OperationInput.Authority(authorityRequest (id 130) target AuthorityAct.Register))
                (token))

        Expect.equal
            rejected.Outcome
            (ExecuteResult.Rejected Refusal.OperationResolved)
            "Authority execution also checks the fence"

        let! created =
            (Core.execute
                actor
                (OperationInput.Authority(authorityRequest (id 131) target AuthorityAct.Register))
                (token))

        let receipt = accepted created
        let! accepted = resolve actor (id 131)

        Expect.equal
            accepted
            (ResolveReply.Accepted receipt)
            "Accepted authority operation resolves to its receipt"
    }

let private privileges (database: PostgresFixture.Database) =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows
                connection
                "SELECT to_regclass('claimcore.recovery_requests') IS NULL, has_table_privilege('claimcore_runtime','claimcore.operation_fences','SELECT,INSERT'), has_table_privilege('claimcore_runtime','claimcore.operation_fences','UPDATE'), has_table_privilege('claimcore_runtime','claimcore.operation_fences','DELETE')"
                [||]
                token

        Expect.equal
            (DatabaseSession.value<bool> 0 rows.Head,
             DatabaseSession.value<bool> 1 rows.Head,
             DatabaseSession.value<bool> 2 rows.Head,
             DatabaseSession.value<bool> 3 rows.Head)
            (true, true, false, false)
            "Operational fences are append only and the body store is absent"
    }

[<Tests>]
let tests =
    testList
        "operation resolution"
        [
            testCaseAsync
                "NOT_ACCEPTED fences a delayed execute under the head lock"
                (run delayedExecute)
            testCaseAsync "author receipts follow current disclosure and erasure" (run receipts)
            testCaseAsync "actor-scoped fences preserve foreign replay" (run foreign)
            testCaseAsync "disabled actors and nil identities cannot resolve" (run actorAdmission)
            testCaseAsync
                "authority execution checks fences and retains accepted receipts"
                (run authorityFence)
            testCaseAsync "runtime can only select and insert fences" (run privileges)
        ]
