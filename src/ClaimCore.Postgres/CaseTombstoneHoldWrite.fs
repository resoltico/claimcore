namespace ClaimCore.Postgres

open System
open System.Data
open System.IO
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain

module internal CaseTombstoneHoldWrite =
    let private expectedHash (change: TombstoneHoldChange) =
        try
            let bytes = Convert.FromHexString(change.ExpectedAuthorityHash)

            if
                bytes.Length = 32
                && change.ExpectedAuthorityHash = Convert.ToHexStringLower bytes
            then
                Some bytes
            else
                None
        with _ ->
            None

    let private basic (change: TombstoneHoldChange) instant =
        if change.EventId = Guid.Empty || change.CaseId = Guid.Empty then
            Error LifecycleRefusal.InvalidIdentity
        elif change.ExpectedAuthorityRevision < 0L || expectedHash change |> Option.isNone then
            Error LifecycleRefusal.VersionConflict
        else
            match change.Mutation with
            | TombstoneHoldMutation.Record(holdId, ground, reviewOn) ->
                TombstoneHoldPolicy.validateRecord holdId ground reviewOn instant
            | TombstoneHoldMutation.Release(holdId, releaseCode) ->
                TombstoneHoldPolicy.validateRelease holdId releaseCode

    let private holdId =
        function
        | TombstoneHoldMutation.Record(id, _, _)
        | TombstoneHoldMutation.Release(id, _) -> id

    let private exists connection transaction id =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS(SELECT 1 FROM claimcore.case_erasure_holds WHERE hold_id=@hold)",
                    connection,
                    transaction
                )

            Sql.uuid command "hold" id
            let! result = command.ExecuteScalarAsync()
            return result :?> bool
        }

    let private decision
        connection
        transaction
        (change: TombstoneHoldChange)
        (stored: StoredCaseTombstone)
        =
        task {
            if
                stored.AuthorityRevision <> change.ExpectedAuthorityRevision
                || stored.AuthorityHash <> (expectedHash change |> Option.defaultValue Array.empty)
            then
                return Error LifecycleRefusal.VersionConflict
            else
                let! active = CaseTombstoneRead.activeHolds connection transaction change.CaseId
                let id = holdId change.Mutation

                match change.Mutation with
                | TombstoneHoldMutation.Record _ ->
                    let! known = exists connection transaction id

                    if known then
                        return Error LifecycleRefusal.DuplicateHold
                    elif active.Length >= 256 then
                        return Error LifecycleRefusal.HoldCapacityExceeded
                    else
                        return Ok()
                | TombstoneHoldMutation.Release _ ->
                    if active |> List.exists (fun (hold, _) -> hold = id) then
                        return Ok()
                    else
                        return Error LifecycleRefusal.HoldNotFound
        }

    let private commit
        connection
        transaction
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (change: TombstoneHoldChange)
        (stored: StoredCaseTombstone)
        instant
        =
        task {
            do! CaseErasurePurgeDelete.verifyAbsent connection transaction change.CaseId

            let canonical =
                CaseTombstoneCandidate.hold
                    change
                    context.Binding.ActorId
                    context.Binding.GrantRevision
                    stored.AuthorityHash
                    instant

            try
                try
                    let intent =
                        witness.BeginAuthority(change.EventId, canonical, Some change.CaseId)

                    do!
                        CaseTombstoneHoldPersistence.persist
                            connection
                            transaction
                            context
                            change
                            stored
                            canonical
                            intent
                            instant

                    do! transaction.CommitAsync()

                    try
                        witness.SettleAuthority(change.EventId, intent) |> ignore

                        return
                            TombstoneWriteOutcome.Applied(
                                change.EventId,
                                stored.AuthorityRevision + 1L
                            )
                    with _ ->
                        return TombstoneWriteOutcome.Unconfirmed change.EventId
                with _ ->
                    return TombstoneWriteOutcome.Unconfirmed change.EventId
            finally
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(canonical)
        }

    let private afterAuthorization
        connection
        transaction
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (change: TombstoneHoldChange)
        (stored: StoredCaseTombstone)
        (instant: DateTimeOffset)
        =
        task {
            let! prior = CaseTombstoneHoldReplay.find connection transaction change.EventId

            match prior with
            | Some receipt ->
                return CaseTombstoneHoldReplay.reconcile witness context change receipt
            | None when stored.Phase = "ERASURE_FINAL" ->
                return TombstoneWriteOutcome.Refused LifecycleRefusal.WrongPrivacyPhase
            | None ->
                let! outcome = decision connection transaction change stored

                match outcome with
                | Error refusal -> return TombstoneWriteOutcome.Refused refusal
                | Ok() ->
                    return! commit connection transaction witness context change stored instant
        }

    let private pruneReady
        connection
        transaction
        (witness: WitnessProtocol)
        (stored: StoredCaseTombstone)
        caseId
        =
        task {
            if stored.PruneEventId.IsNone then
                return true
            else
                let! proof =
                    CaseTombstonePruneReceiptAudit.verify
                        connection
                        transaction
                        witness
                        (witness.Snapshot()).TipSequence
                        caseId

                return proof.IsSome
        }

    let private write
        dataSource
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (change: TombstoneHoldChange)
        instant
        =
        task {
            use! connection = RuntimeDatabase.openConnectionAsync dataSource
            use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)

            let! revision =
                ActorGrantRead.lockRevision
                    connection
                    transaction
                    true
                    System.Threading.CancellationToken.None

            let! found = CaseTombstoneRead.lock connection transaction change.CaseId

            match found with
            | None -> return TombstoneWriteOutcome.ResourceUnavailable
            | Some stored ->
                let! available = pruneReady connection transaction witness stored change.CaseId

                if not available then
                    return TombstoneWriteOutcome.ResourceUnavailable
                else
                    let! allowed =
                        ActorMutationGuard.authorizeScope
                            connection
                            transaction
                            context
                            (ResourceScope.Case change.CaseId)
                            revision

                    if not allowed then
                        return TombstoneWriteOutcome.ResourceUnavailable
                    else
                        return!
                            afterAuthorization
                                connection
                                transaction
                                witness
                                context
                                change
                                stored
                                instant
        }

    let change
        dataSource
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (request: TombstoneHoldChange)
        (instant: DateTimeOffset)
        =
        task {
            let utcInstant = instant.Offset = TimeSpan.Zero
            let instant = CaseLifecycleStoreSupport.microsecondInstant instant

            if
                context.Action <> EndpointAction.ManageTombstoneHold
                || context.CaseId <> Some request.CaseId
            then
                return TombstoneWriteOutcome.ResourceUnavailable
            elif not utcInstant then
                return TombstoneWriteOutcome.Refused LifecycleRefusal.InvalidTime
            else
                match basic request instant with
                | Error refusal -> return TombstoneWriteOutcome.Refused refusal
                | Ok() ->
                    try
                        witness.Admit()
                        return! write dataSource witness context request instant
                    with
                    | :? InvalidDataException ->
                        return TombstoneWriteOutcome.Failed CoreFault.StoreIntegrityError
                    | _ -> return TombstoneWriteOutcome.Failed CoreFault.StoreUnavailable
        }
