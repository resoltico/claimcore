module ClaimCore.IntegrationTests.RecoveryTests

open System
open System.Threading
open System.Text
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Contract
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreContractTests

type private SubmissionInterleaving
    (
        inner: ILedgerStore,
        after: unit -> System.Threading.Tasks.Task<unit>,
        ?afterLoad: unit -> System.Threading.Tasks.Task<unit>,
        ?beforeCaseDecision: unit -> unit
    ) =
    let afterLoad =
        defaultArg afterLoad (fun () -> System.Threading.Tasks.Task.FromResult())

    let beforeCaseDecision = defaultArg beforeCaseDecision ignore
    let mutable businessAttempts = 0

    let finish pending =
        task {
            let! result = pending

            match result with
            | Ok(RecoveryStaged.Stored _) -> do! after ()
            | _ -> ()

            return result
        }

    let recovery =
        { new IRecoveryStore with
            member _.StageCase(principal, envelope, decide, ct) =
                finish (inner.Recovery.StageCase(principal, envelope, decide, ct))

            member _.StageAuthority(principal, request, envelope, decide, ct) =
                finish (inner.Recovery.StageAuthority(principal, request, envelope, decide, ct))

            member _.Pending(actor, after, limit, ct) =
                inner.Recovery.Pending(actor, after, limit, ct)

            member _.Load(actor, request, ct) =
                task {
                    let! result = inner.Recovery.Load(actor, request, ct)

                    match result with
                    | Ok(Some _) -> do! afterLoad ()
                    | _ -> ()

                    return result
                }

            member _.Forget(actor, id, ct) = inner.Recovery.Forget(actor, id, ct)
            member _.Retained(actor, id, ct) = inner.Recovery.Retained(actor, id, ct)
        }

    member _.BusinessAttempts = businessAttempts

    interface ILedgerStore with
        member _.Recovery = recovery

        member _.WriteCase(principal, id, reference, decide, ct) =
            businessAttempts <- businessAttempts + 1

            inner.WriteCase(
                principal,
                id,
                reference,
                (fun view ->
                    let decision = decide view
                    beforeCaseDecision ()
                    decision),
                ct
            )

        member _.WriteAuthority(principal, id, target, reference, decide, ct) =
            businessAttempts <- businessAttempts + 1
            inner.WriteAuthority(principal, id, target, reference, decide, ct)

        member _.ReadAuthorityTarget(principal, id, target, reference, ct) =
            inner.ReadAuthorityTarget(principal, id, target, reference, ct)

        member _.ReadCase(principal, id, reference, ct) =
            inner.ReadCase(principal, id, reference, ct)

        member _.ReadActor(principal, ct) = inner.ReadActor(principal, ct)
        member _.ReadOperation(principal, id, ct) = inner.ReadOperation(principal, id, ct)

        member _.ListCases(principal, roles, select, limit, ct) =
            inner.ListCases(principal, roles, select, limit, ct)

        member _.ReadHistory(principal, reference, select, limit, ct) =
            inner.ReadHistory(principal, reference, select, limit, ct)

        member _.ListActors(principal, select, limit, ct) =
            inner.ListActors(principal, select, limit, ct)

        member _.ListGrants(principal, target, select, limit, ct) =
            inner.ListGrants(principal, target, select, limit, ct)

let private token = CancellationToken.None

let private run action =
    async {
        let! database = PostgresFixture.create () |> Async.AwaitTask

        try
            do! action database |> Async.AwaitTask
        finally
            database.Fixture.Dispose()
    }

let private pending (actor: IActorClaimsCore) =
    task {
        let! result = actor.Pending({ AfterCursor = None; Limit = 50 }, token)

        match result with
        | ReadReply.Succeeded page -> return page.Items
        | _ -> return failtest "Expected recovery metadata."
    }

let private proxiedSource (database: PostgresFixture.Database) port =
    let settings =
        NpgsqlConnectionStringBuilder(database.RuntimeSource.ConnectionString)

    settings.Password <- "synthetic-only"
    settings.Host <- "127.0.0.1"
    settings.Port <- port
    settings.SslMode <- SslMode.Disable
    settings.GssEncryptionMode <- GssEncryptionMode.Disable
    settings.Pooling <- false
    NpgsqlDataSource.Create settings.ConnectionString

let private checkStageEvidence
    (database: PostgresFixture.Database)
    (actor: IActorClaimsCore)
    request
    drop
    =
    task {
        let! verification = Verification.verify database.OwnerSource None token

        Expect.equal
            (VerificationSupport.position verification).Sequence
            3L
            "Only owner and grants exist"

        let! rows = pending actor

        let retained =
            rows
            |> List.tryFind (fun row -> row.OperationId = OperationInput.operationId request)

        Expect.equal
            retained.IsSome
            (drop = CommitProxy.Drop.AfterCommit)
            "The journal may have committed despite reply loss"

        match retained with
        | Some item ->
            let! retried =
                actor.Retry(
                    {
                        RecoveryId = item.RecoveryId
                        OperationId = item.OperationId
                    },
                    token
                )

            accepted retried |> ignore
        | None -> ()
    }

let private stageLoss drop (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture

        let settings =
            NpgsqlConnectionStringBuilder(database.RuntimeSource.ConnectionString)

        use proxy = new CommitProxy.Proxy(nonNull settings.Host, settings.Port, drop)
        use source = proxiedSource database proxy.Port

        let store =
            LedgerStore(
                source,
                database.Fixture.InstallationId,
                Transactions.timeouts,
                PostgresFixture.recoveryProtection
            )
            :> ILedgerStore

        let caller =
            Core.create
                database.Fixture.InstallationId
                store
                database.Fixture.Calendar
                database.Fixture.Owner

        let request =
            OperationInput.Case(request 81 "JOURNAL-LOSS" 0L (CaseAct.Open registration))

        let! result = caller.Execute(request, token)
        do! proxy.Intercepted.WaitAsync(TimeSpan.FromSeconds 3.)

        Expect.equal
            result.Outcome
            (ExecuteResult.Failed CoreFault.StoreUnavailable)
            "Journal confirmation loss never starts business acceptance"

        do! checkStageEvidence database actor request drop
    }

