module internal ClaimCore.IntegrationTests.GrowthControls

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

let private current (database: PostgresFixture.Database) reference =
    task {
        let! read =
            inspectCase database.Fixture (database.Fixture.Owner) (None) (reference) (token)

        match read with
        | Ok { Case = Some record } -> return record.Snapshot
        | _ -> return failtest "Control case is absent"
    }

let private waiters (database: PostgresFixture.Database) expected =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()
        let watch = Stopwatch.StartNew()
        let mutable count = 0L

        while count < expected && watch.Elapsed < TimeSpan.FromSeconds 3. do
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE 'SELECT sequence,entry_hash,observed_at FROM claimcore.lock_ledger_head%'"
                    [||]
                    token

            count <- DatabaseSession.value<int64> 0 rows.Head

            if count < expected then
                do! Task.Delay 10

        Expect.isGreaterThanOrEqual
            count
            expected
            "The intended acceptance transactions are waiting for the head"
    }

let private sameCase database (actor: ServiceContext) =
    task {
        let! snapshot = current database "synthetic-growth-0"

        let act =
            match snapshot.State with
            | (CaseState.Active(fields) | CaseState.Voided(fields)) when
                fields.Status = CaseStatus.Closed
                ->
                (AuthoredCaseAct.Bare BareAct.Reopen)
            | _ -> (AuthoredCaseAct.Bare BareAct.Close)

        let! replies =
            Task.WhenAll
                [|
                    for index in 0..1 ->
                        (Service.execute
                            actor
                            (ClaimCore.TestSupport.Inputs.caseOperation (
                                request
                                    (710000 + index)
                                    (CaseReference.format snapshot.Reference)
                                    (Revision.value snapshot.Revision)
                                    act
                            ))
                            (token))
                |]

        let acceptedCount =
            replies
            |> Array.filter (fun reply ->
                match reply.Outcome with
                | ExecuteResult.Accepted _ -> true
                | _ -> false)
            |> Array.length

        let refusedCount =
            replies
            |> Array.filter (fun reply ->
                match reply.Outcome with
                | ExecuteResult.Rejected _ -> true
                | _ -> false)
            |> Array.length

        Expect.equal
            (acceptedCount, refusedCount)
            (1, 1)
            "Same-revision contenders admit exactly one acceptance"

    }

