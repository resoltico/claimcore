namespace ClaimCore.Postgres

open System
open System.Threading
open System.Data
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness
open WitnessProtocolReconciliation

/// Actor-bound HUMAN OWNER adoption approval. It only witnesses a draft; the later owner
/// command verifies signed custody/provenance and consumes this approval atomically.
module internal ManagedCopyAdoptionApprovalWrite =
    let private requireCaseIntent
        (witness: WitnessProtocol)
        (request: CopyAdoptionApprovalRequest)
        ct
        =
        task {
            let! retained =
                witness.EvidenceStore.TryReadEvidence(
                    request.ApprovalId,
                    ClaimCore.Witness.Intent,
                    ct
                )

            let intent = retained |> Option.defaultWith (fun () -> raise WitnessPending)

            if
                intent.Ticket.ScopeKind <> ClaimCore.Witness.Case
                || intent.Ticket.SubjectCaseId <> Some request.CaseId
            then
                raise WitnessPending

        }

    let private replay
        (witness: WitnessProtocol)
        (request: CopyAdoptionApprovalRequest)
        (prior: StoredAdoptionApproval)
        canonical
        ct
        =
        task {
            if
                prior.CaseId <> request.CaseId
                || prior.CopyId <> request.CopyId
                || prior.AdoptionEventId <> request.AdoptionEventId
                || prior.Canonical <> canonical
                || prior.CandidateHash <> SHA256.HashData(canonical)
            then
                return CopyAdoptionApprovalOutcome.ResourceUnavailable
            else
                try
                    do! requireCaseIntent witness request ct

                    do!
                        witness.ReconcileAuthority(
                            request.ApprovalId,
                            prior.WitnessSequence,
                            prior.WitnessEpoch,
                            prior.WitnessHash,
                            canonical,
                            ct
                        )

                    do!
                        witness.VerifyAuthorityEvidenceForCase(
                            request.ApprovalId,
                            prior.WitnessSequence,
                            prior.WitnessEpoch,
                            prior.WitnessHash,
                            prior.CandidateHash,
                            request.CaseId,
                            CancellationToken.None
                        )

                    return
                        CopyAdoptionApprovalOutcome.Approved(
                            request.ApprovalId,
                            prior.GrantRevision
                        )
                with _ ->
                    return CopyAdoptionApprovalOutcome.StartedUnconfirmed request.ApprovalId
        }

    let private commit
        connection
        transaction
        (witness: WitnessProtocol)
        context
        (request: CopyAdoptionApprovalRequest)
        now
        canonical
        ct
        =
        task {
            try
                let! intent =
                    witness.BeginAuthority(request.ApprovalId, canonical, Some request.CaseId, ct)

                do!
                    ManagedCopyAdoptionApprovalPersistence.persist
                        connection
                        transaction
                        request
                        context
                        now
                        canonical
                        intent

                do! transaction.CommitAsync()

                try
                    let! _ = witness.SettleAuthority(request.ApprovalId, intent)

                    do!
                        witness.VerifyAuthorityEvidenceForCase(
                            request.ApprovalId,
                            intent.Ticket.Sequence,
                            intent.Ticket.Epoch,
                            intent.Ticket.EntryHash,
                            intent.CandidateHash,
                            request.CaseId,
                            CancellationToken.None
                        )

                    return
                        CopyAdoptionApprovalOutcome.Approved(
                            request.ApprovalId,
                            context.Binding.GrantRevision
                        )
                with _ ->
                    return CopyAdoptionApprovalOutcome.StartedUnconfirmed request.ApprovalId
            with _ ->
                return CopyAdoptionApprovalOutcome.StartedUnconfirmed request.ApprovalId
        }

    let private fresh
        connection
        transaction
        witness
        context
        (request: CopyAdoptionApprovalRequest)
        (stored: StoredCaseTombstone)
        ct
        =
        task {
            let! holds = CaseTombstoneRead.activeHolds connection transaction request.CaseId

            let! source =
                ManagedCopyAdoptionApprovalPolicy.source connection transaction witness request ct

            let! signers =
                ManagedCopyAdoptionApprovalSigners.valid connection transaction request

            let! slot =
                ManagedCopyAdoptionApprovalRead.eventAvailable
                    connection
                    transaction
                    request.AdoptionEventId

            let! now = Sql.databaseNow connection transaction ct

            let! historical =
                ManagedCopyAdoptionApprovalPolicy.historical witness stored request ct

            if
                stored.Phase <> "ERASURE_PENDING"
                || not holds.IsEmpty
                || not source
                || not signers
                || not slot
                || not historical
                || request.ExpiresAt <= now
                || request.ExpiresAt > now.AddHours(24.0)
            then
                return CopyAdoptionApprovalOutcome.ResourceUnavailable
            else
                let canonical =
                    ManagedCopyAdoptionApprovalCandidate.canonical
                        request
                        context.Binding.ActorId
                        context.Binding.GrantRevision
                        now

                try
                    return! commit connection transaction witness context request now canonical ct
                finally
                    CryptographicOperations.ZeroMemory(canonical)
        }

    let private afterAuthorization
        connection
        transaction
        witness
        (context: ActorCallContext)
        (request: CopyAdoptionApprovalRequest)
        stored
        ct
        =
        task {
            let! prior =
                ManagedCopyAdoptionApprovalRead.find connection transaction request.ApprovalId

            match prior with
            | Some receipt ->
                let canonical =
                    ManagedCopyAdoptionApprovalCandidate.canonical
                        request
                        context.Binding.ActorId
                        context.Binding.GrantRevision
                        receipt.ApprovedAt

                try
                    return! replay witness request receipt canonical ct
                finally
                    CryptographicOperations.ZeroMemory(canonical)
            | None -> return! fresh connection transaction witness context request stored ct
        }

    let private underLock
        connection
        transaction
        witness
        (context: ActorCallContext)
        (request: CopyAdoptionApprovalRequest)
        ct
        =
        task {
            let! revision = ActorGrantRead.lockRevision connection transaction true ct

            let! found = CaseTombstoneRead.lock connection transaction request.CaseId

            match found with
            | None -> return CopyAdoptionApprovalOutcome.ResourceUnavailable
            | Some stored ->
                let! actor =
                    ActorGrantRead.loadUnderLock
                        connection
                        transaction
                        context.Binding.Principal
                        (ResourceScope.Case request.CaseId)
                        revision
                        ct

                match actor with
                | None -> return CopyAdoptionApprovalOutcome.ResourceUnavailable
                | Some live when
                    not (ManagedCopyAdoptionApprovalPolicy.authorized context live request.CaseId)
                    ->
                    return CopyAdoptionApprovalOutcome.ResourceUnavailable
                | Some _ ->
                    return!
                        afterAuthorization connection transaction witness context request stored ct
        }

    let approve
        dataSource
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (request: CopyAdoptionApprovalRequest)
        ct
        =
        task {
            if not (ManagedCopyAdoptionApprovalPolicy.valid context request) then
                return CopyAdoptionApprovalOutcome.ResourceUnavailable
            else
                try
                    do! witness.Admit(ct)

                    use! connection =
                        RuntimeDatabase.openConnectionAsyncWithCancellation dataSource ct

                    use! _authorityLease =
                        AuthorityOperationFence.acquireShared (Some dataSource) connection ct

                    use! transaction =
                        connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)

                    return! underLock connection transaction witness context request ct
                with _ ->
                    return CopyAdoptionApprovalOutcome.StartedUnconfirmed request.ApprovalId
        }
