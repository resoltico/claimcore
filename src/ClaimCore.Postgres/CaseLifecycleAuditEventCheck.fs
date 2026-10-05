namespace ClaimCore.Postgres

open System
open System.IO
open System.Threading
open Npgsql
open ClaimCore.Application

module internal CaseLifecycleAuditEventCheck =
    let private reject () : 'a =
        raise (InvalidDataException("Lifecycle event authority differs."))

    let boundApprovals
        connection
        transaction
        witness
        cutoff
        caseId
        (row: LifecycleAuditEventRow)
        (event: LifecycleAuditDecodedEvent)
        (ct: CancellationToken)
        =
        task {
            let ids = event.ApprovalIds

            if
                ids.Length > 2
                || ids <> List.sort ids
                || ids.Length <> List.length (List.distinct ids)
            then
                reject ()

            let values = ResizeArray<LifecycleApprovalEvidence>()

            for id in ids do
                let! found = CaseLifecycleAuditRows.approvalById connection transaction id ct
                let approval = found |> Option.defaultWith reject

                if
                    approval.OperationId <> row.EventId
                    || approval.CaseId <> caseId
                    || approval.ActionName <> row.ActionName
                    || approval.ExpectedRevision <> event.Change.ExpectedRevision
                    || approval.ExpectedSequence <> event.Change.ExpectedLifecycleSequence
                    || approval.ExpectedHash <> row.PreviousHash
                    || approval.DraftHash <> row.DraftHash
                    || approval.ApprovedAt > event.Instant
                    || approval.ExpiresAt <= event.Instant
                then
                    reject ()

                do! CaseLifecycleAuditEvidence.approval witness cutoff caseId approval ct

                values.Add
                    {
                        ApprovalId = approval.ApprovalId
                        ApproverId = approval.ApproverId
                        ExpiresAt = approval.ExpiresAt
                    }

            return List.ofSeq values
        }

    let verifyHold
        connection
        transaction
        caseId
        (event: LifecycleAuditDecodedEvent)
        (ct: CancellationToken)
        =
        task {
            match event.Change.Action with
            | LifecycleMutation.RecordHold(holdId, ground, reviewOn) ->
                let! row =
                    CaseLifecycleAuditRelations.holdById connection transaction caseId holdId ct

                let actual = row |> Option.defaultWith reject

                if
                    actual.Hold.Id <> holdId
                    || actual.Hold.Ground <> ground
                    || actual.Hold.ReviewOn <> reviewOn
                    || actual.Hold.RecordedBy <> event.ActorId
                    || actual.Hold.RecordedAt <> event.Instant
                then
                    reject ()
            | LifecycleMutation.ReleaseHold(holdId, reason) ->
                let! row =
                    CaseLifecycleAuditRelations.holdById connection transaction caseId holdId ct

                let actual = row |> Option.defaultWith reject

                if
                    actual.ReleasedBy <> Some event.ActorId
                    || actual.ReleasedAt <> Some event.Instant
                    || actual.ReleaseReason <> Some reason
                then
                    reject ()
            | _ -> ()
        }

    let requireApprovalLimit connection transaction caseId (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS(SELECT 1 FROM claimcore.case_lifecycle_approvals "
                    + "WHERE case_id=@caseId GROUP BY operation_id HAVING count(*)>2)",
                    connection,
                    transaction
                )

            Sql.uuid command "caseId" caseId
            let! value = command.ExecuteScalarAsync(ct)

            if value :?> bool then
                reject ()
        }