let private revocation (database: PostgresFixture.Database) owner principal =
    task {
        let! snapshot = current database "synthetic-growth-0"
        let writerGate = SubmissionStorage.Barrier(database.Fixture.Store)
        let revokeGate = SubmissionStorage.Barrier(database.Fixture.Store)
        let fixture = database.Fixture

        let writer =
            Service.create
                BuildIdentity.current.Version
                fixture.InstallationId
                writerGate.Store
                fixture.Calendar
                principal

        let revoker =
            Service.create
                BuildIdentity.current.Version
                fixture.InstallationId
                revokeGate.Store
                fixture.Calendar
                fixture.Owner

        let act =
            match snapshot.State with
            | (CaseState.Active(fields) | CaseState.Voided(fields)) when
                fields.Status = CaseStatus.Closed
                ->
                (AuthoredCaseAct.Bare BareAct.Reopen)
            | _ -> (AuthoredCaseAct.Bare BareAct.Close)

        let write =
            (Service.execute
                writer
                (ClaimCore.TestSupport.Inputs.caseOperation (
                    request
                        710010
                        (CaseReference.format snapshot.Reference)
                        (Revision.value snapshot.Revision)
                        act
                ))
                (token))

        let revoke =
            grant
                revoker
                (id 710011)
                principal
                {
                    Role = Role.CaseHandler
                    Scope = GrantTarget.Installation
                }
                false

        do! Task.WhenAll(writerGate.Staged, revokeGate.Staged).WaitAsync(TimeSpan.FromSeconds 5.)
        use! holder = database.OwnerSource.OpenConnectionAsync()
        use! transaction = holder.BeginTransactionAsync()
        let! _ = Transactions.head holder token
        revokeGate.Release()
        do! waiters database 1L
        writerGate.Release()
        do! waiters database 2L
        do! transaction.CommitAsync()
        let! revoked = revoke
        acceptedAuthority revoked
        let! written = write

        match written.Outcome with
        | ExecuteResult.Rejected _ -> ()
        | _ -> failtest "Acceptance must reauthorize the waiting writer after revocation"



        let! restored =
            grant
                owner
                (id 710012)
                principal
                {
                    Role = Role.CaseHandler
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority restored

    }

let private longCase database (actor: ServiceContext) =
    task {
        let! snapshot = current database "synthetic-growth-998"
        let mutable revision = Revision.value snapshot.Revision

        let mutable closed =
            match snapshot.State with
            | (CaseState.Active(fields) | CaseState.Voided(fields)) ->
                fields.Status = CaseStatus.Closed
            | _ -> failtest "Live case expected"

        let watch = Stopwatch.StartNew()

        for index in 0..999 do
            let act =
                if closed then
                    (AuthoredCaseAct.Bare BareAct.Reopen)
                else
                    (AuthoredCaseAct.Bare BareAct.Close)

            let! reply =
                (Service.execute
                    actor
                    (ClaimCore.TestSupport.Inputs.caseOperation (
                        request
                            (720000 + index)
                            (CaseReference.format snapshot.Reference)
                            revision
                            act
                    ))
                    (token))

            accepted reply |> ignore

            revision <- revision + 1L
            closed <- not closed

        let! final = current database (CaseReference.format snapshot.Reference)

        Expect.equal
            (Revision.value final.Revision)
            revision
            "The long history's final projection includes every accepted operation"

        return
            {|
                writes = 1000
                seconds = watch.Elapsed.TotalSeconds
                finalRevision = revision
            |}
    }

let private erase (database: PostgresFixture.Database) (actor: ServiceContext) =
    task {
        let! granted =
            grant
                actor
                (id 730000)
                database.Fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority granted

        let! snapshot = current database "synthetic-growth-998"
        let watch = Stopwatch.StartNew()

        let! erased =
            (Service.execute
                actor
                (ClaimCore.TestSupport.Inputs.caseOperation (
                    request
                        730001
                        (CaseReference.format snapshot.Reference)
                        (Revision.value snapshot.Revision)
                        (AuthoredCaseAct.Bare BareAct.Erase)
                ))
                (token))

        accepted erased |> ignore
        watch.Stop()
        let! final = current database (CaseReference.format snapshot.Reference)
        Expect.equal final.State CaseState.Erased "Erasure clears the final projection"

        let! revoked =
            grant
                actor
                (id 730002)
                database.Fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                false

        acceptedAuthority revoked
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows
                connection
                "SELECT count(*),count(*) FILTER(WHERE payload IS NOT NULL OR payload_nonce IS NOT NULL),min(creation_reference) FILTER(WHERE act='OPEN') FROM claimcore.ledger WHERE subject_kind='CASE' AND subject_id=$1"
                [| box (CaseId.value snapshot.CaseId) |]
                token

        let entries = DatabaseSession.value<int64> 0 rows.Head

        Expect.isGreaterThanOrEqual
            entries
            1001L
            "The erasure control clears a genuinely long history"

        Expect.equal
            (DatabaseSession.value<int64> 1 rows.Head)
            0L
            "Every erasable byte/nonce is absent"

        Expect.equal
            (DatabaseSession.value<string> 2 rows.Head)
            "synthetic-growth-998"
            "Creation reference is retained"

        return
            {|
                entries = entries
                elapsedMilliseconds = watch.Elapsed.TotalMilliseconds
            |}
    }

let private recovery (database: PostgresFixture.Database) actor =
    task {
        let! before = Verification.verify database.OwnerSource None token
        let before = VerificationSupport.position before
        let watch = Stopwatch.StartNew()
        let! first = resolve actor { OperationId = id 740000 } token

        Expect.equal
            first
            (Outcome.Succeeded Resolved.NotAccepted)
            "The first final resolution commits a retained control"

        let firstMilliseconds = watch.Elapsed.TotalMilliseconds
        watch.Restart()

        for _ in 1..1000 do
            let! repeated = resolve actor { OperationId = id 740000 } token
            Expect.equal repeated first "Repeated final resolution is stable"

        let repeatedMilliseconds = watch.Elapsed.TotalMilliseconds

        let! delayed =
            Service.execute
                actor
                (Inputs.caseOperation (
                    request 740000 "GROWTH-RESOLVED" 0L (AuthoredCaseAct.Open registration)
                ))
                token

        Expect.equal
            delayed.Outcome
            (ExecuteResult.Rejected Refusal.OperationResolved)
            "The logged final answer blocks delayed acceptance"

        let! after = Verification.verify database.OwnerSource None token
        let after = VerificationSupport.position after

        Expect.equal
            after.Sequence
            (Sequence.successor before.Sequence)
            "Repeated resolution and delayed execute append no additional records"

        return
            {|
                retainedEntries = 1
                repetitions = 1000
                firstMilliseconds = firstMilliseconds
                repeatedMilliseconds = repeatedMilliseconds
            |}
    }

let run database actor principal =
    task {
        do! sameCase database actor
        do! revocation database actor principal
        let! longHistory = longCase database actor
        let! erasure = erase database actor
        let! recovery = recovery database actor

        return
            {|
                accepted = 1006
                retainedControls = 1
                recovery = recovery
                erasure = erasure
                sameCaseRefusals = 1
                revokedWriterRefusals = 1
                longHistory = longHistory
            |}
    }