let private unreadableProtection (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture

        let! result =
            actor.Execute(
                OperationInput.Case(request 82 "UNREADABLE" 0L (CaseAct.Open registration)),
                token
            )

        let id =
            result.RecoveryId |> Option.defaultWith (fun () -> failtest "Journal identity")

        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! _ =
            DatabaseSession.execute
                connection
                "UPDATE claimcore.recovery_requests SET protected_body=$1 WHERE recovery_id=$2"
                [| box [| 0uy |]; box id |]
                token

        let! retried =
            actor.Retry(
                {
                    RecoveryId = id
                    OperationId = result.OperationId
                },
                token
            )

        Expect.equal
            retried.Outcome
            (ExecuteResult.Failed CoreFault.StoreIntegrityError)
            "Unreadable bytes never execute"

        let! verification = Verification.verify database.OwnerSource None token

        Expect.equal
            (VerificationSupport.position verification).Sequence
            4L
            "No extra accepted operation"
    }

let private erasedRecovery (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture

        let! grantResult =
            grant
                actor
                (id 83)
                database.Fixture.Owner
                Role.DataSteward
                GrantTarget.Installation
                true

        acceptedAuthority grantResult

        let! opened =
            actor.Execute(
                OperationInput.Case(request 84 "ERASE-JOURNAL" 0L (CaseAct.Open registration)),
                token
            )

        accepted opened |> ignore

        let! erased =
            actor.Execute(OperationInput.Case(request 85 "ERASE-JOURNAL" 1L CaseAct.Erase), token)

        accepted erased |> ignore
        Expect.isNone erased.RecoveryId "Erasure clears its own journal too"
        let! rows = pending actor

        Expect.isFalse
            (rows |> List.exists (fun row -> row.CaseReference = Some "ERASE-JOURNAL"))
            "No actor's indexed case copy remains"

        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! count =
            DatabaseSession.rows
                connection
                "SELECT count(*) FROM claimcore.recovery_requests WHERE case_reference='ERASE-JOURNAL'"
                [||]
                token

        Expect.equal
            (DatabaseSession.value<int64> 0 count.Head)
            0L
            "Live protected copies physically removed"

        let! again =
            actor.Execute(
                OperationInput.Case(
                    request 86 "ERASE-JOURNAL" 2L (CaseAct.AmendRegistration registration)
                ),
                token
            )

        Expect.equal
            again.Outcome
            (ExecuteResult.Rejected Refusal.CaseErased)
            "Stage cannot restore erased content"
    }

let private revokeBetweenTransactions (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture

        let revoke () =
            task {
                let! result =
                    grant
                        actor
                        (id 88)
                        database.Fixture.Owner
                        Role.CaseHandler
                        GrantTarget.Installation
                        false

                acceptedAuthority result
            }

        let store = SubmissionInterleaving(database.Fixture.Store, revoke) :> ILedgerStore

        let caller =
            Core.create
                database.Fixture.InstallationId
                store
                database.Fixture.Calendar
                database.Fixture.Owner

        let! result =
            caller.Execute(
                OperationInput.Case(request 87 "REVOKED-AFTER-STAGE" 0L (CaseAct.Open registration)),
                token
            )

        Expect.equal
            result.Outcome
            (ExecuteResult.Rejected Refusal.ResourceUnavailable)
            "Acceptance independently rereads current grants after journal commit"

        Expect.isSome result.RecoveryId "Refused acceptance keeps submitted evidence"
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! counts =
            DatabaseSession.rows
                connection
                "SELECT count(*) FROM claimcore.cases WHERE case_reference='REVOKED-AFTER-STAGE'"
                [||]
                token

        Expect.equal
            (DatabaseSession.value<int64> 0 counts.Head)
            0L
            "No unauthorized business projection"
    }

let private storedEnvelope (database: PostgresFixture.Database) recoveryId =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows
                connection
                ($"SELECT {RecoveryRows.columns} FROM claimcore.recovery_requests WHERE recovery_id=$1")
                [| box recoveryId |]
                token

        let record = RecoveryRows.read rows.Head

        let plain =
            RecoveryRows.decode
                (PostgresFixture.recoveryProtection().Value)
                database.Fixture.InstallationId
                record

        return record, plain
    }

let private frozenRawBody (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture

        let! opened =
            actor.Execute(
                OperationInput.Case(request 90 "FROZEN" 0L (CaseAct.Open registration)),
                token
            )

        accepted opened |> ignore

        let input =
            OperationInput.Case(request 91 "FROZEN" 999L (CaseAct.AmendRegistration registration))

        let compact = Api.operationExecute.Input.Write input |> WireWriter.bytes

        let raw =
            Array.concat [ Encoding.UTF8.GetBytes " \n"; compact; Encoding.UTF8.GetBytes " \t" ]

        let envelope =
            ExecutionEnvelope.Admit(input, raw)
            |> Result.defaultWith (fun _ -> failtest "Raw admission")

        let! first = actor.ExecuteEnvelope(envelope, token)

        Expect.equal
            first.Outcome
            (ExecuteResult.Rejected(Refusal.RevisionConflict 1L))
            "Known business refusal follows journal commit"

        let! repeat = actor.Execute(input, token)

        Expect.equal
            repeat.RecoveryId
            first.RecoveryId
            "Canonical-equal input recognizes its original reservation"

        let! record, plain = storedEnvelope database first.RecoveryId.Value

        Expect.sequenceEqual
            plain.Bytes
            raw
            "The first admitted raw body survives canonical-equivalent resubmission"

        let metadata =
            RecoveryCodecs.item.Write record.Item
            |> WireWriter.bytes
            |> Encoding.UTF8.GetString

        Expect.isFalse
            (metadata.Contains registration.ClaimantName)
            "Metadata contains no claimant body"
    }

