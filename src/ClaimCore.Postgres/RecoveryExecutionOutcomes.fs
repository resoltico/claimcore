namespace ClaimCore.Postgres

open System
open System.Data
open System.IO
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open OperationAuthorityStore
open PreparationData
open RecoveryExecutionSupport
open WitnessProtocolReconciliation

/// The one storage-owned transaction that rechecks authority, invokes the pure Domain callback,
/// persists accepted state, and records a definite settlement together.
module internal RecoveryExecutionOutcomes =
    let revokeExisting
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (request: CommandRequest)
        (witness: WitnessProtocol)
        cancellationToken
        =
        task {
            do!
                witness.ReconcileRevoked(
                    connection,
                    transaction,
                    request.OperationId,
                    cancellationToken
                )

            // Revocation closes future authority; it does not settle a historical attempt.
            return Ok(AdmittedExecution.RevokedBeforeExecution SettlementConfirmation.Unconfirmed)
        }

    let rejectPending
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (attemptId: Guid)
        rejection
        cancellationToken
        commitStarted
        (knownRejection: DomainError option ref)
        =
        task {
            knownRejection.Value <- Some rejection
            do! settleRequired connection transaction attemptId RecoverySettlement.Rejected
            do! commit transaction cancellationToken commitStarted
            return Ok(AdmittedExecution.Rejected(rejection, SettlementConfirmation.Confirmed))
        }

    let private attribution
        caseId
        (retained: RetainedPreparation)
        (actorContext: ActorCallContext)
        =
        let phase =
            if actorContext.Action = EndpointAction.RecoveryResolve then
                AttemptActorPhase.RecoveryResolve
            else
                AttemptActorPhase.NormalSubmit

        {
            Command =
                {
                    Actor = actorContext.Binding
                    CaseId = caseId
                }
            PreparerActorId = retained.PreparerActorId
            ImporterActorId = retained.ImporterActorId
            Phase = phase
        }

    let private persistAccepted
        connection
        transaction
        operation
        context
        current
        caseId
        actorEvidence
        claim
        ticket
        =
        task {
            try
                return!
                    AcceptedCasePersistence.persistUnderCaseLock
                        connection
                        transaction
                        operation
                        context
                        current
                        caseId
                        actorEvidence
                        claim
                        ticket
            with _ ->
                return raise WitnessPending
        }

    let acceptPending
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        operation
        (context: BusinessContext)
        current
        claim
        (retained: RetainedPreparation)
        (actorContext: ActorCallContext)
        (attemptId: Guid)
        cancellationToken
        commitStarted
        (witness: WitnessProtocol)
        =
        task {
            let request = Operation.request operation

            let! caseId =
                StoreData.caseIdForDecision
                    connection
                    transaction
                    request.CaseReference
                    current
                    (Some retained.CaseId)
                    false

            let actorEvidence = attribution caseId retained actorContext

            let! intent =
                WitnessAcceptedProtocol.beginAccepted
                    witness
                    operation
                    context
                    caseId
                    actorEvidence
                    claim
                    cancellationToken

            let! receipt =
                persistAccepted
                    connection
                    transaction
                    operation
                    context
                    current
                    caseId
                    actorEvidence
                    claim
                    intent.Ticket

            do! settleRequired connection transaction attemptId RecoverySettlement.Accepted
            do! commit transaction cancellationToken commitStarted
            let! _ = witness.SettleAccepted(request.OperationId, intent)
            return Ok(AdmittedExecution.Accepted receipt)
        }
