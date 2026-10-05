namespace ClaimCore.Postgres

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain

/// Owner preflight holds the global authority barrier; no witness purge intent is emitted here.
module internal CaseErasurePurgePreparation =
    let private invalid () : 'a =
        raise (InvalidDataException("Owner purge preflight differs."))

    let private refusal =
        function
        | LifecycleRefusal.HoldActive -> OwnerPurgeRefusal.HoldActive
        | LifecycleRefusal.ApprovalRequired
        | LifecycleRefusal.ApprovalExpired
        | LifecycleRefusal.ApprovalCapacityExceeded -> OwnerPurgeRefusal.ApprovalsIncomplete
        | LifecycleRefusal.WrongPrivacyPhase
        | LifecycleRefusal.ErasureHasBegun -> OwnerPurgeRefusal.ErasureNotPending
        | _ -> OwnerPurgeRefusal.ProposalMismatch

    let audit ownerConnectionString (witness: WitnessProtocol) commitments (ct: CancellationToken) =
        task {
            let builder = OwnerConnection.builder ownerConnectionString
            use connection = new NpgsqlConnection(builder.ConnectionString)
            do! connection.OpenAsync(ct)
            OwnerConnection.requireIdentity connection
            SchemaBaseline.requireCurrent connection
            DatabaseEnvironment.requireCompatible connection
            let! summary = DataAudit.runWithSuppression connection witness (Some commitments) ct
            let! tip = witness.Snapshot(ct)

            if summary.WitnessCutoff <> tip.TipSequence then
                invalid ()

            return tip
        }

    let private requestCommitment
        connection
        transaction
        (commitments: ISuppressionCommitments)
        eventId
        caseId
        =
        task {
            let! found =
                CaseLifecycleAuditRows.eventById
                    connection
                    transaction
                    eventId
                    CancellationToken.None

            let value = found |> Option.defaultWith invalid

            if
                value.CaseId <> caseId
                || value.ActionName <> "REQUEST_ERASURE"
                || SHA256.HashData(value.Canonical) <> value.CandidateHash
            then
                invalid ()

            let keyed = commitments.RequestCandidate value.Canonical

            if keyed.Length <> 32 then
                invalid ()

            return keyed
        }

    let private authorized
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        (draftHash: byte array)
        (selected: LifecycleApprovalEvidence list)
        instant
        requestEventId
        receipts
        =
        match
            CaseLifecycleDecisions.authorizeOwnerPurge
                projection.CaseId
                projection.State
                change
                (Convert.ToHexStringLower draftHash)
                selected
                instant
        with
        | Ok() -> Ok(requestEventId, receipts)
        | Error decision -> Error(OwnerPurgeOutcome.Refused(refusal decision))

    let private keyedEvidence
        connection
        transaction
        (commitments: ISuppressionCommitments)
        (projection: LifecycleProjection)
        (draft: byte array)
        requestEventId
        receipts
        copy
        =
        task {
            let! requestKeyed =
                requestCommitment
                    connection
                    transaction
                    commitments
                    requestEventId
                    projection.CaseId

            let proposalKeyed = commitments.PurgeProposal draft

            if proposalKeyed.Length <> 32 then
                invalid ()

            return
                Ok
                    {
                        RequestEventId = requestEventId
                        RequestCommitment = requestKeyed
                        ProposalCommitment = proposalKeyed
                        Approvals = receipts
                        CopySeal = copy
                    }
        }

    let private proposalRefusal (projection: LifecycleProjection) (change: LifecycleChange) =
        if not (CaseLifecycle.holds projection.State |> List.isEmpty) then
            Some(OwnerPurgeOutcome.Refused OwnerPurgeRefusal.HoldActive)
        elif not (CaseLifecycleStoreSupport.matchesProjection change projection) then
            Some(OwnerPurgeOutcome.Refused OwnerPurgeRefusal.ProposalMismatch)
        else
            None

    let private approved
        connection
        transaction
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments)
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        (draft: byte array)
        instant
        ct
        =
        task {
            match proposalRefusal projection change with
            | Some refusal -> return Error refusal
            | None ->
                let! request =
                    CaseErasureRequestProof.read
                        connection
                        transaction
                        witness
                        commitments
                        projection.CaseId
                        change.CaseReference
                        ct

                match request with
                | None -> return Error(OwnerPurgeOutcome.AuditUnavailable "REQUEST_FENCE")
                | Some(requestEventId, _) ->
                    let draftHash = SHA256.HashData(draft)

                    let! selected, receipts =
                        CaseErasurePurgeApprovalRead.read
                            connection
                            transaction
                            witness
                            projection
                            change
                            draftHash
                            draft
                            ct
                            commitments
                            instant

                    return
                        authorized
                            projection
                            change
                            draftHash
                            selected
                            instant
                            requestEventId
                            receipts
        }

    let private inventorySeal
        connection
        transaction
        witness
        (inventory: IManagedCopyErasureClearance)
        (projection: LifecycleProjection)
        (tip: ClaimCore.Witness.Snapshot)
        ct
        =
        inventory.RequireCompleteInventory(
            connection,
            transaction,
            witness,
            projection.CaseId,
            tip.TipSequence,
            tip.TipHash,
            ct
        )

    let prepare
        connection
        transaction
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments)
        (inventory: IManagedCopyErasureClearance)
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        (draft: byte array)
        (tip: ClaimCore.Witness.Snapshot)
        instant
        ct
        =
        task {
            let! ready =
                approved
                    connection
                    transaction
                    witness
                    commitments
                    projection
                    change
                    draft
                    instant
                    ct

            match ready with
            | Error outcome -> return Error outcome
            | Ok(requestEventId, receipts) ->
                let! copySeal =
                    inventorySeal connection transaction witness inventory projection tip ct

                match copySeal with
                | None -> return Error OwnerPurgeOutcome.InventoryUnknown
                | Some copy ->
                    return!
                        keyedEvidence
                            connection
                            transaction
                            commitments
                            projection
                            draft
                            requestEventId
                            receipts
                            copy
        }
