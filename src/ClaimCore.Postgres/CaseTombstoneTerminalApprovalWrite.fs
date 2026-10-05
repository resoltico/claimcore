namespace ClaimCore.Postgres

open System
open System.Threading
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
        ct
        =
        task {
            let! retained = witness.EvidenceStore.TryReadEvidence(approvalId, Intent, ct)
            let intent = retained |> Option.defaultWith (fun () -> raise WitnessPending)

            if
                intent.Ticket.ScopeKind <> Case
                || intent.Ticket.SubjectCaseId <> Some prior.CaseId
            then
                raise WitnessPending

            do!
                witness.ReconcileAuthority(
                    approvalId,
                    prior.WitnessSequence,
                    prior.WitnessEpoch,
                    prior.WitnessHash,
                    canonical,
                    ct
                )

            do!
                witness.VerifyAuthorityEvidenceForCase(
                    approvalId,
                    prior.WitnessSequence,
                    prior.WitnessEpoch,
                    prior.WitnessHash,
                    prior.CandidateHash,
                    prior.CaseId,
                    CancellationToken.None
                )
        }

    let private replay
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        proposal
        approvalId
        expiresAt
        (prior: StoredTerminalApproval)
        ct
        =
        task {
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
                    return TombstoneWriteOutcome.Refused LifecycleRefusal.ApprovalMismatch
                else
                    try
                        do! reconcileCase witness approvalId prior canonical ct

                        let revision =
                            (TombstoneTerminalProposal.copy proposal).ExpectedAuthorityRevision

                        return TombstoneWriteOutcome.Applied(approvalId, revision)
                    with _ ->
                        return TombstoneWriteOutcome.Unconfirmed approvalId
            finally
                CryptographicOperations.ZeroMemory(canonical)
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
        ct
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
                    let! intent =
                        witness.BeginAuthority(approvalId, canonical, Some value.CaseId, ct)

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
        proposal
        approvalId
        expiresAt
        instant
        (stored: StoredTerminalTombstone)
        ct
        =
        task {
            let! prior = CaseTombstoneTerminalRead.findApproval connection transaction approvalId

            match prior with
            | Some receipt ->
                return! replay witness context proposal approvalId expiresAt receipt ct
            | None when not (CaseTombstoneTerminalPolicy.matches stored proposal) ->
                return TombstoneWriteOutcome.Refused LifecycleRefusal.VersionConflict
            | None when
                not (CaseTombstoneTerminalPolicy.valid proposal approvalId expiresAt instant)
                ->
                return TombstoneWriteOutcome.Refused LifecycleRefusal.InvalidTime
            | None ->
                let! available =
                    CaseTombstoneTerminalApprovalSet.available
                        connection
                        transaction
                        witness
                        context
                        proposal
                        ct

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
                            ct
        }

    let private apply dataSource witness context proposal approvalId expiresAt ct =
        task {
            let value = TombstoneTerminalProposal.copy proposal
            use! connection = RuntimeDatabase.openConnectionAsyncWithCancellation dataSource ct

            use! _authorityLease =
                AuthorityOperationFence.acquireShared (Some dataSource) connection ct

            use! transaction = connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)

            let! revision = ActorGrantRead.lockRevision connection transaction true ct

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
                    let! instant = Sql.databaseNow connection transaction ct

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
                            ct
        }

    let approve
        dataSource
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        proposal
        approvalId
        expiresAt
        ct
        =
        task {
            if
                context.Action <> EndpointAction.ApproveTerminalErasure
                || context.CaseId <> Some(TombstoneTerminalProposal.caseId proposal)
            then
                return TombstoneWriteOutcome.ResourceUnavailable
            else
                try
                    do! witness.Admit(ct)
                    return! apply dataSource witness context proposal approvalId expiresAt ct
                with
                | :? System.OperationCanceledException when ct.IsCancellationRequested ->
                    return TombstoneWriteOutcome.CancelledBeforeAdmission approvalId
                | :? InvalidDataException ->
                    return TombstoneWriteOutcome.Failed CoreFault.StoreIntegrityError
                | _ -> return TombstoneWriteOutcome.Failed CoreFault.StoreUnavailable
        }
