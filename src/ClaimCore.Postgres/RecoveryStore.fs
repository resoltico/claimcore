namespace ClaimCore.Postgres

open ClaimCore.Contract.Protocol

open System
open System.Threading
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Contract
open Npgsql

module internal RecoveryRows =
    let columns =
        "recovery_id,actor_id,operation_id,contract_identity,scope,act,case_id,case_reference,expected_revision,target_actor_id,created_at,expires_at,protected_body"

    let private subject row =
        let act = DatabaseSession.value<string> 5 row

        match DatabaseSession.value<string> 4 row with
        | "CASE" ->
            let kind =
                CaseActKinds.all
                |> List.tryFind (CaseActKinds.token >> (=) act)
                |> Option.defaultWith (fun () ->
                    raise (RecordCorruptException "Unknown recovery case act."))

            if DatabaseSession.optional<Guid> 9 row |> Option.isSome then
                raise (RecordCorruptException "Invalid recovery target.")

            RecoverySubject.Case
                {
                    CaseReference = DatabaseSession.value 7 row
                    ExpectedRevision = DatabaseSession.value 8 row
                    Act = kind
                }
        | "AUTHORITY" ->
            let kind =
                RecoveryAuthorityKinds.all
                |> List.tryFind (RecoveryAuthorityKinds.token >> (=) act)
                |> Option.defaultWith (fun () ->
                    raise (RecordCorruptException "Unknown recovery authority act."))

            if DatabaseSession.optional<int64> 8 row |> Option.isSome then
                raise (RecordCorruptException "Invalid recovery revision.")

            RecoverySubject.Authority
                {
                    TargetActorId = DatabaseSession.value 9 row
                    CaseReference = DatabaseSession.optional 7 row
                    Act = kind
                }
        | _ -> raise (RecordCorruptException "Unknown recovery scope.")

    let read row : RecoveryRecord =
        if DatabaseSession.value<string> 3 row <> ContractIdentity.value then
            raise (RecordCorruptException "Recovery contract differs.")

        {
            ActorId = DatabaseSession.value 1 row
            CaseId = DatabaseSession.optional 6 row
            ProtectedBody = DatabaseSession.value 12 row
            Item =
                {
                    RecoveryId = DatabaseSession.value 0 row
                    OperationId = DatabaseSession.value 2 row
                    Subject = subject row
                    CreatedAt = DatabaseSession.instant 10 row
                    ExpiresAt = DatabaseSession.instant 11 row
                    Offers = []
                }
        }

    let purpose installationId (record: RecoveryRecord) =
        let metadata =
            RecoveryCodecs.item.Write record.Item
            |> CanonicalWriter.bytes
            |> Convert.ToBase64String

        let target =
            record.CaseId
            |> Option.map (fun id -> id.ToString "D")
            |> Option.defaultValue "-"

        $"{installationId:D}/{record.ActorId:D}/{target}/{ContractIdentity.value}/{metadata}"

    let metadata id actor caseId target instant input : RecoveryRecord =
        let subject =
            match input with
            | OperationInput.Case request ->
                RecoverySubject.Case
                    {
                        CaseReference = request.CaseReference
                        ExpectedRevision = request.ExpectedRevision
                        Act = CaseActs.kind request.Act
                    }
            | OperationInput.Authority request ->
                let reference =
                    match request.Act with
                    | AuthorityAct.SetGrant {
                                                Scope = GrantTarget.CaseReference reference
                                            } -> Some reference
                    | _ -> None

                RecoverySubject.Authority
                    {
                        TargetActorId =
                            target
                            |> Option.defaultWith (fun () ->
                                invalidOp "Authority recovery needs its target actor.")
                        CaseReference = reference
                        Act = RecoveryAuthorityKinds.fromAct request.Act
                    }

        {
            ActorId = actor
            CaseId = caseId
            ProtectedBody = [||]
            Item =
                {
                    RecoveryId = id
                    OperationId = OperationInput.operationId input
                    Subject = subject
                    CreatedAt = instant
                    ExpiresAt = instant.AddDays(float ServiceLimits.RecoveryRetentionDays)
                    Offers = []
                }
        }

    let decode protection installationId (record: RecoveryRecord) =
        let raw =
            try
                protection.Unprotect (purpose installationId record) record.ProtectedBody
            with _ ->
                raise (RecordCorruptException "Recovery protection is unreadable.")

        match Codec.decode Limits.MaximumRequestBytes RequestCodecs.operationInput raw with
        | Error _ -> raise (RecordCorruptException "Recovery request is invalid.")
        | Ok input ->
            let expected =
                metadata
                    record.Item.RecoveryId
                    record.ActorId
                    record.CaseId
                    record.Item.TargetActorId
                    record.Item.CreatedAt
                    input

            if expected.Item <> record.Item then
                raise (RecordCorruptException "Recovery metadata differs from its request.")

            match ExecutionEnvelope.Admit(input, raw) with
            | Ok value -> value
            | Error _ -> raise (RecordCorruptException "Recovery envelope is invalid.")

    let insert connection (record: RecoveryRecord) token =
        DatabaseSession.execute
            connection
            "INSERT INTO claimcore.recovery_requests(recovery_id,actor_id,operation_id,contract_identity,scope,act,case_id,case_reference,expected_revision,target_actor_id,created_at,expires_at,protected_body) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)"
            [|
                box record.Item.RecoveryId
                box record.ActorId
                box record.Item.OperationId
                box ContractIdentity.value
                box record.Item.Scope
                box record.Item.Act
                DatabaseSession.parameter record.CaseId
                DatabaseSession.parameter record.Item.CaseReference
                DatabaseSession.parameter record.Item.ExpectedRevision
                DatabaseSession.parameter record.Item.TargetActorId
                box record.Item.CreatedAt
                box record.Item.ExpiresAt
                box record.ProtectedBody
            |]
            token

    let cleanup connection instant actor operationId token =
        DatabaseSession.execute
            connection
            "DELETE FROM claimcore.recovery_requests WHERE recovery_id IN (SELECT recovery_id FROM claimcore.recovery_requests WHERE expires_at <= $1 ORDER BY CASE WHEN actor_id=$2 AND operation_id=$3 THEN 0 ELSE 1 END,expires_at,recovery_id LIMIT $4)"
            [|
                box instant
                box actor
                box operationId
                box ServiceLimits.RecoveryCleanupBatch
            |]
            token

    let owned connection actor recoveryId operationId token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    ($"SELECT {columns} FROM claimcore.recovery_requests WHERE actor_id=$1 AND recovery_id=$2 AND operation_id=$3 AND expires_at>clock_timestamp()")
                    [| box actor; box recoveryId; box operationId |]
                    token

            return rows |> List.tryHead |> Option.map read
        }