let private secondCaseWorker fixture (admin: IActorClaimsCore) =
    task {
        let principal =
            PrincipalKey.human "https://issuer.example/" "recovery-second"
            |> Result.defaultWith failtest

        let! registered =
            admin.Execute(
                OperationInput.Authority(
                    authorityRequest (id 93) principal AdmittedAuthorityAct.RegisterActor
                ),
                token
            )

        acceptedAuthority registered

        let! granted =
            grant admin (id 94) principal Role.CaseHandler GrantTarget.Installation true

        acceptedAuthority granted
        return core fixture principal
    }

let private foreignAccess
    (owner: IActorClaimsCore)
    (other: IActorClaimsCore)
    (selected: RecoveryRequest)
    =
    task {
        let! hidden = other.Retry(selected, token)

        Expect.equal
            hidden.Outcome
            (ExecuteResult.Rejected Refusal.ResourceUnavailable)
            "A foreign recovery identity is unavailable"

        let! forgotten = other.Forget({ RecoveryId = selected.RecoveryId }, token)

        Expect.equal
            forgotten
            (ReadReply.Succeeded())
            "Foreign forget is indistinguishable from absence"

        let! kept = owner.Retry(selected, token)

        Expect.equal
            kept.Outcome
            (ExecuteResult.Rejected(Refusal.RevisionConflict 1L))
            "Foreign forget cannot delete owned content"

    }

let private actorScopedIdentity (database: PostgresFixture.Database) =
    task {
        let! owner = caseWorker database.Fixture
        let! other = secondCaseWorker database.Fixture owner

        let! opened =
            owner.Execute(
                OperationInput.Case(request 95 "SCOPED" 0L (CaseAct.Open registration)),
                token
            )

        accepted opened |> ignore

        let input =
            OperationInput.Case(request 96 "SCOPED" 999L (CaseAct.AmendRegistration registration))

        let! first = owner.Execute(input, token)
        let! second = other.Execute(input, token)
        Expect.isSome first.RecoveryId "First actor reserves its own rejected request"

        Expect.isSome
            second.RecoveryId
            "Second actor may independently reserve the same operation ID"

        Expect.notEqual
            first.RecoveryId
            second.RecoveryId
            "Opaque reservation identities are actor scoped"

        let selected =
            {
                RecoveryId = first.RecoveryId.Value
                OperationId = first.OperationId
            }

        do! foreignAccess owner other selected

        let changed =
            OperationInput.Case(
                request
                    96
                    "SCOPED"
                    999L
                    (CaseAct.AmendRegistration
                        { registration with
                            ClaimedAmount = "1.00"
                        })
            )

        let! conflict = owner.Execute(changed, token)

        Expect.equal
            conflict.Outcome
            (ExecuteResult.Rejected Refusal.IdempotencyConflict)
            "Different authored canonical content cannot replace a reservation"

        let! rows = pending other

        Expect.isFalse
            (rows |> List.exists (fun row -> row.RecoveryId = selected.RecoveryId))
            "No foreign journal enumeration"
    }

let private expireAndCheck
    (database: PostgresFixture.Database)
    (actor: IActorClaimsCore)
    (item: RecoveryItem)
    =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! _ =
            DatabaseSession.execute
                connection
                "UPDATE claimcore.recovery_requests SET expires_at=clock_timestamp() WHERE recovery_id=$1"
                [| box item.RecoveryId |]
                token

        let! unavailable =
            actor.Retry(
                {
                    RecoveryId = item.RecoveryId
                    OperationId = item.OperationId
                },
                token
            )

        Expect.equal
            unavailable.Outcome
            (ExecuteResult.Rejected Refusal.ResourceUnavailable)
            "Expiry is enforced during retry"

        let! listed = pending actor

        Expect.isFalse
            (listed |> List.exists (fun row -> row.RecoveryId = item.RecoveryId))
            "Expiry is enforced on read"

    }

let private expiredIdentity (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture

        let! opened =
            actor.Execute(
                OperationInput.Case(request 97 "EXPIRY" 0L (CaseAct.Open registration)),
                token
            )

        accepted opened |> ignore

        let input =
            OperationInput.Case(request 98 "EXPIRY" 999L (CaseAct.AmendRegistration registration))

        let! first = actor.Execute(input, token)
        let! rows = pending actor
        let item = rows |> List.find (fun row -> Some row.RecoveryId = first.RecoveryId)

        Expect.equal
            (item.ExpiresAt - item.CreatedAt)
            (TimeSpan.FromDays(float ServiceLimits.RecoveryRetentionDays))
            "Fixed non-sliding retention"

        let! repeated = actor.Execute(input, token)
        let! unchanged = pending actor

        Expect.equal
            (unchanged |> List.find (fun row -> Some row.RecoveryId = repeated.RecoveryId))
                .ExpiresAt
            item.ExpiresAt
            "Retry cannot extend expiry"

        do! expireAndCheck database actor item

        let! restaged = actor.Execute(input, token)

        Expect.isSome
            restaged.RecoveryId
            "Explicit fresh admission may replace an expired reservation"

        Expect.notEqual
            restaged.RecoveryId
            first.RecoveryId
            "Expired identity is never silently revived"
    }

