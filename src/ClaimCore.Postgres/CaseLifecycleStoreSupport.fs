namespace ClaimCore.Postgres

open System
open System.Data
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open WitnessProtocolReconciliation

module internal CaseLifecycleStoreSupport =
    let expectedAction =
        function
        | LifecycleMutation.VoidDataEntryError _ -> EndpointAction.VoidCase
        | LifecycleMutation.ReinstateVoided _ -> EndpointAction.ReinstateCase
        | LifecycleMutation.RequestErasure _ -> EndpointAction.RequestErasure
        | LifecycleMutation.MarkErasurePending _ -> EndpointAction.RequestErasure
        | LifecycleMutation.PurgeLivePayload _ -> EndpointAction.ApproveErasure
        | LifecycleMutation.RecordHold _
        | LifecycleMutation.ReleaseHold _ -> EndpointAction.ManageHolds

    let validChange (change: LifecycleChange) =
        change.EventId <> Guid.Empty
        && change.ExpectedRevision > 0L
        && change.ExpectedLifecycleSequence >= 0L
        && change.ExpectedLifecycleHash.Length = 64
        && change.ExpectedLifecycleHash = change.ExpectedLifecycleHash.ToLowerInvariant()
        && (change.ExpectedLifecycleHash
            |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
        && not (String.IsNullOrWhiteSpace change.CaseReference)

    let validInstant (instant: DateTimeOffset) = instant.Offset = TimeSpan.Zero

    let microsecondInstant (instant: DateTimeOffset) =
        DateTimeOffset(instant.UtcTicks - instant.UtcTicks % 10L, TimeSpan.Zero)

    let matchesProjection (change: LifecycleChange) (projection: LifecycleProjection) =
        let actualHash = Convert.ToHexStringLower(projection.EventHash)

        change.ExpectedRevision = (Claim.view projection.Claim).Version
        && change.ExpectedLifecycleSequence = projection.Sequence
        && change.ExpectedLifecycleHash = actualHash

    let matchesWitness
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT installation_id,lineage_id,witness_epoch "
                    + "FROM claimcore.installation_lineage WHERE singleton",
                    connection,
                    transaction
                )

            let! result = command.ExecuteReaderAsync()
            use reader = result
            let! found = reader.ReadAsync()
            let identity = witness.Identity

            return
                found
                && reader.GetGuid(0) = identity.InstallationId
                && reader.GetGuid(1) = identity.LineageId
                && reader.GetInt64(2) = identity.Epoch
                && not (reader.Read())
        }

    let lockCase connection transaction reference (eventId: Guid) =
        task {
            do! Sql.lockKeyAsync connection transaction ("operation:" + eventId.ToString("D"))
            do! Sql.lockKeyAsync connection transaction ("case:" + reference)
            return! CaseLifecycleRead.lockProjection connection transaction reference
        }

    let authorize
        connection
        transaction
        revision
        (context: ActorCallContext)
        (projection: LifecycleProjection)
        =
        task {
            if context.CaseId <> Some projection.CaseId then
                return false
            else
                return!
                    ActorMutationGuard.authorizeScope
                        connection
                        transaction
                        context
                        (ResourceScope.Case projection.CaseId)
                        revision
        }

    let replayEvent (witness: WitnessProtocol) eventId draftHash (stored: LifecycleStoredEvent) =
        if stored.DraftHash <> draftHash then
            LifecycleWriteOutcome.ResourceUnavailable
        else
            try
                witness.ReconcileAuthority(
                    eventId,
                    stored.WitnessSequence,
                    stored.WitnessEpoch,
                    stored.WitnessHash,
                    stored.Canonical
                )

                LifecycleWriteOutcome.Applied(eventId, stored.Revision, stored.Sequence)
            with _ ->
                LifecycleWriteOutcome.Unconfirmed eventId

    let replayApproval
        (witness: WitnessProtocol)
        approvalId
        operationId
        caseId
        draftHash
        approverId
        expiresAt
        revision
        sequence
        (stored: LifecycleStoredApproval)
        =
        let canonical =
            CaseLifecycleCandidate.approval
                approvalId
                operationId
                caseId
                draftHash
                approverId
                stored.GrantRevision
                stored.ApprovedAt
                expiresAt

        let exact = canonical = stored.Canonical
        CryptographicOperations.ZeroMemory(canonical)

        if
            stored.OperationId <> operationId
            || stored.CaseId <> caseId
            || stored.DraftHash <> draftHash
            || stored.ApproverId <> approverId
            || stored.ExpiresAt <> expiresAt
            || not exact
        then
            LifecycleWriteOutcome.ResourceUnavailable
        else
            try
                witness.ReconcileAuthority(
                    approvalId,
                    stored.WitnessSequence,
                    stored.WitnessEpoch,
                    stored.WitnessHash,
                    stored.Canonical
                )

                LifecycleWriteOutcome.Applied(approvalId, revision, sequence)
            with _ ->
                LifecycleWriteOutcome.Unconfirmed approvalId

    let beginTransaction (connection: NpgsqlConnection) =
        connection.BeginTransaction(IsolationLevel.ReadCommitted)

    let clear (bytes: byte array) =
        CryptographicOperations.ZeroMemory(bytes)
