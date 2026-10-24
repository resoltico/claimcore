module internal ClaimCore.IntegrationTests.GrowthRecovery

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Contract
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreContractTests

let private token = CancellationToken.None

let private fill fixture (principal, index) =
    task {
        let actor = core fixture principal
        let rows = ResizeArray<Guid>()

        for entry in 0..99 do
            let request =
                request
                    (600000 + index * 100 + entry)
                    "synthetic-growth-0"
                    1000000L
                    (CaseAct.AmendRegistration registration)

            let! result = actor.Execute(OperationInput.Case request, token)

            match result.Outcome, result.RecoveryId with
            | ExecuteResult.Rejected _, Some recoveryId -> rows.Add recoveryId
            | _ ->
                let classification =
                    match result.Outcome with
                    | ExecuteResult.Rejected reason -> "rejected " + string reason
                    | ExecuteResult.Failed fault -> "failed " + string fault
                    | _ -> "unexpected execution outcome"

                failtestf
                    "Recovery fill actor index %d item %d: %s; retained=%b"
                    index
                    entry
                    classification
                    result.RecoveryId.IsSome

        return actor, rows.ToArray()
    }

let private count (database: PostgresFixture.Database) =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows
                connection
                "SELECT count(*) FROM claimcore.recovery_requests"
                [||]
                token

        return DatabaseSession.value<int64> 0 rows.Head
    }

let private expire (database: PostgresFixture.Database) key =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! _ =
            DatabaseSession.execute
                connection
                "UPDATE claimcore.recovery_requests SET expires_at=clock_timestamp() WHERE recovery_id=$1"
                [| box key |]
                token

        return ()
    }

let private head (database: PostgresFixture.Database) =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows connection "SELECT sequence FROM claimcore.ledger_head" [||] token

        return DatabaseSession.value<int64> 0 rows.Head
    }

let private boundaries database (owner: IActorClaimsCore) (ownerRows: Guid array) =
    task {
        let! before = head database

        let! capped =
            owner.Execute(
                OperationInput.Case(
                    request
                        699999
                        "synthetic-growth-0"
                        1000000L
                        (CaseAct.AmendRegistration registration)
                ),
                token
            )

        Expect.isNone capped.RecoveryId "Admission refuses a record above the cap"

        Expect.equal
            capped.Outcome
            (ExecuteResult.Failed CoreFault.StoreUnavailable)
            "The cap is a known operational storage refusal"

        let! after = head database
        Expect.equal after before "Journal capacity refusal does not attempt a business acceptance"
        do! expire database ownerRows[0]
        let! pending = owner.Pending({ AfterCursor = None; Limit = 50 }, token)

        match pending with
        | ReadReply.Succeeded page ->
            Expect.equal page.Items.Length 50 "The metadata page remains bounded at capacity"

            let! next =
                owner.Pending(
                    {
                        AfterCursor = page.NextCursor
                        Limit = 50
                    },
                    token
                )

            match next with
            | ReadReply.Succeeded following ->
                Expect.equal
                    (page.Items.Length + following.Items.Length)
                    99
                    "Expiry equality removes one record from pending"
            | _ -> failtest "Pending continuation failed at capacity"
        | _ -> failtest "Pending metadata failed at capacity"

    }

let run (database: PostgresFixture.Database) principals =
    task {
        let watch = Stopwatch.StartNew()

        let filled = ResizeArray<IActorClaimsCore * Guid array>()

        for batch in principals |> List.indexed |> List.chunkBySize 16 do
            let! completed =
                Task.WhenAll
                    [| for index, principal in batch -> fill database.Fixture (principal, index) |]

            filled.AddRange completed

        let! active = count database
        Expect.equal active 10000L "The real submission path reaches the global active-record cap"
        let owner, ownerRows = filled[0]

        do! boundaries database owner ownerRows

        for actor, rows in filled do
            for key in rows do
                let! forgotten = actor.Forget({ RecoveryId = key }, token)

                Expect.equal
                    forgotten
                    (ReadReply.Succeeded())
                    "Owned records are explicitly acknowledged"

        let! remaining = count database
        Expect.equal remaining 0L "No active qualification journal state remains"

        return
            {|
                activeRecords = active
                actors = filled.Count
                capRefusals = 1
                expired = 1
                remaining = remaining
                seconds = watch.Elapsed.TotalSeconds
            |}
    }