let private boundedCleanup (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! _ =
            DatabaseSession.execute
                connection
                "INSERT INTO claimcore.recovery_requests(recovery_id,actor_id,operation_id,contract_identity,scope,act,created_at,expires_at,protected_body) SELECT gen_random_uuid(),gen_random_uuid(),gen_random_uuid(),$1,'CASE','OPEN',clock_timestamp()-interval '8 days',clock_timestamp()-interval '1 day',decode('00','hex') FROM generate_series(1,100)"
                [| box ContractIdentity.value |]
                token

        let! _ =
            DatabaseSession.execute
                connection
                "INSERT INTO claimcore.recovery_requests(recovery_id,actor_id,operation_id,contract_identity,scope,act,case_reference,expected_revision,created_at,expires_at,protected_body) SELECT gen_random_uuid(),actor_id,$1,$2,'CASE','OPEN','CLEANUP',0,clock_timestamp()-interval '8 days',clock_timestamp()-interval '1 hour',decode('00','hex') FROM claimcore.recovery_requests WHERE expires_at>clock_timestamp() LIMIT 1"
                [| box (id 99); box ContractIdentity.value |]
                token

        let! result =
            actor.Execute(
                OperationInput.Case(request 99 "CLEANUP" 0L (CaseAct.Open registration)),
                token
            )

        accepted result |> ignore

        let! counts =
            DatabaseSession.rows
                connection
                "SELECT count(*) FROM claimcore.recovery_requests WHERE expires_at<=clock_timestamp()"
                [||]
                token

        Expect.equal
            (DatabaseSession.value<int64> 0 counts.Head)
            1L
            "Targeted expired identity removal shares the fixed100 cleanup budget"
    }

let private checkCapResults (attempts: ExecuteReply array) =
    let acceptedCount =
        attempts
        |> Array.filter (fun reply ->
            match reply.Outcome with
            | ExecuteResult.Accepted _ -> true
            | _ -> false)
        |> Array.length

    let refusedCount =
        attempts
        |> Array.filter (fun reply ->
            reply.Outcome = ExecuteResult.Failed CoreFault.StoreUnavailable
            && reply.RecoveryId.IsNone)
        |> Array.length

    Expect.equal acceptedCount 1 "Head serialized admission reserves only the last actor slot"
    Expect.equal refusedCount 1 "The losing admission never executes business work"

let private checkCapProjections (database: PostgresFixture.Database) =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! count =
            DatabaseSession.rows
                connection
                "SELECT count(*) FROM claimcore.recovery_requests"
                [||]
                token

        Expect.equal
            (DatabaseSession.value<int64> 0 count.Head)
            (int64 ServiceLimits.RecoveryPerActor)
            "Concurrent admission cannot overshoot the actor cap"

        let! cases =
            DatabaseSession.rows
                connection
                "SELECT count(*) FROM claimcore.cases WHERE case_reference IN ('CAP-RACE-A','CAP-RACE-B')"
                [||]
                token

        Expect.equal
            (DatabaseSession.value<int64> 0 cases.Head)
            1L
            "Exactly one contender has an accepted case projection"
    }

let private concurrentCap (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture

        for n in 1..97 do
            let! acceptedReply =
                actor.Execute(
                    OperationInput.Case(
                        request (1000 + n) $"CAP-REAL-{n}" 0L (CaseAct.Open registration)
                    ),
                    token
                )

            accepted acceptedReply |> ignore

        let! attempts =
            System.Threading.Tasks.Task.WhenAll
                [|
                    actor.Execute(
                        OperationInput.Case(
                            request 2001 "CAP-RACE-A" 0L (CaseAct.Open registration)
                        ),
                        token
                    )
                    actor.Execute(
                        OperationInput.Case(
                            request 2002 "CAP-RACE-B" 0L (CaseAct.Open registration)
                        ),
                        token
                    )
                |]

        checkCapResults attempts
        do! checkCapProjections database
    }

let private stageOtherAuthority fixture (actor: IActorClaimsCore) =
    task {
        let! other = secondCaseWorker fixture actor

        let otherPrincipal =
            PrincipalKey.human "https://issuer.example/" "recovery-second"
            |> Result.defaultWith failtest

        let! ownership =
            grant actor (id 105) otherPrincipal Role.Owner GrantTarget.Installation true

        acceptedAuthority ownership

        let! otherMissing =
            grant
                other
                (id 106)
                otherPrincipal
                Role.CaseReader
                (GrantTarget.CaseReference "FUTURE-CASE")
                true

        Expect.isSome
            otherMissing.RecoveryId
            "Another actor retains its independent authority request"

    }

let private erasedAuthorityEvidence
    (database: PostgresFixture.Database)
    (actor: IActorClaimsCore)
    (missing: ExecuteReply)
    =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! copies =
            DatabaseSession.rows
                connection
                "SELECT count(*) FROM claimcore.recovery_requests WHERE case_reference='FUTURE-CASE'"
                [||]
                token

        Expect.equal
            (DatabaseSession.value<int64> 0 copies.Head)
            0L
            "Erasure removes even authority rows staged before the case existed"

        let! retry =
            actor.Retry(
                {
                    RecoveryId = missing.RecoveryId.Value
                    OperationId = missing.OperationId
                },
                token
            )

        Expect.equal
            retry.Outcome
            (ExecuteResult.Rejected Refusal.ResourceUnavailable)
            "Erased authority copy is unavailable"
    }

let private authorityBeforeCaseCreation (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture

        let! stewardship =
            grant
                actor
                (id 101)
                database.Fixture.Owner
                Role.DataSteward
                GrantTarget.Installation
                true

        acceptedAuthority stewardship

        let! missing =
            grant
                actor
                (id 102)
                database.Fixture.Owner
                Role.CaseReader
                (GrantTarget.CaseReference "FUTURE-CASE")
                true

        Expect.equal
            missing.Outcome
            (ExecuteResult.Rejected Refusal.ResourceUnavailable)
            "Missing target grant is business refused after journaling"

        Expect.isSome missing.RecoveryId "Authority submission is retained before target creation"

        do! stageOtherAuthority database.Fixture actor

        let! opened =
            actor.Execute(
                OperationInput.Case(request 103 "FUTURE-CASE" 0L (CaseAct.Open registration)),
                token
            )

        accepted opened |> ignore

        let! erased =
            actor.Execute(OperationInput.Case(request 104 "FUTURE-CASE" 1L CaseAct.Erase), token)

        accepted erased |> ignore
        do! erasedAuthorityEvidence database actor missing
    }

