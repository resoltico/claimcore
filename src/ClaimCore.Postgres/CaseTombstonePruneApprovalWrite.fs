namespace ClaimCore.Postgres

open System
open System.Data
open System.IO
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open WitnessProtocolReconciliation
open CaseTombstonePruneApprovalPolicy

module internal CaseTombstonePruneApprovalWrite =
    let private replay
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (value: TombstonePruneProposal)
        (approvalId: Guid)
        (expiresAt: DateTimeOffset)
        (prior: StoredPruneApproval)
        =
        let canonical =
            CaseTombstoneCandidate.approval
                value
                approvalId
                prior.ActorId
                prior.GrantRevision
                prior.ApprovedAt
                prior.ExpiresAt

        try
            if
                prior.CaseId <> value.CaseId
                || prior.PruneEventId <> value.EventId
                || prior.ActorId <> context.Binding.ActorId
                || prior.ExpiresAt <> expiresAt
                || prior.Canonical <> canonical
                || prior.CandidateHash <> SHA256.HashData(canonical)
            then
                TombstoneWriteOutcome.Refused LifecycleRefusal.ApprovalMismatch
            else
                try
                    witness.ReconcileAuthority(
                        approvalId,
                        prior.WitnessSequence,
                        prior.WitnessEpoch,
                        prior.WitnessHash,
                        canonical
                    )

                    TombstoneWriteOutcome.Applied(approvalId, value.ExpectedAuthorityRevision)
                with _ ->
                    TombstoneWriteOutcome.Unconfirmed approvalId
        finally
            CryptographicOperations.ZeroMemory(canonical)

    let private slotDecision
        connection
        transaction
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (value: TombstonePruneProposal)
        =
        task {
            let! holds = CaseTombstoneRead.activeHolds connection transaction value.CaseId

            let! approvers =
                CaseTombstonePruneApprovalRead.approvers connection transaction value.EventId

            if not holds.IsEmpty then
                return Error LifecycleRefusal.HoldActive
            elif approvers |> List.contains context.Binding.ActorId then
                return Error LifecycleRefusal.ApprovalMismatch
            elif approvers.Length >= 2 then
                return Error LifecycleRefusal.ApprovalCapacityExceeded
            elif not (targetMatches witness value) then
                return Error LifecycleRefusal.ErasureEvidenceIncomplete
            else
                return Ok()
        }

    let private commitApproval
        connection
        transaction
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (value: TombstonePruneProposal)
        approvalId
        expiresAt
        instant
        authorityRevision
        =
        task {
            do! CaseErasurePurgeDelete.verifyAbsent connection transaction value.CaseId

            let canonical =
                CaseTombstoneCandidate.approval
                    value
                    approvalId
                    context.Binding.ActorId
                    context.Binding.GrantRevision
                    instant
                    expiresAt

            try
                try
                    let intent = witness.BeginAuthority(approvalId, canonical, Some value.CaseId)

                    do!
                        CaseTombstonePruneApprovalPersistence.persist
                            connection
                            transaction
                            context
                            value
                            approvalId
                            expiresAt
                            instant
                            canonical
                            intent

                    do! transaction.CommitAsync()

                    try
                        witness.SettleAuthority(approvalId, intent) |> ignore
                        return TombstoneWriteOutcome.Applied(approvalId, authorityRevision)
                    with _ ->
                        return TombstoneWriteOutcome.Unconfirmed approvalId
                with _ ->
                    return TombstoneWriteOutcome.Unconfirmed approvalId
            finally
                CryptographicOperations.ZeroMemory(canonical)
        }

    let private afterAuthorization
        connection
        transaction
        witness
        context
        value
        approvalId
        expiresAt
        instant
        (stored: StoredCaseTombstone)
        =
        task {
            let! prior = CaseTombstonePruneApprovalRead.find connection transaction approvalId

            match prior with
            | Some receipt -> return replay witness context value approvalId expiresAt receipt
            | None when not (matches stored value) ->
                return TombstoneWriteOutcome.Refused LifecycleRefusal.VersionConflict
            | None ->
                let! slots = slotDecision connection transaction witness context value

                match slots with
                | Error refusal -> return TombstoneWriteOutcome.Refused refusal
                | Ok() ->
                    return!
                        commitApproval
                            connection
                            transaction
                            witness
                            context
                            value
                            approvalId
                            expiresAt
                            instant
                            stored.AuthorityRevision
        }

    let private apply
        dataSource
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (value: TombstonePruneProposal)
        (approvalId: Guid)
        (expiresAt: DateTimeOffset)
        (instant: DateTimeOffset)
        =
        task {
            use! connection = RuntimeDatabase.openConnectionAsync dataSource

            use! _authorityLease =
                AuthorityOperationFence.acquireShared
                    (Some dataSource)
                    connection
                    System.Threading.CancellationToken.None

            use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)

            let! revision =
                ActorGrantRead.lockRevision
                    connection
                    transaction
                    true
                    Threading.CancellationToken.None

            let! found = CaseTombstoneRead.lock connection transaction value.CaseId

            match found with
            | None -> return TombstoneWriteOutcome.ResourceUnavailable
            | Some stored ->
                let! allowed =
                    ActorMutationGuard.authorizeScope
                        connection
                        transaction
                        context
                        (ResourceScope.Case value.CaseId)
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
                            value
                            approvalId
                            expiresAt
                            instant
                            stored
        }

    let approve
        dataSource
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (value: TombstonePruneProposal)
        (approvalId: Guid)
        (expiresAt: DateTimeOffset)
        (instant: DateTimeOffset)
        =
        task {
            let utcInstant = instant.Offset = TimeSpan.Zero
            let instant = CaseLifecycleStoreSupport.microsecondInstant instant

            if
                context.Action <> EndpointAction.ApproveWitnessPrune
                || context.CaseId <> Some value.CaseId
            then
                return TombstoneWriteOutcome.ResourceUnavailable
            elif not utcInstant || not (valid value approvalId expiresAt instant) then
                return TombstoneWriteOutcome.Refused LifecycleRefusal.InvalidTime
            else
                try
                    witness.Admit()
                    return! apply dataSource witness context value approvalId expiresAt instant
                with
                | :? InvalidDataException ->
                    return TombstoneWriteOutcome.Failed CoreFault.StoreIntegrityError
                | _ -> return TombstoneWriteOutcome.Failed CoreFault.StoreUnavailable
        }
