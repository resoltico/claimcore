namespace ClaimCore.Postgres

open System
open System.Threading
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
        ct
        =
        task {
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
                    return TombstoneWriteOutcome.Refused LifecycleRefusal.ApprovalMismatch
                else
                    try
                        do!
                            witness.ReconcileAuthority(
                                approvalId,
                                prior.WitnessSequence,
                                prior.WitnessEpoch,
                                prior.WitnessHash,
                                canonical,
                                ct
                            )

                        return
                            TombstoneWriteOutcome.Applied(
                                approvalId,
                                value.ExpectedAuthorityRevision
                            )
                    with _ ->
                        return TombstoneWriteOutcome.Unconfirmed approvalId
            finally
                CryptographicOperations.ZeroMemory(canonical)
        }

    let private slotDecision
        connection
        transaction
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (value: TombstonePruneProposal)
        ct
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
            else
                let! matches = targetMatches witness value ct

                return
                    if matches then
                        Ok()
                    else
                        Error LifecycleRefusal.ErasureEvidenceIncomplete
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
        ct
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
                    let! intent =
                        witness.BeginAuthority(approvalId, canonical, Some value.CaseId, ct)

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
                        let! _ = witness.SettleAuthority(approvalId, intent)
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
        ct
        =
        task {
            let! prior = CaseTombstonePruneApprovalRead.find connection transaction approvalId

            match prior with
            | Some receipt -> return! replay witness context value approvalId expiresAt receipt ct
            | None when not (matches stored value) ->
                return TombstoneWriteOutcome.Refused LifecycleRefusal.VersionConflict
            | None when not (valid value approvalId expiresAt instant) ->
                return TombstoneWriteOutcome.Refused LifecycleRefusal.InvalidTime
            | None ->
                let! slots = slotDecision connection transaction witness context value ct

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
                            ct
        }

    let private apply
        dataSource
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (value: TombstonePruneProposal)
        (approvalId: Guid)
        (expiresAt: DateTimeOffset)
        ct
        =
        task {
            use! connection = RuntimeDatabase.openConnectionAsyncWithCancellation dataSource ct

            use! _authorityLease =
                AuthorityOperationFence.acquireShared (Some dataSource) connection ct

            use! transaction = connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)

            let! revision = ActorGrantRead.lockRevision connection transaction true ct

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
                    let! instant = Sql.databaseNow connection transaction ct

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
                            ct
        }

    let approve
        dataSource
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (value: TombstonePruneProposal)
        (approvalId: Guid)
        (expiresAt: DateTimeOffset)
        ct
        =
        task {
            if
                context.Action <> EndpointAction.ApproveWitnessPrune
                || context.CaseId <> Some value.CaseId
            then
                return TombstoneWriteOutcome.ResourceUnavailable
            else
                try
                    do! witness.Admit(ct)
                    return! apply dataSource witness context value approvalId expiresAt ct
                with
                | :? System.OperationCanceledException when ct.IsCancellationRequested ->
                    return TombstoneWriteOutcome.CancelledBeforeAdmission approvalId
                | :? InvalidDataException ->
                    return TombstoneWriteOutcome.Failed CoreFault.StoreIntegrityError
                | _ -> return TombstoneWriteOutcome.Failed CoreFault.StoreUnavailable
        }