let private protectionOutsideHead (database: PostgresFixture.Database) =
    task {
        let! _ = caseWorker database.Fixture
        let mutable initialized = false
        let security = PostgresFixture.recoveryProtection().Value

        let prepare () =
            DatabaseSession.transaction
                database.RuntimeSource
                System.Data.IsolationLevel.ReadCommitted
                token
                (fun connection _ ->
                    task {
                        let! _ =
                            DatabaseSession.execute
                                connection
                                "SET LOCAL lock_timeout='100ms'"
                                [||]
                                token

                        let! _ = Transactions.head connection token
                        initialized <- true
                    })
            |> fun work -> work.GetAwaiter().GetResult()

        let protection =
            { security with
                Prepare = prepare
                Protect =
                    fun purpose bytes ->
                        Expect.isTrue
                            initialized
                            "Resources initialized before per-record protection"

                        security.Protect purpose bytes
            }

        let store =
            LedgerStore(
                database.RuntimeSource,
                database.Fixture.InstallationId,
                Transactions.timeouts,
                fun () -> Some protection
            )
            :> ILedgerStore

        let caller =
            Core.create
                database.Fixture.InstallationId
                store
                database.Fixture.Calendar
                database.Fixture.Owner

        let! result =
            caller.Execute(
                OperationInput.Case(request 107 "PREPARE-KEYS" 0L (CaseAct.Open registration)),
                token
            )

        accepted result |> ignore

        Expect.isTrue
            initialized
            "Initialization can acquire a separate head lock because staging has not acquired it"
    }

let private protectedCaller (database: PostgresFixture.Database) protection =
    let store =
        LedgerStore(
            database.RuntimeSource,
            database.Fixture.InstallationId,
            Transactions.timeouts,
            fun () -> protection
        )
        :> ILedgerStore

    Core.create
        database.Fixture.InstallationId
        store
        database.Fixture.Calendar
        database.Fixture.Owner

let private privateKeyRing () =
    let directory = System.IO.Directory.CreateTempSubdirectory "claimcore-recovery-"

    try
        System.IO.File.SetUnixFileMode(
            directory.FullName,
            System.IO.UnixFileMode.UserRead
            ||| System.IO.UnixFileMode.UserWrite
            ||| System.IO.UnixFileMode.UserExecute
        )

        let admitted = ClaimCore.Web.SessionKeys.admit directory.FullName

        admitted,
        { new IDisposable with
            member _.Dispose() = directory.Delete true
        }
    with error ->
        directory.Delete true
        raise error

let private expectReplayWithoutLocator expectedOperation (reply: ExecuteReply) =
    Expect.isTrue
        (match reply.Outcome with
         | ExecuteResult.AlreadyAccepted receipt -> receipt.OperationId = expectedOperation
         | _ -> false)
        "Exact accepted ledger identity remains authoritative"

    Expect.isNone
        reply.RecoveryId
        "Unreadable or unavailable optional recovery content is never acknowledged"

let private acceptedReplayAfterKeyLoss (database: PostgresFixture.Database) =
    task {
        let! _ = caseWorker database.Fixture
        let originalKeys, originalOwned = privateKeyRing ()
        use _originalOwned = originalOwned

        let original =
            protectedCaller database (Some(ClaimCore.Web.RecoveryProtection.create originalKeys))

        let input =
            OperationInput.Case(request 211 "LOST-KEY" 0L (CaseAct.Open registration))

        let! submitted = original.Execute(input, token)
        accepted submitted |> ignore

        let selected =
            {
                RecoveryId = submitted.RecoveryId.Value
                OperationId = submitted.OperationId
            }

        let replacementKeys, replacementOwned = privateKeyRing ()
        use _replacementOwned = replacementOwned

        let replacement =
            protectedCaller database (Some(ClaimCore.Web.RecoveryProtection.create replacementKeys))

        let! opaque = replacement.Retry(selected, token)

        Expect.equal
            opaque.Outcome
            (ExecuteResult.Failed CoreFault.StoreIntegrityError)
            "Opaque retry cannot execute content without its retained key"

        let! known = replacement.Execute(input, token)
        expectReplayWithoutLocator submitted.OperationId known
        let unavailable = protectedCaller database None
        let! knownWithoutProtection = unavailable.Execute(input, token)
        expectReplayWithoutLocator submitted.OperationId knownWithoutProtection

        let! fresh =
            unavailable.Execute(
                OperationInput.Case(request 212 "NO-KEYS" 0L (CaseAct.Open registration)),
                token
            )

        Expect.equal
            fresh.Outcome
            (ExecuteResult.Failed CoreFault.StoreUnavailable)
            "Fresh staging cannot proceed without initialized protection"

        let! verified = Verification.verify database.OwnerSource None token

        Expect.equal
            (VerificationSupport.position verified).Sequence
            4L
            "Neither replay nor unavailable fresh staging appends"
    }

let private noAuthoritySubmission
    (database: PostgresFixture.Database)
    (observed: SubmissionInterleaving)
    =
    task {
        Expect.equal
            observed.BusinessAttempts
            0
            "Technical target refusal never starts transaction B"

        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows
                connection
                "SELECT count(*) FROM claimcore.recovery_requests WHERE operation_id=$1"
                [| box (id 215) |]
                token

        Expect.equal
            (DatabaseSession.value<int64> 0 rows.Head)
            0L
            "An unknown actor target has no retained placeholder metadata"
    }

let private absentAuthorityTarget (database: PostgresFixture.Database) =
    task {
        let! admin = caseWorker database.Fixture

        let principal =
            PrincipalKey.human "https://issuer.example/" "future-actor"
            |> Result.defaultWith failtest

        let observed =
            SubmissionInterleaving(
                database.Fixture.Store,
                fun () -> System.Threading.Tasks.Task.FromResult()
            )

        let caller =
            Core.create
                database.Fixture.InstallationId
                (observed :> ILedgerStore)
                database.Fixture.Calendar
                database.Fixture.Owner

        let input =
            OperationInput.Authority(
                authorityRequest (id 215) principal (AdmittedAuthorityAct.SetEnabled false)
            )

        let! absent = caller.Execute(input, token)

        Expect.equal
            absent.Outcome
            (ExecuteResult.Rejected Refusal.ActorUnknown)
            "Non-register request needs an identifiable actor target"

        Expect.isNone absent.RecoveryId "No invented target actor ID is journaled"

        do! noAuthoritySubmission database observed

        let! registered =
            admin.Execute(
                OperationInput.Authority(
                    authorityRequest (id 216) principal AdmittedAuthorityAct.RegisterActor
                ),
                token
            )

        acceptedAuthority registered
        let! acceptedAfterRegistration = caller.Execute(input, token)
        acceptedAuthority acceptedAfterRegistration

        Expect.equal
            observed.BusinessAttempts
            1
            "Explicit supplied execution runs after target registration"

        let! listed = pending admin
        let selected = listed |> List.find (fun item -> item.OperationId = id 215)

        Expect.equal
            selected.TargetActorId
            (Some(id 216))
            "Frozen metadata uses the registered target's actual identity"
    }

