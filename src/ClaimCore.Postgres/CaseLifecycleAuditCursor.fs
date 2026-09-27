namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql

/// Holds at most one 50-row business page while lifecycle authority is replayed.
type internal LifecycleBusinessCursor
    (connection: NpgsqlConnection, transaction: NpgsqlTransaction, caseId: Guid) =
    let mutable after = 0L
    let mutable buffered: (int64 * byte array) list = []
    let mutable exhausted = false

    member _.Peek(ct: CancellationToken) =
        task {
            if buffered.IsEmpty && not exhausted then
                let! page =
                    CaseLifecycleAuditRelations.businessPage
                        connection
                        transaction
                        caseId
                        after
                        50
                        ct

                buffered <- page
                exhausted <- page.IsEmpty

            return List.tryHead buffered
        }

    member _.Drop() =
        match buffered with
        | (revision, _) :: rest ->
            after <- revision
            buffered <- rest
        | [] -> invalidOp "No business row is buffered."

module internal CaseLifecycleAuditCursor =
    let advanceTo
        (cursor: LifecycleBusinessCursor)
        target
        (state: LifecycleAuditState)
        (ct: CancellationToken)
        =
        task {
            let mutable current = state
            let mutable reading = true

            while reading do
                let! next = cursor.Peek(ct)

                match next with
                | Some(revision, bytes) when revision <= target ->
                    current <- CaseLifecycleAuditReplay.appendBusiness current (revision, bytes)
                    cursor.Drop()
                | _ -> reading <- false

            return current
        }
