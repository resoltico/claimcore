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
    let private replay
        (witness: WitnessProtocol)
        (request: RealDataActivationApprovalRequest)
        actorId
        (stored: RealDataActivationApprovalRow)
        ct
        =
        task {
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
                    return RealDataActivationApprovalOutcome.ResourceUnavailable
                else
                    try
                        do!
                            RealDataActivationApprovalRecovery.requireSettled
                                witness
                                request
                                stored
                                canonical
                                ct

                        return
                            RealDataActivationApprovalOutcome.Approved(
                                request.ApprovalId,
                                stored.ApproverGrantRevision
                            )
                    with _ ->
                        return
                            RealDataActivationApprovalOutcome.StartedUnconfirmed request.ApprovalId
            finally
                CryptographicOperations.ZeroMemory(canonical)
        }

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
                    let! _ = witness.SettleAuthority(request.ApprovalId, intent)
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
        ct
        =
        task {
            let canonical =
                RealDataActivationApprovalCandidate.canonical request actorId revision approvedAt

            try
                try
                    let! intent = witness.BeginAuthority(request.ApprovalId, canonical, None, ct)

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

    let private resumeOrBegin
        connection
        transaction
        witness
        (request: RealDataActivationApprovalRequest)
        actorId
        revision
        snapshot
        pending
        now
        ct
        =
        task {
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
                    beginAndRetain connection transaction witness request actorId revision now ct
            | None -> return RealDataActivationApprovalOutcome.ResourceUnavailable
        }

    let private fresh
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (request: RealDataActivationApprovalRequest)
        actorId
        revision
        ct
        =
        task {
            let! now = Sql.databaseNow connection transaction ct
            let! snapshot = witness.Snapshot(ct)
            let! first = RealDataActivationApprovalRows.first connection transaction request
            let! pending = RealDataActivationApprovalRecovery.tryRead witness request actorId ct

            let! common =
                RealDataActivationApprovalChecks.baseEligible
                    witness
                    request
                    actorId
                    now
                    pending.IsNone
                    first
                    snapshot
                    ct

            if not common then
                return RealDataActivationApprovalOutcome.ResourceUnavailable
            else
                return!
                    resumeOrBegin
                        connection
                        transaction
                        witness
                        request
                        actorId
                        revision
                        snapshot
                        pending
                        now
                        ct
        }

    let private underLock
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (request: RealDataActivationApprovalRequest)
        ct
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
                        ct

                match published with
                | Some plan when RealDataActivationApprovalChecks.matchesPublished request plan ->
                    let! stored =
                        RealDataActivationApprovalRows.prior
                            connection
                            transaction
                            request.ApprovalId

                    match stored with
                    | Some row -> return! replay witness request live.ActorId row ct
                    | None ->
                        return!
                            fresh connection transaction witness request live.ActorId revision ct
                | _ -> return RealDataActivationApprovalOutcome.ResourceUnavailable
            | _ -> return RealDataActivationApprovalOutcome.ResourceUnavailable
        }

    let approve dataSource (witness: WitnessProtocol) context request ct =
        task {
            if not (RealDataActivationApprovalChecks.valid context request) then
                return RealDataActivationApprovalOutcome.ResourceUnavailable
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
                    return RealDataActivationApprovalOutcome.ResourceUnavailable
        }