let private replayNeedsNoOperationalCommit (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture

        let input =
            OperationInput.Case(request 217 "READ-ONLY-REPLAY" 0L (CaseAct.Open registration))

        let! original = actor.Execute(input, token)
        accepted original |> ignore

        let settings =
            NpgsqlConnectionStringBuilder(database.RuntimeSource.ConnectionString)

        use proxy =
            new CommitProxy.Proxy(
                nonNull settings.Host,
                settings.Port,
                CommitProxy.Drop.BeforeCommit
            )

        use source = proxiedSource database proxy.Port

        let store =
            LedgerStore(
                source,
                database.Fixture.InstallationId,
                Transactions.timeouts,
                PostgresFixture.recoveryProtection
            )
            :> ILedgerStore

        let caller =
            Core.create
                database.Fixture.InstallationId
                store
                database.Fixture.Calendar
                database.Fixture.Owner

        let! replay = caller.Execute(input, token)

        Expect.isTrue
            (match replay.Outcome with
             | ExecuteResult.AlreadyAccepted receipt -> receipt.OperationId = original.OperationId
             | _ -> false)
            "An authorized exact replay needs no operational commit acknowledgement"

        Expect.equal
            replay.RecoveryId
            original.RecoveryId
            "Matching committed recovery content remains acknowledgeable"

        Expect.isFalse
            proxy.Intercepted.IsCompleted
            "No journal or ledger COMMIT is dispatched for read-only replay"
    }

let private raceCase fixture reference =
    task {
        let! actor = caseWorker fixture

        let! steward =
            grant actor (id 2700000) fixture.Owner Role.DataSteward GrantTarget.Installation true

        acceptedAuthority steward

        let! opened =
            actor.Execute(
                OperationInput.Case(request 2700001 reference 0L (CaseAct.Open registration)),
                token
            )

        accepted opened |> ignore
        return actor
    }

let private probeCore (database: PostgresFixture.Database) (probe: SubmissionInterleaving) =
    Core.create
        database.Fixture.InstallationId
        (probe :> ILedgerStore)
        database.Fixture.Calendar
        database.Fixture.Owner

let private releaseAfterControl release (pending: System.Threading.Tasks.Task<'T>) control =
    task {
        let! failure =
            task {
                try
                    do! control ()
                    return None
                with error ->
                    return Some error
            }

        release ()
        let! result = pending.WaitAsync(TimeSpan.FromSeconds 15.)
        return result, failure
    }

let private erasedWithoutCopies (database: PostgresFixture.Database) reference =
    task {
        let! view =
            database.Fixture.Store.ReadCase(database.Fixture.Owner, None, reference, token)

        match view with
        | Ok { Case = Some record } ->
            Expect.equal record.Snapshot.View CaseView.Erased "The tombstone remains authoritative"
        | _ -> failtest "Erased case missing"

        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows
                connection
                "SELECT count(*) FROM claimcore.recovery_requests WHERE case_reference=$1"
                [| box reference |]
                token

        Expect.equal
            (DatabaseSession.value<int64> 0 rows.Head)
            0L
            "No live recovery copy survived or was restored"
    }

let private waitForHead (database: PostgresFixture.Database) =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()
        let watch = Diagnostics.Stopwatch.StartNew()
        let mutable waiting = 0L

        while waiting = 0L && watch.Elapsed < TimeSpan.FromSeconds 3. do
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE 'SELECT sequence,entry_hash,observed_at FROM claimcore.lock_ledger_head%'"
                    [||]
                    token

            waiting <- DatabaseSession.value<int64> 0 rows.Head

            if waiting = 0L then
                do! System.Threading.Tasks.Task.Delay 10

        Expect.isGreaterThan
            waiting
            0L
            "The intended stage is actually waiting behind the erasure head lock"
    }

let private stageThenErase (database: PostgresFixture.Database) =
    task {
        let reference = "STAGE-BEFORE-ERASE"
        let! actor = raceCase database.Fixture reference
        let barrier = SubmissionStorage.Barrier(database.Fixture.Store, id 2700002)
        let fixture = database.Fixture

        let writer =
            Core.create fixture.InstallationId barrier.Store fixture.Calendar fixture.Owner

        let pending =
            writer.Execute(
                OperationInput.Case(
                    request 2700002 reference 1L (CaseAct.AmendRegistration registration)
                ),
                token
            )

        let! outcome, failure =
            releaseAfterControl barrier.Release pending (fun () ->
                task {
                    do! barrier.Staged.WaitAsync(TimeSpan.FromSeconds 5.)

                    let! erased =
                        actor.Execute(
                            OperationInput.Case(request 2700003 reference 1L CaseAct.Erase),
                            token
                        )

                    accepted erased |> ignore
                })

        failure |> Option.iter raise

        Expect.equal
            outcome.Outcome
            (ExecuteResult.Rejected Refusal.CaseErased)
            "A committed earlier stage cannot authorize acceptance after erasure"

        Expect.isNone outcome.RecoveryId "Erasure removed the in-flight request's retained copy"
        do! erasedWithoutCopies database reference
    }

