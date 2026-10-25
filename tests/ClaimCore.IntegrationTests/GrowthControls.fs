module internal ClaimCore.IntegrationTests.GrowthControls

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Contract
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreContractTests

let private token = CancellationToken.None

let private current (database: PostgresFixture.Database) reference =
    task {
        let! read =
            database.Fixture.Store.ReadCase(database.Fixture.Owner, None, reference, token)

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

let private sameCase database (actor: IActorClaimsCore) =
    task {
        let! snapshot = current database "synthetic-growth-0"

        let act =
            match snapshot.View with
            | CaseView.Live(fields, _) when fields.Status = CaseStatus.Closed -> CaseAct.Reopen
            | _ -> CaseAct.Close

        let! replies =
            Task.WhenAll
                [|
                    for index in 0..1 ->
                        actor.Execute(
                            OperationInput.Case(
                                request (710000 + index) snapshot.Reference snapshot.Revision act
                            ),
                            token
                        )
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
        let writerGate = SubmissionStorage.Barrier(database.Fixture.Store, id 710010)
        let revokeGate = SubmissionStorage.Barrier(database.Fixture.Store, id 710011)
        let fixture = database.Fixture

        let writer =
            Core.create fixture.InstallationId writerGate.Store fixture.Calendar principal

        let revoker =
            Core.create fixture.InstallationId revokeGate.Store fixture.Calendar fixture.Owner

        let act =
            match snapshot.View with
            | CaseView.Live(fields, _) when fields.Status = CaseStatus.Closed -> CaseAct.Reopen
            | _ -> CaseAct.Close

        let write =
            writer.Execute(
                OperationInput.Case(request 710010 snapshot.Reference snapshot.Revision act),
                token
            )

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

let private longCase database (actor: IActorClaimsCore) =
    task {
        let! snapshot = current database "synthetic-growth-998"
        let mutable revision = snapshot.Revision

        let mutable closed =
            match snapshot.View with
            | CaseView.Live(fields, _) -> fields.Status = CaseStatus.Closed
            | _ -> failtest "Live case expected"

        let watch = Stopwatch.StartNew()

        for index in 0..999 do
            let act = if closed then CaseAct.Reopen else CaseAct.Close

            let! reply =
                actor.Execute(
                    OperationInput.Case(request (720000 + index) snapshot.Reference revision act),
                    token
                )

            accepted reply |> ignore

            revision <- revision + 1L
            closed <- not closed

        let! final = current database snapshot.Reference

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

let private erase (database: PostgresFixture.Database) (actor: IActorClaimsCore) =
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
            actor.Execute(
                OperationInput.Case(
                    request 730001 snapshot.Reference snapshot.Revision CaseAct.Erase
                ),
                token
            )

        accepted erased |> ignore
        let! final = current database snapshot.Reference
        Expect.equal final.View CaseView.Erased "Erasure clears the final projection"

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
