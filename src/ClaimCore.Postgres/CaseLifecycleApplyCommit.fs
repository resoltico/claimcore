namespace ClaimCore.Postgres

open System.Security.Cryptography
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open WitnessProtocolReconciliation

type internal LifecycleEventCandidate =
    {
        Draft: byte array
        Canonical: byte array
        Snapshot: byte array option
        CandidateHash: byte array
        EventHash: byte array
    }

module internal CaseLifecycleApplyCommit =
    let private build
        (context: ActorCallContext)
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        (decision: LifecycleDecisionResult)
        (approvals: LifecycleApprovalEvidence list)
        instant
        =
        let snapshot =
            decision.Snapshot |> Option.map ClaimCore.RecordFormat.CaseRecord.encodeSnapshot

        let businessRevision =
            decision.Snapshot
            |> Option.map _.Version
            |> Option.defaultValue (Claim.view projection.Claim).Version

        let draft = CaseLifecycleCandidate.draft projection.CaseId change

        let canonical =
            CaseLifecycleCandidate.event
                draft
                businessRevision
                (projection.Sequence + 1L)
                projection.EventHash
                (CaseLifecycle.disposition decision.State)
                (CaseLifecycle.privacy decision.State)
                instant
                context.Binding.ActorId
                context.Binding.GrantRevision
                (approvals |> List.map _.ApprovalId)
                snapshot

        let candidateHash = SHA256.HashData(canonical)

        {
            Draft = draft
            Canonical = canonical
            Snapshot = snapshot
            CandidateHash = candidateHash
            EventHash = CaseLifecycleCandidate.eventHash projection.EventHash candidateHash
        }

    let private clear (candidate: LifecycleEventCandidate) =
        CaseLifecycleStoreSupport.clear candidate.Draft
        CaseLifecycleStoreSupport.clear candidate.Canonical
        candidate.Snapshot |> Option.iter CaseLifecycleStoreSupport.clear

    let private erasureDenials connection transaction caseId commitments action =
        match action with
        | LifecycleMutation.RequestErasure _ ->
            task {
                let! count, digest =
                    CaseErasureDenials.scan
                        connection
                        transaction
                        caseId
                        commitments
                        false
                        System.Threading.CancellationToken.None

                return Some(count, digest)
            }
        | _ -> System.Threading.Tasks.Task.FromResult None

    let private persistAndSettle
        connection
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        commitments
        context
        projection
        (change: LifecycleChange)
        draftHash
        decision
        instant
        denials
        (candidate: LifecycleEventCandidate)
        intent
        =
        task {
            try
                let! revision, sequence =
                    CaseLifecycleWrite.persistEvent
                        connection
                        transaction
                        projection
                        change
                        decision
                        draftHash
                        candidate.Canonical
                        candidate.CandidateHash
                        candidate.EventHash
                        intent
                        context
                        instant
                        commitments
                        denials

                do! transaction.CommitAsync()

                witness.ReconcileAuthority(
                    change.EventId,
                    intent.Ticket.Sequence,
                    intent.Ticket.Epoch,
                    intent.Ticket.EntryHash,
                    candidate.Canonical
                )

                return LifecycleWriteOutcome.Applied(change.EventId, revision, sequence)
            with _ ->
                return LifecycleWriteOutcome.Unconfirmed change.EventId
        }

    let emit
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments)
        (context: ActorCallContext)
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        draftHash
        (decision: LifecycleDecisionResult)
        approvals
        instant
        =
        task {
            let! denials =
                erasureDenials connection transaction projection.CaseId commitments change.Action

            let candidate = build context projection change decision approvals instant

            try
                let intent =
                    witness.BeginAuthority(
                        change.EventId,
                        candidate.Canonical,
                        Some projection.CaseId
                    )

                return!
                    persistAndSettle
                        connection
                        transaction
                        witness
                        commitments
                        context
                        projection
                        change
                        draftHash
                        decision
                        instant
                        denials
                        candidate
                        intent
            finally
                clear candidate
        }
