namespace ClaimCore.Postgres

open System
open System.Data
open System.IO
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Witness
open WitnessProtocolReconciliation

module internal CaseTombstoneTerminalApprovalWrite =
    let private reconcileCase
        (witness: WitnessProtocol)
        approvalId
        (prior: StoredTerminalApproval)
        canonical
        =
        let intent =
            witness.EvidenceStore.TryReadEvidence(approvalId, Intent)
            |> Option.defaultWith (fun () -> raise WitnessPending)

        if
            intent.Ticket.ScopeKind <> Case
            || intent.Ticket.SubjectCaseId <> Some prior.CaseId
        then
            raise WitnessPending

        witness.ReconcileAuthority(
            approvalId,
            prior.WitnessSequence,
            prior.WitnessEpoch,
            prior.WitnessHash,
            canonical
        )

        witness.VerifyAuthorityEvidenceForCase(
            approvalId,
            prior.WitnessSequence,
            prior.WitnessEpoch,
            prior.WitnessHash,
            prior.CandidateHash,
            prior.CaseId
        )

    let private replay
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        proposal
        approvalId
        expiresAt
        (prior: StoredTerminalApproval)
        =
        let canonical =
            CaseTombstoneTerminalCandidate.approval
                proposal
                approvalId
                prior.ActorId
                prior.GrantRevision
                prior.ApprovedAt
                prior.ExpiresAt

        try
            if
                prior.CaseId <> TombstoneTerminalProposal.caseId proposal
                || prior.TerminalEventId <> TombstoneTerminalProposal.eventId proposal
                || prior.ActorId <> context.Binding.ActorId
                || prior.ExpiresAt <> expiresAt
                || prior.Canonical <> canonical
                || prior.CandidateHash <> SHA256.HashData(canonical)
            then
                TombstoneWriteOutcome.Refused LifecycleRefusal.ApprovalMismatch
            else
                try
                    reconcileCase witness approvalId prior canonical

                    let revision =
                        (TombstoneTerminalProposal.copy proposal).ExpectedAuthorityRevision

                    TombstoneWriteOutcome.Applied(approvalId, revision)
                with _ ->
                    TombstoneWriteOutcome.Unconfirmed approvalId
        finally
            CryptographicOperations.ZeroMemory(canonical)

    let private slots
        connection
        transaction
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        proposal
        =
        task {
            let value = TombstoneTerminalProposal.copy proposal
            let! holds = CaseTombstoneRead.activeHolds connection transaction value.CaseId

            let! approvers =
                CaseTombstoneTerminalRead.approvers connection transaction value.EventId

            let! sameDraft =
                CaseTombstoneTerminalApprovalSet.matches connection transaction witness proposal

            if not holds.IsEmpty then
                return Error LifecycleRefusal.HoldActive
            elif not sameDraft then
                return Error LifecycleRefusal.ApprovalMismatch
            elif approvers |> List.contains context.Binding.ActorId then
                return Error LifecycleRefusal.ApprovalMismatch
            elif approvers.Length >= 2 then
                return Error LifecycleRefusal.ApprovalCapacityExceeded
            else
                witness.RequireSettled(value.PruneEventId, ClaimCore.Witness.SettledAuthority)
                return Ok()
        }

    let private commit
        connection
        transaction
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        proposal
        approvalId
        expiresAt
        instant
        authorityRevision
        =
        task {
            let value = TombstoneTerminalProposal.copy proposal
            do! CaseErasurePurgeDelete.verifyAbsent connection transaction value.CaseId

            let canonical =
                CaseTombstoneTerminalCandidate.approval
                    proposal
                    approvalId
                    context.Binding.ActorId
                    context.Binding.GrantRevision
                    instant
                    expiresAt

            try
                try
                    let intent = witness.BeginAuthority(approvalId, canonical, Some value.CaseId)

                    do!
                        CaseTombstoneTerminalApprovalPersistence.persist
                            connection
                            transaction
                            context
                            proposal
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
        proposal
        approvalId
        expiresAt
        instant
        (stored: StoredTerminalTombstone)
        =
        task {
            let! prior = CaseTombstoneTerminalRead.findApproval connection transaction approvalId

            match prior with
            | Some receipt -> return replay witness context proposal approvalId expiresAt receipt
            | None when not (CaseTombstoneTerminalPolicy.matches stored proposal) ->
                return TombstoneWriteOutcome.Refused LifecycleRefusal.VersionConflict
            | None ->
                let! available = slots connection transaction witness context proposal

                match available with
                | Error refusal -> return TombstoneWriteOutcome.Refused refusal
                | Ok() ->
                    return!
                        commit
                            connection
                            transaction
                            witness
                            context
                            proposal
                            approvalId
                            expiresAt
                            instant
                            stored.AuthorityRevision
        }

    let private apply dataSource witness context proposal approvalId expiresAt instant =
        task {
            let value = TombstoneTerminalProposal.copy proposal
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

            let! found = CaseTombstoneTerminalRead.lock connection transaction value.CaseId

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
                            proposal
                            approvalId
                            expiresAt
                            instant
                            stored
        }

    let approve
        dataSource
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        proposal
        approvalId
        expiresAt
        (instant: DateTimeOffset)
        =
        task {
            let utcInstant = instant.Offset = TimeSpan.Zero
            let instant = CaseLifecycleStoreSupport.microsecondInstant instant

            if
                context.Action <> EndpointAction.ApproveTerminalErasure
                || context.CaseId <> Some(TombstoneTerminalProposal.caseId proposal)
            then
                return TombstoneWriteOutcome.ResourceUnavailable
            elif
                not utcInstant
                || not (CaseTombstoneTerminalPolicy.valid proposal approvalId expiresAt instant)
            then
                return TombstoneWriteOutcome.Refused LifecycleRefusal.InvalidTime
            else
                try
                    witness.Admit()
                    return! apply dataSource witness context proposal approvalId expiresAt instant
                with
                | :? InvalidDataException ->
                    return TombstoneWriteOutcome.Failed CoreFault.StoreIntegrityError
                | _ -> return TombstoneWriteOutcome.Failed CoreFault.StoreUnavailable
        }
