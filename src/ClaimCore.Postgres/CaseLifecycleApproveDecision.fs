namespace ClaimCore.Postgres

open System
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain

module internal CaseLifecycleApproveDecision =
    let private approvable (projection: LifecycleProjection) mutation =
        match mutation with
        | LifecycleMutation.VoidDataEntryError _ ->
            CaseLifecycle.disposition projection.State = CaseDisposition.Active
            && CaseLifecycle.privacy projection.State = PrivacyPhase.Active
        | LifecycleMutation.ReinstateVoided _ ->
            CaseLifecycle.disposition projection.State = CaseDisposition.VoidedDataEntryError
            && CaseLifecycle.privacy projection.State = PrivacyPhase.Active
        | LifecycleMutation.PurgeLivePayload _ ->
            CaseLifecycle.privacy projection.State = PrivacyPhase.ErasurePending
            && CaseLifecycle.holds projection.State |> List.isEmpty
        | _ -> false

    let private validateProposal
        connection
        transaction
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        actorId
        (draftHash: byte array)
        instant
        =
        task {
            let! historicalPayment =
                match change.Action with
                | LifecycleMutation.VoidDataEntryError _ ->
                    CaseLifecyclePaymentEvidence.historicalPayment
                        connection
                        transaction
                        projection.CaseId
                | _ -> System.Threading.Tasks.Task.FromResult false

            let result =
                CaseLifecycleDecisions.decide
                    projection.CaseId
                    (Claim.view projection.Claim)
                    projection.State
                    change
                    actorId
                    (Convert.ToHexStringLower draftHash)
                    historicalPayment
                    None
                    []
                    instant

            return
                match result with
                | Ok _
                | Error LifecycleRefusal.ApprovalRequired -> Ok()
                | Error refusal -> Error refusal
        }

    let private alreadyApproved connection transaction operationId approverId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS(SELECT 1 FROM claimcore.case_lifecycle_approvals "
                    + "WHERE operation_id=@operation AND approver_actor_id=@approver)",
                    connection,
                    transaction
                )

            Sql.uuid command "operation" operationId
            Sql.uuid command "approver" approverId
            let! value = command.ExecuteScalarAsync()
            return value :?> bool
        }

    let private approvalCount connection transaction operationId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT count(*) FROM claimcore.case_lifecycle_approvals WHERE operation_id=@operation",
                    connection,
                    transaction
                )

            Sql.uuid command "operation" operationId
            let! value = command.ExecuteScalarAsync()
            return value :?> int64
        }

    let private checkSlot
        connection
        transaction
        witness
        (context: ActorCallContext)
        (change: LifecycleChange)
        =
        task {
            let! duplicate =
                alreadyApproved connection transaction change.EventId context.Binding.ActorId

            if duplicate then
                return Error(LifecycleWriteOutcome.Refused LifecycleRefusal.ApprovalRequired)
            else
                let! count = approvalCount connection transaction change.EventId

                if count >= 2L then
                    return
                        Error(
                            LifecycleWriteOutcome.Refused LifecycleRefusal.ApprovalCapacityExceeded
                        )
                else
                    let! correct =
                        CaseLifecycleStoreSupport.matchesWitness connection transaction witness

                    return
                        if correct then
                            Ok()
                        else
                            Error LifecycleWriteOutcome.ResourceUnavailable
        }

    let ready
        connection
        transaction
        witness
        (context: ActorCallContext)
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        draftHash
        instant
        =
        task {
            let expectedAction =
                match change.Action with
                | LifecycleMutation.PurgeLivePayload _ -> EndpointAction.ApproveErasure
                | _ -> EndpointAction.ApproveLifecycle

            if not (CaseLifecycleStoreSupport.matchesProjection change projection) then
                return Error(LifecycleWriteOutcome.Refused LifecycleRefusal.VersionConflict)
            elif context.Action <> expectedAction then
                return Error(LifecycleWriteOutcome.Refused LifecycleRefusal.ApprovalMismatch)
            elif not (approvable projection change.Action) then
                return Error(LifecycleWriteOutcome.Refused LifecycleRefusal.WrongDisposition)
            else
                let! valid =
                    validateProposal
                        connection
                        transaction
                        projection
                        change
                        context.Binding.ActorId
                        draftHash
                        instant

                match valid with
                | Error refusal -> return Error(LifecycleWriteOutcome.Refused refusal)
                | Ok() -> return! checkSlot connection transaction witness context change
        }
