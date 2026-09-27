namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Domain
open ClaimCore.RecordFormat
open DataAuditCommon

type internal DispositionCursor =
    {
        mutable After: int64
        mutable Buffered: (int64 * byte array) list
        mutable Exhausted: bool
        mutable Consumed: int64
    }

/// Fetches at most one authenticated disposition page while retaining only its remaining rows.
module internal DataAuditDispositionCursor =
    let create () =
        {
            After = 0L
            Buffered = []
            Exhausted = false
            Consumed = 0L
        }

    let private restore previous (revision, bytes: byte array) =
        let prior = previous |> Option.defaultWith corrupt |> Claim.view

        let snapshot =
            match CaseRecord.decodeSnapshot bytes with
            | Ok value when CaseRecord.encodeSnapshot value = bytes -> value
            | _ -> corrupt ()

        if
            snapshot.Version <> revision
            || snapshot.Version <> prior.Version + 1L
            || snapshot.Fields <> prior.Fields
        then
            corrupt ()

        match Claim.restore snapshot with
        | Ok claim -> Some claim
        | Error _ -> corrupt ()

    let private peek
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        witness
        cutoff
        caseId
        (cursor: DispositionCursor)
        (ct: CancellationToken)
        =
        task {
            match cursor.Buffered with
            | head :: _ -> return Some head
            | [] when cursor.Exhausted -> return None
            | [] ->
                let! page =
                    CaseLifecycleAudit.readVerifiedDispositionPage
                        connection
                        transaction
                        witness
                        cutoff
                        caseId
                        cursor.After
                        50
                        ct

                match page.Items with
                | [] ->
                    cursor.Exhausted <- true
                    return None
                | rows ->
                    cursor.Buffered <- rows
                    cursor.After <- rows |> List.last |> fst
                    cursor.Exhausted <- page.NextAfter.IsNone
                    return rows |> List.tryHead
        }

    let advance
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        witness
        cutoff
        caseId
        (cursor: DispositionCursor)
        target
        previous
        (ct: CancellationToken)
        =
        task {
            let mutable state = previous
            let mutable more = true

            while more do
                let! next = peek connection transaction witness cutoff caseId cursor ct

                match next with
                | Some(revision, bytes) when revision < target ->
                    state <- restore state (revision, bytes)
                    cursor.Buffered <- List.tail cursor.Buffered
                    cursor.Consumed <- cursor.Consumed + 1L
                | Some(revision, _) when revision = target -> corrupt ()
                | _ -> more <- false

            return state
        }
