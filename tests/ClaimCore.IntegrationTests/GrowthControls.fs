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
                AuthoredCaseAct.Reopen
            | _ -> AuthoredCaseAct.Close

        let! replies =
            Task.WhenAll
                [|
                    for index in 0..1 ->
                        (Service.execute
                            actor
                            (OperationInput.Case(
                                request
                                    (710000 + index)
                                    (CaseReference.format snapshot.Reference)
                                    snapshot.Revision
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
                AuthoredCaseAct.Reopen
            | _ -> AuthoredCaseAct.Close

        let write =
            (Service.execute
                writer
                (OperationInput.Case(
                    request 710010 (CaseReference.format snapshot.Reference) snapshot.Revision act
                ))
                (token))

        let revoke =
            grant revoker (id 710011) principal Role.CaseHandler GrantTarget.Installation false

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
            grant owner (id 710012) principal Role.CaseHandler GrantTarget.Installation true

        acceptedAuthority restored

    }

let private longCase database (actor: ServiceContext) =
    task {
        let! snapshot = current database "synthetic-growth-998"
        let mutable revision = snapshot.Revision

        let mutable closed =
            match snapshot.State with
            | (CaseState.Active(fields) | CaseState.Voided(fields)) ->
                fields.Status = CaseStatus.Closed
            | _ -> failtest "Live case expected"

        let watch = Stopwatch.StartNew()

        for index in 0..999 do
            let act =
                if closed then
                    AuthoredCaseAct.Reopen
                else
                    AuthoredCaseAct.Close

            let! reply =
                (Service.execute
                    actor
                    (OperationInput.Case(
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
            final.Revision
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
                Role.DataSteward
                GrantTarget.Installation
                true

        acceptedAuthority granted

        let! snapshot = current database "synthetic-growth-999"

        let! erased =
            (Service.execute
                actor
                (OperationInput.Case(
                    request
                        730001
                        (CaseReference.format snapshot.Reference)
                        snapshot.Revision
                        AuthoredCaseAct.Erase
                ))
                (token))

        accepted erased |> ignore
        let! final = current database (CaseReference.format snapshot.Reference)
        Expect.equal final.State CaseState.Erased "Erasure clears the final projection"

        let! revoked =
            grant
                actor
                (id 730002)
                database.Fixture.Owner
                Role.DataSteward
                GrantTarget.Installation
                false

        acceptedAuthority revoked

    }

let run database actor principal =
    task {
        do! sameCase database actor
        do! revocation database actor principal
        let! longHistory = longCase database actor
        do! erase database actor

        return
            {|
                accepted = 1006
                sameCaseRefusals = 1
                revokedWriterRefusals = 1
                longHistory = longHistory
            |}
    }
