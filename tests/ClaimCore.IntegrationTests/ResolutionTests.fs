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
open ClaimCore.Application.Contracts
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
    (ClaimCore.TestSupport.StoreFixtures.resolve actor ({ OperationId = id }) (token))

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
            ClaimCore.TestSupport.Inputs.caseOperation (
                request 101 "FENCED" 0L (AuthoredCaseAct.Open registration)
            )

        let executing = (Service.execute actor (input) (token))
        do! waiters database 2L
        do! transaction.CommitAsync()
        let! resolved = resolving

        Expect.equal
            resolved
            (Outcome.Succeeded Resolved.NotAccepted)
            "A committed fence is a final answer"

        let! executed = executing

        Expect.equal
            executed.Outcome
            (ExecuteResult.Rejected Refusal.OperationResolved)
            "Delayed execute must never append"

        let! repeated = resolve actor (id 101)
        Expect.equal repeated resolved "Resolution is idempotent"
        use! inspected = database.OwnerSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows
                inspected
                "SELECT count(*) FROM claimcore.ledger WHERE subject_kind='CONTROL'"
                [||]
                token

        Expect.equal
            (DatabaseSession.value<int64> 0 rows.Head)
            1L
            "Repeated resolution appends exactly one control"

        let! observed =
            ClaimCore.TestSupport.StoreFixtures.observe actor { OperationId = id 101 } token

        Expect.equal
            observed
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "A control is never an acceptance receipt"

        let! verified = Verification.verify database.OwnerSource None token

        Expect.equal
            (VerificationSupport.position verified).Sequence
            (CaseFixtures.sequence (4L))
            "Bootstrap, grants and exactly one final-nonacceptance control exist"

        do!
            VerificationSupport.sql
                database
                "UPDATE claimcore.operation_fences SET fenced_at=fenced_at+interval '1 second'"
                [||]

        do! VerificationSupport.finding VerificationFinding.FenceProjection database

        do!
            VerificationSupport.sql
                database
                "UPDATE claimcore.operation_fences f SET fenced_at=l.recorded_at FROM claimcore.ledger l WHERE l.subject_kind='CONTROL' AND l.actor_id=f.actor_id AND l.operation_id=f.operation_id"
                [||]

        let! repaired = VerificationSupport.verified database

        Expect.equal
            repaired
            (VerificationSupport.position verified)
            "Restoring the projection reestablishes the same control checkpoint"

    }

let private revokeReadRoles (actor: ServiceContext) principal =
    task {
        for operation, role in [ id 111, Role.CaseHandler; id 114, Role.PaymentRecorder ] do
            let! reply =
                grant
                    actor
                    operation
                    principal
                    {
                        Role = role
                        Scope = GrantTarget.Installation
                    }
                    false

            acceptedAuthority reply
    }