type internal RecoveryStore
    (
        source: NpgsqlDataSource,
        installationId: Guid,
        settings,
        protection: unit -> RecoveryProtection option
    ) =
    let requireProtection () =
        protection ()
        |> Option.defaultWith (fun () -> invalidOp "Recovery protection is unavailable.")

    let canonical (envelope: ExecutionEnvelope) =
        RequestCodecs.operationInput.Write envelope.Input |> CanonicalWriter.bytes

    let protectAndInsert
        connection
        instant
        (envelope: ExecutionEnvelope)
        actor
        caseId
        target
        security
        token
        =
        task {
            let record =
                RecoveryRows.metadata (Guid.NewGuid()) actor caseId target instant envelope.Input

            let protectedBody =
                security.Protect (RecoveryRows.purpose installationId record) envelope.Bytes

            let! _ =
                RecoveryRows.insert
                    connection
                    { record with
                        ProtectedBody = protectedBody
                    }
                    token

            return RecoveryStaged.Stored(record.Item.RecoveryId, actor, envelope)
        }

    let insertFresh
        connection
        instant
        (envelope: ExecutionEnvelope)
        actor
        caseId
        target
        security
        token
        =
        task {
            let! counts =
                DatabaseSession.rows
                    connection
                    "SELECT count(*),count(*) FILTER (WHERE actor_id=$1) FROM claimcore.recovery_requests WHERE expires_at>$2"
                    [| box actor; box instant |]
                    token

            let total = DatabaseSession.value<int64> 0 counts.Head
            let owned = DatabaseSession.value<int64> 1 counts.Head

            if
                total >= int64 ServiceLimits.RecoveryInstallation
                || owned >= int64 ServiceLimits.RecoveryPerActor
            then
                return
                    RecoveryStaged.Terminal(ExecuteResult.Failed CoreFault.StoreUnavailable, None)
            else
                return!
                    protectAndInsert connection instant envelope actor caseId target security token
        }

    let ownedOperation connection instant actor (envelope: ExecutionEnvelope) token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    ($"SELECT {RecoveryRows.columns} FROM claimcore.recovery_requests WHERE actor_id=$1 AND operation_id=$2 AND expires_at>$3")
                    [| box actor; box (OperationInput.operationId envelope.Input); box instant |]
                    token

            return rows |> List.tryHead |> Option.map RecoveryRows.read
        }

    let replayLocator connection instant envelope actor security token =
        task {
            try
                match actor with
                | None -> return None
                | Some actor ->
                    let! owned = ownedOperation connection instant actor envelope token

                    return
                        owned
                        |> Option.bind (fun record ->
                            let frozen = RecoveryRows.decode security installationId record

                            if canonical frozen = canonical envelope then
                                Some record.Item.RecoveryId
                            else
                                None)
            with :? RecordCorruptException ->
                // Optional operational content cannot veto an already-authorized exact ledger receipt.
                return None
        }

    let store connection instant (envelope: ExecutionEnvelope) actor caseId target security token =
        task {
            let! _ =
                RecoveryRows.cleanup
                    connection
                    instant
                    actor
                    (OperationInput.operationId envelope.Input)
                    token

            let! owned = ownedOperation connection instant actor envelope token

            match owned with
            | Some record ->
                let existing = RecoveryRows.decode security installationId record

                if canonical existing = canonical envelope then
                    return RecoveryStaged.Stored(record.Item.RecoveryId, actor, existing)
                else
                    return
                        RecoveryStaged.Terminal(
                            ExecuteResult.Rejected Refusal.IdempotencyConflict,
                            None
                        )
            | None ->
                return! insertFresh connection instant envelope actor caseId target security token
        }

    let admit connection instant envelope prepared actor decision token =
        task {
            match decision with
            | RecoveryAdmission.Terminal(ExecuteResult.AlreadyAccepted _ as outcome) ->
                let! recovery =
                    match prepared with
                    | Ok security -> replayLocator connection instant envelope actor security token
                    | Error _ -> System.Threading.Tasks.Task.FromResult None

                return Ok(RecoveryStaged.Terminal(outcome, recovery))
            | RecoveryAdmission.Terminal outcome ->
                return Ok(RecoveryStaged.Terminal(outcome, None))
            | RecoveryAdmission.Ready(actor, caseId, target) ->
                match prepared with
                | Error failure -> return Error failure
                | Ok security ->
                    let! result =
                        store connection instant envelope actor caseId target security token

                    return Ok result
        }

    let prepare () =
        try
            match protection () with
            | None -> Error StoreFailure.Unavailable
            | Some security ->
                security.Prepare()
                Ok security
        with
        | :? System.Security.Cryptography.CryptographicException
        | :? System.IO.IOException
        | :? UnauthorizedAccessException -> Error StoreFailure.Unavailable
        | error -> Error(Transactions.failure false error)

    let stage token action =
        task {
            // Initialization occurs outside the head; accepted identity does not depend on its availability.
            let prepared = prepare ()

            let! result =
                Transactions.operational source settings token (fun connection ->
                    task {
                        let! result = action prepared connection

                        return
                            match result with
                            | Ok(RecoveryStaged.Stored _) ->
                                Transactions.OperationalDecision.Persist result
                            | _ -> Transactions.OperationalDecision.ReadOnly result
                    })

            return result |> Result.bind id
        }

    interface IRecoveryStore with
        member _.StageCase(principal, envelope, decide, token) =
            stage token (fun prepared connection ->
                task {
                    let! _, instant, _ = Transactions.head connection token

                    match envelope.Input with
                    | OperationInput.Authority _ ->
                        return invalidArg (nameof envelope) "Expected a case request."
                    | OperationInput.Case request ->
                        let! view =
                            DecisionViews.case
                                connection
                                installationId
                                principal
                                (Some request.OperationId)
                                request.CaseReference
                                instant
                                token

                        return!
                            admit
                                connection
                                instant
                                envelope
                                prepared
                                (view.Actor.Actor |> Option.map _.ActorId)
                                (decide view)
                                token
                })

        member _.StageAuthority(principal, request, envelope, decide, token) =
            stage token (fun prepared connection ->
                task {
                    let! _, instant, _ = Transactions.head connection token

                    let! view =
                        DecisionViews.authority
                            connection
                            installationId
                            principal
                            request.OperationId
                            request.Target
                            (AuthorityRequests.targetCase request)
                            instant
                            token

                    return!
                        admit
                            connection
                            instant
                            envelope
                            prepared
                            (view.Actor.Actor |> Option.map _.ActorId)
                            (decide view)
                            token
                })

        member _.Pending(actor, after, limit, token) =
            Transactions.read source token (fun connection ->
                task {
                    let! rows =
                        DatabaseSession.rows
                            connection
                            ($"SELECT {RecoveryRows.columns} FROM claimcore.recovery_requests WHERE actor_id=$1 AND ($2::uuid IS NULL OR recovery_id>$2) AND expires_at>clock_timestamp() ORDER BY recovery_id LIMIT $3")
                            [| box actor; DatabaseSession.parameter after; box limit |]
                            token

                    return rows |> List.map (RecoveryRows.read >> _.Item)
                })

        member _.Load(actor, request, token) =
            Transactions.read source token (fun connection ->
                task {
                    let! row =
                        RecoveryRows.owned
                            connection
                            actor
                            request.RecoveryId
                            request.OperationId
                            token

                    return
                        row
                        |> Option.map (RecoveryRows.decode (requireProtection ()) installationId)
                })

        member _.Forget(actor, id, token) =
            Transactions.operational source settings token (fun connection ->
                task {
                    let! _ =
                        DatabaseSession.execute
                            connection
                            "DELETE FROM claimcore.recovery_requests WHERE actor_id=$1 AND recovery_id=$2"
                            [| box actor; box id |]
                            token

                    return Transactions.OperationalDecision.Persist()
                })

        member _.Retained(actor, id, token) =
            Transactions.read source token (fun connection ->
                task {
                    let! rows =
                        DatabaseSession.rows
                            connection
                            "SELECT recovery_id FROM claimcore.recovery_requests WHERE actor_id=$1 AND recovery_id=$2 AND expires_at>clock_timestamp()"
                            [| box actor; box id |]
                            token

                    return not rows.IsEmpty
                })