let private caseAmend actor number reference revision =
    (actor: IActorClaimsCore)
        .Execute(
            OperationInput.Case(
                request number reference revision (CaseAct.AmendRegistration registration)
            ),
            token
        )

let private headPause (release: ManualResetEventSlim) =
    let entered =
        System.Threading.Tasks.TaskCompletionSource(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
        )

    let pause () =
        entered.TrySetResult() |> ignore

        if not (release.Wait(TimeSpan.FromSeconds 10.)) then
            failtest "Erasure release deadline"

    entered.Task, pause

let private loadPause () =
    let ready =
        System.Threading.Tasks.TaskCompletionSource(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
        )

    let release =
        System.Threading.Tasks.TaskCompletionSource(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
        )

    let pause () =
        task {
            ready.TrySetResult() |> ignore
            do! release.Task
        }

    ready.Task, (fun () -> release.TrySetResult() |> ignore), pause

let private eraseThenStage (database: PostgresFixture.Database) =
    task {
        let reference = "ERASE-BEFORE-STAGE"
        let! _ = raceCase database.Fixture reference

        use release = new ManualResetEventSlim()
        let entered, pause = headPause release

        let eraserProbe =
            SubmissionInterleaving(
                database.Fixture.Store,
                (fun () -> System.Threading.Tasks.Task.FromResult()),
                beforeCaseDecision = pause
            )

        let writerProbe =
            SubmissionInterleaving(
                database.Fixture.Store,
                fun () -> System.Threading.Tasks.Task.FromResult()
            )

        let erasing =
            (probeCore database eraserProbe)
                .Execute(OperationInput.Case(request 2700003 reference 1L CaseAct.Erase), token)

        let mutable writing = None

        let! erased, failure =
            releaseAfterControl release.Set erasing (fun () ->
                task {
                    do! entered.WaitAsync(TimeSpan.FromSeconds 5.)

                    writing <-
                        Some(caseAmend (probeCore database writerProbe) 2700002 reference 1L)

                    do! waitForHead database
                })

        failure |> Option.iter raise

        let! outcome =
            writing
            |> Option.defaultWith (fun () -> failtest "Stage contender was never dispatched")

        accepted erased |> ignore

        Expect.equal
            outcome.Outcome
            (ExecuteResult.Rejected Refusal.CaseErased)
            "The stage waiting for erasure sees the committed tombstone"

        Expect.equal
            writerProbe.BusinessAttempts
            0
            "Refused late staging never enters business acceptance"

        do! erasedWithoutCopies database reference
    }

let private loadedRetryThenErase (database: PostgresFixture.Database) =
    task {
        let reference = "RETRY-BEFORE-ERASE"
        let! actor = raceCase database.Fixture reference

        let! submitted =
            actor.Execute(
                OperationInput.Case(
                    request 2700002 reference 999L (CaseAct.AmendRegistration registration)
                ),
                token
            )

        let ready, release, pause = loadPause ()

        let probe =
            SubmissionInterleaving(
                database.Fixture.Store,
                (fun () -> System.Threading.Tasks.Task.FromResult()),
                afterLoad = pause
            )

        let pending =
            (probeCore database probe)
                .Retry(
                    {
                        RecoveryId = submitted.RecoveryId.Value
                        OperationId = submitted.OperationId
                    },
                    token
                )

        let! outcome, failure =
            releaseAfterControl release pending (fun () ->
                task {
                    do! ready.WaitAsync(TimeSpan.FromSeconds 5.)

                    let! erased =
                        actor.Execute(
                            OperationInput.Case(request 2700003 reference 1L CaseAct.Erase),
                            token
                        )

                    accepted erased |> ignore
                })

        failure |> Option.iter raise

        Expect.equal
            outcome.Outcome
            (ExecuteResult.Rejected Refusal.CaseErased)
            "A decrypted in-flight retry cannot restore erased content"

        Expect.equal probe.BusinessAttempts 0 "Retry must pass fresh staging before transaction B"
        do! erasedWithoutCopies database reference
    }

let private recoveryHttp
    (host: ClaimCore.WebTests.TestServerFixture.Host)
    csrf
    (endpoint: Endpoint<'Input, 'Output>)
    input
    =
    task {
        let! status, bytes =
            host.Call(endpoint.Id, endpoint.Input.Write input |> WireWriter.bytes, csrf)

        Expect.equal status 200 "Recovery HTTP outcome is typed"

        let output =
            StrictJson.read 100000 bytes
            |> Result.bind (endpoint.Output.Read "$")
            |> Result.defaultWith (fun _ ->
                failtest "Published recovery reply codec rejected the response")

        Expect.isFalse
            (Encoding.UTF8.GetString(bytes).Contains "Superseded synthetic name")
            "Historical claimant text never crosses this boundary"

        return output
    }

let private recoveryHuman (database: PostgresFixture.Database) =
    task {
        let! admin = caseWorker database.Fixture

        let principal =
            PrincipalKey.human
                ClaimCore.WebTests.TestServerFixture.oidc.Issuer.AbsoluteUri
                "synthetic-human"
            |> Result.defaultWith failtest

        let! registered =
            admin.Execute(
                OperationInput.Authority(
                    authorityRequest (id 2800000) principal AdmittedAuthorityAct.RegisterActor
                ),
                token
            )

        acceptedAuthority registered

        let! granted =
            grant admin (id 2800001) principal Role.CaseRegistrar GrantTarget.Installation true

        acceptedAuthority granted
        return admin, principal
    }