let private receipts (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture

        let! created =
            (Service.execute
                actor
                (ClaimCore.TestSupport.Inputs.caseOperation (
                    request 110 "RESOLVED" 0L (AuthoredCaseAct.Open registration)
                ))
                (token))

        let receipt = accepted created
        let! resolved = resolve actor (id 110)

        Expect.equal
            resolved
            ((Resolved.Accepted >> Outcome.Succeeded) receipt)
            "Committed operation returns its receipt"

        do! revokeReadRoles actor database.Fixture.Owner

        let! withheld = resolve actor (id 110)

        match withheld with
        | Outcome.Succeeded(Resolved.Accepted receipt) ->
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
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority steward

        let! erased =
            (Service.execute
                actor
                (ClaimCore.TestSupport.Inputs.caseOperation (
                    request 113 "RESOLVED" 1L (AuthoredCaseAct.Bare BareAct.Erase)
                ))
                (token))

        accepted erased |> ignore
        let! resolvedErased = resolve actor (id 110)

        match resolvedErased with
        | Outcome.Succeeded(Resolved.Accepted receipt) ->
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
            (Service.execute
                actor
                (OperationInput.Authority(authorityRequest (id 120) principal AuthorityAct.Register))
                (token))

        acceptedAuthority registered
        let other = core database.Fixture principal

        let input =
            ClaimCore.TestSupport.Inputs.caseOperation (
                request 121 "FOREIGN" 0L (AuthoredCaseAct.Open registration)
            )

        let! created = (Service.execute actor (input) (token))
        let receipt = accepted created
        let! hidden = resolve other (id 121)

        Expect.equal
            hidden
            (Outcome.Succeeded Resolved.NotAccepted)
            "Foreign acceptance discloses nothing"

        let! replayed = (Service.execute actor (input) (token))

        Expect.equal
            replayed.Outcome
            (ExecuteResult.AlreadyAccepted receipt)
            "Foreign fence cannot disturb author replay"

        let! fence = resolve other (id 122)

        Expect.equal
            fence
            (Outcome.Succeeded Resolved.NotAccepted)
            "Another actor's future ID is independently fenced"

        let! acceptedOtherId =
            (Service.execute
                actor
                (ClaimCore.TestSupport.Inputs.caseOperation (
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
            (Service.execute
                actor
                (OperationInput.Authority(authorityRequest (id 120) principal AuthorityAct.Register))
                (token))

        acceptedAuthority registered
        let other = core database.Fixture principal

        let! disabled =
            (Service.execute
                actor
                (OperationInput.Authority(
                    authorityRequest (id 123) principal (AuthorityAct.Disable)
                ))
                (token))

        acceptedAuthority disabled
        let! refused = resolve other (id 121)

        Expect.equal
            refused
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Disabled actor cannot resolve"

        let! nil = resolve actor Guid.Empty

        Expect.equal
            nil
            (Outcome.Rejected(
                Refusal.invalidInput (InputTarget.OperationId, Violation.OperationIdRequired)
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

        Expect.equal
            resolved
            (Outcome.Succeeded Resolved.NotAccepted)
            "Authority identity is fenced"

        let! rejected =
            (Service.execute
                actor
                (OperationInput.Authority(authorityRequest (id 130) target AuthorityAct.Register))
                (token))

        Expect.equal
            rejected.Outcome
            (ExecuteResult.Rejected Refusal.OperationResolved)
            "Authority execution also checks the fence"

        let! created =
            (Service.execute
                actor
                (OperationInput.Authority(authorityRequest (id 131) target AuthorityAct.Register))
                (token))

        let receipt = accepted created
        let! accepted = resolve actor (id 131)

        Expect.equal
            accepted
            ((Resolved.Accepted >> Outcome.Succeeded) receipt)
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

let private passiveWhileHeadHeld (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture
        use! connection = database.OwnerSource.OpenConnectionAsync()
        use! transaction = connection.BeginTransactionAsync()
        let! _ = Transactions.head connection token
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 3.)

        let! status =
            Service.status actor { OperationLookup.OperationId = id 111 } deadline.Token

        Expect.equal
            status.Outcome
            (Outcome.Succeeded OperationStatus.Unresolved)
            "A passive status read completes while the global write head is held"

        let settling = resolve actor (id 111)
        do! waiters database 1L
        do! transaction.RollbackAsync()
        let! settled = settling

        Expect.equal
            settled
            (Outcome.Succeeded Resolved.NotAccepted)
            "Explicit settlement still waits for ordered write admission"
    }

let private fenceEvidenceCorruption (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture
        let! settled = resolve actor (id 112)

        Expect.equal
            settled
            (Outcome.Succeeded Resolved.NotAccepted)
            "The control is genuinely committed before projection corruption"

        use! connection = database.OwnerSource.OpenConnectionAsync()
        let parameters = [| box (id 1); box (id 112) |]

        let! removed =
            DatabaseSession.execute
                connection
                "DELETE FROM claimcore.operation_fences WHERE actor_id=$1 AND operation_id=$2"
                parameters
                token

        Expect.equal removed 1 "The intended projection was actually removed"

        let assertIntegrity label =
            task {
                let! status = Service.status actor { OperationLookup.OperationId = id 112 } token

                Expect.equal
                    status.Outcome
                    (Outcome.Failed CoreFault.StoreIntegrityError)
                    (label + ": passive status")

                let! resolved = resolve actor (id 112)

                Expect.equal
                    resolved
                    (Outcome.Failed CoreFault.StoreIntegrityError)
                    (label + ": final settlement")

                let! executed =
                    Service.execute
                        actor
                        (Inputs.caseOperation (
                            request 112 "RETAINED-CONTROL" 0L (AuthoredCaseAct.Open registration)
                        ))
                        token

                Expect.equal
                    executed.Outcome
                    (ExecuteResult.Failed CoreFault.StoreIntegrityError)
                    (label + ": execute cannot contradict retained history")
            }

        do! assertIntegrity "Missing projection"

        let! inserted =
            DatabaseSession.execute
                connection
                "INSERT INTO claimcore.operation_fences(actor_id,operation_id,fenced_at) SELECT actor_id,operation_id,recorded_at + interval '1 microsecond' FROM claimcore.ledger WHERE actor_id=$1 AND operation_id=$2 AND subject_kind='CONTROL'"
                parameters
                token

        Expect.equal inserted 1 "A real projection timestamp disagreement was installed"
        do! assertIntegrity "Corrupted projection time"

        let! aligned =
            DatabaseSession.execute
                connection
                "UPDATE claimcore.operation_fences f SET fenced_at=l.recorded_at FROM claimcore.ledger l WHERE f.actor_id=$1 AND f.operation_id=$2 AND l.actor_id=f.actor_id AND l.operation_id=f.operation_id AND l.subject_kind='CONTROL'"
                parameters
                token

        Expect.equal aligned 1 "The projection agrees again before testing control-node corruption"

        do!
            VerificationSupport.guardedSql
                database
                "UPDATE claimcore.ledger SET previous_hash=set_byte(previous_hash,0,get_byte(previous_hash,0)#1) WHERE actor_id=$1 AND operation_id=$2 AND subject_kind='CONTROL'"
                parameters

        let key =
            {
                FenceKey.ActorId = CaseFixtures.actorId (id 1)
                OperationId = CaseFixtures.operationId (id 112)
            }

        let! evidence =
            database.Fixture.Store.Read
                {
                    Work = (fun tx -> tx.NonAcceptance key)
                    CancellationToken = token
                }

        match evidence with
        | Ok { Control = Some control } ->
            Expect.isOk
                (LedgerEntries.validate database.Fixture.InstallationId control.Entry)
                "Payload/static identity validation alone still passes: the node hash must detect this corruption"
        | _ -> failtest "The retained corruption control must remain structurally readable"

        do! assertIntegrity "Corrupted retained node hash"

        let! rows =
            DatabaseSession.rows
                connection
                "SELECT count(*) FROM claimcore.ledger WHERE actor_id=$1 AND operation_id=$2 AND subject_kind='CONTROL'"
                parameters
                token

        Expect.equal
            (DatabaseSession.value<int64> 0 rows.Head)
            1L
            "Faults never append a replacement control"
    }

[<Tests>]
let tests =
    testList
        "operation resolution"
        [
            testCaseAsync
                "passive status avoids the write head while explicit settlement remains ordered"
                (run passiveWhileHeadHeld)
            testCaseAsync
                "missing and corrupted fence projections cannot bypass retained controls"
                (run fenceEvidenceCorruption)
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
