namespace ClaimCore.Postgres

open System
open System.Data
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness
open WitnessProtocolReconciliation

/// Actor-bound HUMAN OWNER adoption approval. It only witnesses a draft; the later owner
/// command verifies signed custody/provenance and consumes this approval atomically.
module internal ManagedCopyAdoptionApprovalWrite =
    let private authorized (context: ActorCallContext) (authority: ActorAuthority) caseId =
        let owner =
            authority.Grants
            |> List.exists (fun grant ->
                grant.Role = Role.Owner
                && (grant.Scope = GrantScope.Installation || grant.Scope = GrantScope.Case caseId))

        owner
        && (match
                ActorAuthorization.authorizeAtRevision
                    context.Binding.Principal
                    authority
                    context.Binding.GrantRevision
                    EndpointAction.ApproveCopyAdoption
                    (ResourceScope.Case caseId)
            with
            | AuthorizationDecision.Available(actorId, _) -> actorId = context.Binding.ActorId
            | _ -> false)

    let private replay
        (witness: WitnessProtocol)
        (request: CopyAdoptionApprovalRequest)
        (prior: StoredAdoptionApproval)
        canonical
        =
        if
            prior.CaseId <> request.CaseId
            || prior.CopyId <> request.CopyId
            || prior.AdoptionEventId <> request.AdoptionEventId
            || prior.Canonical <> canonical
            || prior.CandidateHash <> SHA256.HashData(canonical)
        then
            CopyAdoptionApprovalOutcome.ResourceUnavailable
        else
            try
                let intent =
                    witness.EvidenceStore.TryReadEvidence(
                        request.ApprovalId,
                        ClaimCore.Witness.Intent
                    )
                    |> Option.defaultWith (fun () -> raise WitnessPending)

                if
                    intent.Ticket.ScopeKind <> ClaimCore.Witness.Case
                    || intent.Ticket.SubjectCaseId <> Some request.CaseId
                then
                    raise WitnessPending

                witness.ReconcileAuthority(
                    request.ApprovalId,
                    prior.WitnessSequence,
                    prior.WitnessEpoch,
                    prior.WitnessHash,
                    canonical
                )

                witness.VerifyAuthorityEvidenceForCase(
                    request.ApprovalId,
                    prior.WitnessSequence,
                    prior.WitnessEpoch,
                    prior.WitnessHash,
                    prior.CandidateHash,
                    request.CaseId
                )

                CopyAdoptionApprovalOutcome.Approved(request.ApprovalId, prior.GrantRevision)
            with _ ->
                CopyAdoptionApprovalOutcome.StartedUnconfirmed request.ApprovalId

    let private commit
        connection
        transaction
        (witness: WitnessProtocol)
        context
        (request: CopyAdoptionApprovalRequest)
        now
        canonical
        =
        task {
            try
                let intent =
                    witness.BeginAuthority(request.ApprovalId, canonical, Some request.CaseId)

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
                    witness.SettleAuthority(request.ApprovalId, intent) |> ignore

                    witness.VerifyAuthorityEvidenceForCase(
                        request.ApprovalId,
                        intent.Ticket.Sequence,
                        intent.Ticket.Epoch,
                        intent.Ticket.EntryHash,
                        intent.CandidateHash,
                        request.CaseId
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
        =
        task {
            let! holds = CaseTombstoneRead.activeHolds connection transaction request.CaseId

            let! source =
                ManagedCopyAdoptionApprovalPolicy.source connection transaction witness request

            let! signers =
                ManagedCopyAdoptionApprovalSigners.valid connection transaction request

            let! slot =
                ManagedCopyAdoptionApprovalRead.eventAvailable
                    connection
                    transaction
                    request.AdoptionEventId

            let! now = Sql.databaseNow connection transaction

            if
                stored.Phase <> "ERASURE_PENDING"
                || not holds.IsEmpty
                || not source
                || not signers
                || not slot
                || not (ManagedCopyAdoptionApprovalPolicy.historical witness stored request)
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
                    return! commit connection transaction witness context request now canonical
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
                    return replay witness request receipt canonical
                finally
                    CryptographicOperations.ZeroMemory(canonical)
            | None -> return! fresh connection transaction witness context request stored
        }

    let private underLock
        connection
        transaction
        witness
        (context: ActorCallContext)
        (request: CopyAdoptionApprovalRequest)
        =
        task {
            let! revision =
                ActorGrantRead.lockRevision
                    connection
                    transaction
                    true
                    Threading.CancellationToken.None

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
                        Threading.CancellationToken.None

                match actor with
                | None -> return CopyAdoptionApprovalOutcome.ResourceUnavailable
                | Some live when not (authorized context live request.CaseId) ->
                    return CopyAdoptionApprovalOutcome.ResourceUnavailable
                | Some _ ->
                    return! afterAuthorization connection transaction witness context request stored
        }

    let approve
        dataSource
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (request: CopyAdoptionApprovalRequest)
        =
        task {
            if not (ManagedCopyAdoptionApprovalPolicy.valid context request) then
                return CopyAdoptionApprovalOutcome.ResourceUnavailable
            else
                try
                    witness.Admit()
                    use! connection = RuntimeDatabase.openConnectionAsync dataSource

                    use! _authorityLease =
                        AuthorityOperationFence.acquireShared
                            (Some dataSource)
                            connection
                            System.Threading.CancellationToken.None

                    use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)
                    return! underLock connection transaction witness context request
                with _ ->
                    return CopyAdoptionApprovalOutcome.StartedUnconfirmed request.ApprovalId
        }
