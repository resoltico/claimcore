namespace ClaimCore.Postgres

open System
open System.Data
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open WitnessProtocolReconciliation

/// Actor-bound installation approval; owner activation later consumes two distinct witnessed rows.
module internal RealDataActivationApproval =
    let private settled
        (witness: WitnessProtocol)
        (request: RealDataActivationApprovalRequest)
        (stored: RealDataActivationApprovalRow)
        canonical
        =
        try
            witness.VerifyAuthorityEvidenceForInstallation(
                request.ApprovalId,
                stored.WitnessSequence,
                stored.WitnessEpoch,
                stored.WitnessHash,
                stored.CandidateHash
            )
        with _ ->
            witness.ReconcileAuthority(
                request.ApprovalId,
                stored.WitnessSequence,
                stored.WitnessEpoch,
                stored.WitnessHash,
                canonical
            )

            witness.VerifyAuthorityEvidenceForInstallation(
                request.ApprovalId,
                stored.WitnessSequence,
                stored.WitnessEpoch,
                stored.WitnessHash,
                stored.CandidateHash
            )

    let private replay
        (witness: WitnessProtocol)
        (request: RealDataActivationApprovalRequest)
        actorId
        (stored: RealDataActivationApprovalRow)
        =
        let canonical =
            RealDataActivationApprovalCandidate.canonical
                request
                actorId
                stored.ApproverGrantRevision
                stored.ApprovedAt

        try
            if
                stored.ApproverActorId <> actorId
                || stored.Canonical <> canonical
                || stored.CandidateHash <> SHA256.HashData(canonical)
            then
                RealDataActivationApprovalOutcome.ResourceUnavailable
            else
                try
                    settled witness request stored canonical

                    RealDataActivationApprovalOutcome.Approved(
                        request.ApprovalId,
                        stored.ApproverGrantRevision
                    )
                with _ ->
                    RealDataActivationApprovalOutcome.StartedUnconfirmed request.ApprovalId
        finally
            CryptographicOperations.ZeroMemory(canonical)

    let private retain
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (request: RealDataActivationApprovalRequest)
        actorId
        revision
        approvedAt
        (intent: WitnessIntent)
        =
        task {
            let canonical =
                RealDataActivationApprovalCandidate.canonical request actorId revision approvedAt

            try
                try
                    do!
                        RealDataActivationApprovalRows.insert
                            connection
                            transaction
                            request
                            actorId
                            revision
                            approvedAt
                            canonical
                            intent

                    do! transaction.CommitAsync()
                    witness.SettleAuthority(request.ApprovalId, intent) |> ignore
                    return RealDataActivationApprovalOutcome.Approved(request.ApprovalId, revision)
                with _ ->
                    return RealDataActivationApprovalOutcome.StartedUnconfirmed request.ApprovalId
            finally
                CryptographicOperations.ZeroMemory(canonical)
        }

    let private beginAndRetain
        connection
        transaction
        (witness: WitnessProtocol)
        (request: RealDataActivationApprovalRequest)
        actorId
        revision
        approvedAt
        =
        task {
            let canonical =
                RealDataActivationApprovalCandidate.canonical request actorId revision approvedAt

            try
                try
                    let intent = witness.BeginAuthority(request.ApprovalId, canonical, None)

                    return!
                        retain
                            connection
                            transaction
                            witness
                            request
                            actorId
                            revision
                            approvedAt
                            intent
                with _ ->
                    return RealDataActivationApprovalOutcome.StartedUnconfirmed request.ApprovalId
            finally
                CryptographicOperations.ZeroMemory(canonical)
        }

    let private fresh
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (request: RealDataActivationApprovalRequest)
        actorId
        revision
        =
        task {
            let! now = ManagedCopySignerPolicy.databaseNow connection transaction
            let snapshot = witness.Snapshot()
            let! first = RealDataActivationApprovalRows.first connection transaction request
            let pending = RealDataActivationApprovalRecovery.tryRead witness request actorId

            let common =
                RealDataActivationApprovalChecks.baseEligible
                    witness
                    request
                    actorId
                    now
                    pending.IsNone
                    first
                    snapshot

            if not common then
                return RealDataActivationApprovalOutcome.ResourceUnavailable
            else
                match pending with
                | Some original when
                    RealDataActivationApprovalChecks.pendingValid request snapshot original now
                    ->
                    return!
                        retain
                            connection
                            transaction
                            witness
                            request
                            actorId
                            original.GrantRevision
                            original.ApprovedAt
                            original.Intent
                | Some _ -> return RealDataActivationApprovalOutcome.ResourceUnavailable
                | None when
                    RealDataActivationApprovalChecks.freshTip request snapshot
                    && request.ExpiresAt > now
                    && request.ExpiresAt <= now.AddHours(24.)
                    ->
                    return!
                        beginAndRetain connection transaction witness request actorId revision now
                | None -> return RealDataActivationApprovalOutcome.ResourceUnavailable
        }

    let private underLock
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (request: RealDataActivationApprovalRequest)
        =
        task {
            let! revision =
                ActorGrantRead.lockRevision connection transaction true CancellationToken.None

            let! authority =
                ActorGrantRead.loadUnderLock
                    connection
                    transaction
                    context.Binding.Principal
                    ResourceScope.Installation
                    revision
                    CancellationToken.None

            match authority with
            | Some live when
                RealDataActivationOwnerPolicy.current
                    context
                    live
                    revision
                    EndpointAction.ApproveRealDataActivation
                ->
                let! published =
                    InstallationUsePlanRead.verified
                        connection
                        transaction
                        witness
                        request.PlanId
                        CancellationToken.None

                match published with
                | Some plan when RealDataActivationApprovalChecks.matchesPublished request plan ->
                    let! stored =
                        RealDataActivationApprovalRows.prior
                            connection
                            transaction
                            request.ApprovalId

                    match stored with
                    | Some row -> return replay witness request live.ActorId row
                    | None ->
                        return! fresh connection transaction witness request live.ActorId revision
                | _ -> return RealDataActivationApprovalOutcome.ResourceUnavailable
            | _ -> return RealDataActivationApprovalOutcome.ResourceUnavailable
        }

    let approve dataSource (witness: WitnessProtocol) context request =
        task {
            if not (RealDataActivationApprovalChecks.valid context request) then
                return RealDataActivationApprovalOutcome.ResourceUnavailable
            else
                try
                    witness.Admit()
                    use! connection = RuntimeDatabase.openConnectionAsync dataSource
                    use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)
                    return! underLock connection transaction witness context request
                with _ ->
                    return RealDataActivationApprovalOutcome.ResourceUnavailable
        }

    let review dataSource witness context planId =
        RealDataActivationPlanReview.review dataSource witness context planId
