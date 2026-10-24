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

let private acknowledge (actor: IActorClaimsCore) (reply: ExecuteReply) =
    task {
        acceptedAuthority reply

        match reply.RecoveryId with
        | None -> failtest "Setup execution must retain an acknowledgement identity"
        | Some key ->
            let! forgotten = actor.Forget({ RecoveryId = key }, token)
            Expect.equal forgotten (ReadReply.Succeeded()) "Known setup result acknowledged"
    }

let private globalActors fixture =
    task {
        let! owner = bootstrap fixture

        let! access =
            grant owner (id 2300000) fixture.Owner Role.CaseHandler GrantTarget.Installation true

        do! acknowledge owner access
        let actors = ResizeArray<Guid>()

        for index in 0..99 do
            let principal =
                PrincipalKey.service "https://issuer.example/" $"global-cap-{index}"
                |> Result.defaultWith failtest

            let actorId = id (2400000 + index)

            let! registered =
                owner.Execute(
                    OperationInput.Authority(
                        authorityRequest actorId principal AdmittedAuthorityAct.RegisterActor
                    ),
                    token
                )

            do! acknowledge owner registered

            let! granted =
                grant
                    owner
                    (id (2410000 + index))
                    principal
                    Role.CaseHandler
                    GrantTarget.Installation
                    true

            do! acknowledge owner granted
            actors.Add actorId

        return owner, actors.ToArray()
    }

let private globalSeedRecord
    (database: PostgresFixture.Database)
    (actors: Guid array)
    security
    instant
    index
    =
    let reference = $"GLOBAL-SEED-{index}"

    let input =
        OperationInput.Case(request (2500000 + index) reference 0L (CaseAct.Open registration))

    let envelope = ExecutionEnvelope.Create input

    let caseId =
        ClaimCore.RecordFormat.CaseReferenceId.encode database.Fixture.InstallationId reference

    let record =
        RecoveryRows.metadata (Guid.NewGuid()) actors[index / 100] (Some caseId) None instant input

    let protectedBody =
        security.Protect
            (RecoveryRows.purpose database.Fixture.InstallationId record)
            envelope.Bytes

    { record with
        ProtectedBody = protectedBody
    }

let private seedGlobalRows (database: PostgresFixture.Database) (actors: Guid array) =
    task {
        let security = PostgresFixture.recoveryProtection().Value
        security.Prepare()

        return!
            DatabaseSession.transaction
                database.OwnerSource
                System.Data.IsolationLevel.ReadCommitted
                token
                (fun connection transaction ->
                    task {
                        let! _, instant, _ = Transactions.head connection token
                        let keys = ResizeArray<Guid>()

                        for index in 0..9998 do
                            let record = globalSeedRecord database actors security instant index
                            let! _ = RecoveryRows.insert connection record token

                            keys.Add record.Item.RecoveryId

                        do! transaction.CommitAsync token
                        return keys.ToArray()
                    })
    }

let private globalContenders (owner: IActorClaimsCore) =
    task {
        let! replies =
            Task.WhenAll
                [|
                    owner.Execute(
                        OperationInput.Case(
                            request 2600000 "GLOBAL-CONTENDER-A" 0L (CaseAct.Open registration)
                        ),
                        token
                    )
                    owner.Execute(
                        OperationInput.Case(
                            request 2600001 "GLOBAL-CONTENDER-B" 0L (CaseAct.Open registration)
                        ),
                        token
                    )
                |]

        let accepted =
            replies
            |> Array.filter (fun reply ->
                match reply.Outcome with
                | ExecuteResult.Accepted _ -> true
                | _ -> false)
            |> Array.length

        let refused =
            replies
            |> Array.filter (fun reply ->
                reply.Outcome = ExecuteResult.Failed CoreFault.StoreUnavailable
                && reply.RecoveryId.IsNone)
            |> Array.length

        Expect.equal
            (accepted, refused)
            (1, 1)
            "The last global slot has exactly one winner while both contenders are below their actor cap"

        let! excess =
            owner.Execute(
                OperationInput.Case(
                    request 2600002 "GLOBAL-CONTENDER-C" 0L (CaseAct.Open registration)
                ),
                token
            )

        Expect.equal
            excess.Outcome
            (ExecuteResult.Failed CoreFault.StoreUnavailable)
            "Global-full admission rejects an actor with only one retained request"
    }

/// Complement the full-path 10k fill with an independent last-global-slot rejecting control.
let qualifyGlobalBound (database: PostgresFixture.Database) =
    task {
        let! owner, actors = globalActors database.Fixture
        let! keys = seedGlobalRows database actors
        let! before = head database
        do! globalContenders owner
        let! after = head database

        Expect.equal
            after
            (before + 1L)
            "Only the one admitted contender reaches business acceptance"

        let! rows = count database
        Expect.equal rows 10000L "The installation cap never overshoots"
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! retained =
            DatabaseSession.rows
                connection
                "SELECT count(*) FROM claimcore.recovery_requests WHERE recovery_id=ANY($1)"
                [| box keys |]
                token

        Expect.equal
            (DatabaseSession.value<int64> 0 retained.Head)
            9999L
            "No live submitted request is silently evicted"
    }
