namespace ClaimCore.Postgres

open System
open System.IO
open System.Threading
open Npgsql

module internal CaseLifecycleAuditScan =
    let private reject () : 'a =
        raise (InvalidDataException("Lifecycle page replay differs."))

    let approvals connection transaction witness cutoff caseId (ct: CancellationToken) =
        task {
            let mutable after: Guid option = None
            let mutable count = 0L
            let mutable more = true

            while more do
                let! page =
                    CaseLifecycleAuditRows.approvalsPage connection transaction caseId after 50 ct

                match page with
                | [] -> more <- false
                | rows ->
                    for row in rows do
                        ct.ThrowIfCancellationRequested()
                        CaseLifecycleAuditEvidence.approval witness cutoff caseId row
                        count <- count + 1L

                    after <- rows |> List.last |> fun row -> Some row.ApprovalId

            return count
        }

    let private applyEvent
        connection
        transaction
        witness
        cutoff
        caseId
        (cursor: LifecycleBusinessCursor)
        (state: LifecycleAuditState)
        (row: LifecycleAuditEventRow)
        (ct: CancellationToken)
        =
        task {
            let event =
                CaseLifecycleAuditEvidence.event
                    witness
                    cutoff
                    caseId
                    state.EventHash
                    (state.Sequence + 1L)
                    row

            let! before =
                CaseLifecycleAuditCursor.advanceTo cursor event.Change.ExpectedRevision state ct

            if before.Revision <> event.Change.ExpectedRevision then
                reject ()

            let! approved =
                CaseLifecycleAuditEventCheck.boundApprovals
                    connection
                    transaction
                    witness
                    cutoff
                    caseId
                    row
                    event
                    ct

            do! CaseLifecycleAuditEventCheck.verifyHold connection transaction caseId event ct
            return CaseLifecycleAuditReplay.step caseId before row event approved
        }

    let events
        connection
        transaction
        witness
        cutoff
        caseId
        (cursor: LifecycleBusinessCursor)
        (ct: CancellationToken)
        =
        task {
            let mutable state = CaseLifecycleAuditReplay.initial
            let mutable more = true

            while more do
                let! page =
                    CaseLifecycleAuditRows.eventsPage
                        connection
                        transaction
                        caseId
                        state.Sequence
                        50
                        ct

                match page with
                | [] -> more <- false
                | rows ->
                    for row in rows do
                        ct.ThrowIfCancellationRequested()

                        let! next =
                            applyEvent
                                connection
                                transaction
                                witness
                                cutoff
                                caseId
                                cursor
                                state
                                row
                                ct

                        state <- next

            return! CaseLifecycleAuditCursor.advanceTo cursor Int64.MaxValue state ct
        }