let private supersededRecovery admin (host: ClaimCore.WebTests.TestServerFixture.Host) csrf =
    task {
        let original =
            request
                2800002
                "RECOVERY-DISCLOSURE"
                0L
                (CaseAct.Open
                    { registration with
                        ClaimantName = "Superseded synthetic name"
                    })
        // The first accepted receipt may disclose the current assertion; later recovery must not.
        let! status, bytes =
            host.Call(
                Api.operationExecute.Id,
                Api.operationExecute.Input.Write(OperationInput.Case original)
                |> WireWriter.bytes,
                csrf
            )

        Expect.equal status 200 "Original browser submission"

        let submitted =
            StrictJson.read 100000 bytes
            |> Result.bind (Api.operationExecute.Output.Read "$")
            |> Result.defaultWith (fun _ -> failtest "Execute reply")

        accepted submitted |> ignore

        let! changed = caseAmend admin 2800003 original.CaseReference 1L

        accepted changed |> ignore

        let selected =
            {
                RecoveryId = submitted.RecoveryId.Value
                OperationId = submitted.OperationId
            }

        let! replay = recoveryHttp host csrf Api.operationRetry selected

        match replay.Outcome with
        | ExecuteResult.AlreadyAccepted receipt ->
            Expect.equal
                (caseReceipt receipt).Snapshot
                SnapshotDisclosure.Withheld
                "Registrar retry withholds superseded receipt fields"
        | _ -> failtest "Expected authorized exact replay"

        return selected
    }

let private currentOnlyRecovery admin principal host csrf (selected: RecoveryRequest) =
    task {
        let! reader =
            grant admin (id 2800004) principal Role.CaseReader GrantTarget.Installation true

        acceptedAuthority reader

        let! revoked =
            grant admin (id 2800005) principal Role.CaseRegistrar GrantTarget.Installation false

        acceptedAuthority revoked

        let! listed =
            recoveryHttp host csrf Api.operationPending { AfterCursor = None; Limit = 50 }

        match listed with
        | ReadReply.Succeeded page ->
            let item =
                page.Items |> List.find (fun item -> item.RecoveryId = selected.RecoveryId)

            Expect.equal
                item.Offers
                [ RecoveryOffer.Forget ]
                "Current-only reader cannot replay an old registration assertion"
        | _ -> failtest "Current-only actor can inspect its metadata"

        let! denied = recoveryHttp host csrf Api.operationRetry selected

        Expect.equal
            denied.Outcome
            (ExecuteResult.Rejected Refusal.ResourceUnavailable)
            "Current-only reader lacks fresh execution authority"
    }

let private disabledRecovery
    (admin: IActorClaimsCore)
    principal
    host
    csrf
    (selected: RecoveryRequest)
    =
    task {
        let! disabled =
            admin.Execute(
                OperationInput.Authority(
                    authorityRequest (id 2800006) principal (AdmittedAuthorityAct.SetEnabled false)
                ),
                token
            )

        acceptedAuthority disabled

        let! listed =
            recoveryHttp host csrf Api.operationPending { AfterCursor = None; Limit = 50 }

        Expect.equal
            listed
            (ReadReply.Rejected Refusal.ResourceUnavailable)
            "Disabled authenticated caller receives no metadata"

        let! denied = recoveryHttp host csrf Api.operationRetry selected

        Expect.equal
            denied.Outcome
            (ExecuteResult.Rejected Refusal.ResourceUnavailable)
            "Disabled actor cannot replay retained intent"
    }

let private recoveryDisclosure (database: PostgresFixture.Database) =
    task {
        let! admin, principal = recoveryHuman database

        use host =
            new ClaimCore.WebTests.TestServerFixture.Host(true, forActor = core database.Fixture)

        let! status, _ = host.Login()
        Expect.equal status 200 "Synthetic OIDC browser authenticated"
        let! csrf = host.Token()
        let! selected = supersededRecovery admin host csrf
        do! currentOnlyRecovery admin principal host csrf selected
        do! disabledRecovery admin principal host csrf selected
        let! verified = Verification.verify database.OwnerSource None token

        Expect.equal
            (VerificationSupport.position verified).Sequence
            10L
            "Recovery reads and refused retries never append ledger entries"
    }

[<Tests>]
let tests =
    testList
        "protected recovery at the real database boundary"
        [
            testCaseAsync
                "recovery enforces superseded disclosure current authority and disabled actors over HTTP"
                (run recoveryDisclosure)
            testCaseAsync
                "stage commit before erasure cannot later accept or restore content"
                (run stageThenErase)
            testCaseAsync
                "erasure commit before a waiting stage forbids a new copy"
                (run eraseThenStage)
            testCaseAsync
                "erasure wins against an already loaded opaque retry"
                (run loadedRetryThenErase)
            testCaseAsync
                "global cap rejects below-actor-cap contenders for the final slot"
                (run GrowthRecovery.qualifyGlobalBound)
            testCaseAsync
                "exact accepted replay dispatches no operational commit"
                (run replayNeedsNoOperationalCommit)
            testCaseAsync
                "accepted exact replay remains available after recovery key loss"
                (run acceptedReplayAfterKeyLoss)
            testCaseAsync
                "unknown authority actor target never journals a placeholder identity"
                (run absentAuthorityTarget)
            testCaseAsync
                "protection resources initialize before the head lock"
                (run protectionOutsideHead)
            testCaseAsync
                "case erasure removes earlier authority requests for an absent case"
                (run authorityBeforeCaseCreation)
            testCaseAsync
                "concurrent cap admission never overshoots the actor bound"
                (run concurrentCap)
            testCaseAsync
                "actor scoped reservations exclude foreign retry and forget"
                (run actorScopedIdentity)
            testCaseAsync
                "expiry is fixed and enforced on every recovery access"
                (run expiredIdentity)
            testCaseAsync "expiry cleanup remains bounded at admission" (run boundedCleanup)
            testCaseAsync
                "raw request remains frozen across canonical-equal resubmission"
                (run frozenRawBody)
            testCaseAsync
                "revocation between journal and acceptance is authoritative"
                (run revokeBetweenTransactions)
            testCaseAsync
                "lost journal commit reply never dispatches acceptance"
                (run (stageLoss CommitProxy.Drop.AfterCommit))
            testCaseAsync
                "aborted journal commit never dispatches acceptance"
                (run (stageLoss CommitProxy.Drop.BeforeCommit))
            testCaseAsync "unreadable protection is a safe typed fault" (run unreadableProtection)
            testCaseAsync
                "accepted erasure clears recovery and blocks restaging"
                (run erasedRecovery)
        ]
