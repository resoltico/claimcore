namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application
open DataAuditCommon

/// Accepted command rows are replayed through Application/Domain with authenticated
/// disposition-only revisions interleaved from bounded independent witness-verified pages.
module internal DataAuditReplay =
    let private verifyRow
        zone
        (witness: WitnessProtocol)
        cutoff
        caseId
        previous
        (row: AcceptedAuditRow)
        =
        if row.CaseId <> caseId || row.RuleRevision <> 1s || row.WitnessSequence > cutoff then
            corrupt ()

        let localDate =
            TimeZoneInfo.ConvertTime(row.ObservedInstant, zone).DateTime
            |> DateOnly.FromDateTime

        if row.BusinessDate <> localDate then
            corrupt ()

        let digest =
            witnessProof (fun () ->
                WitnessCandidate.acceptedDigestFromEvidence
                    row.Request.OperationId
                    row.ActorEvidence
                    row.Request.CaseReference
                    row.Request.ExpectedVersion
                    (Claim.view row.Receipt.Case).Version
                    row.Receipt.CommandName
                    row.BusinessDate
                    row.ObservedInstant
                    row.CanonicalRequest
                    row.Snapshot)

        witnessProof (fun () ->
            witness.VerifyAcceptedEvidenceForCase(
                row.Request.OperationId,
                row.WitnessSequence,
                row.WitnessEpoch,
                row.WitnessEntryHash,
                digest,
                caseId
            ))

        match
            AcceptedHistoryAudit.replay row.BusinessDate row.Request previous row.Receipt.Case
        with
        | Some replayed -> replayed
        | None -> corrupt ()

    let private replayPage
        connection
        transaction
        zone
        witness
        cutoff
        caseId
        cursor
        previous
        rows
        (ct: CancellationToken)
        =
        task {
            let mutable last = previous

            for row in rows do
                let revision = (Claim.view row.Receipt.Case).Version

                let! advanced =
                    DataAuditDispositionCursor.advance
                        connection
                        transaction
                        witness
                        cutoff
                        caseId
                        cursor
                        revision
                        last
                        ct

                last <- Some(verifyRow zone witness cutoff caseId advanced row)

            return last
        }

    let private finish
        connection
        transaction
        witness
        cutoff
        caseId
        cursor
        previous
        (current: Claim)
        (lastBusiness: CaseView)
        dispositionCount
        (ct: CancellationToken)
        =
        task {
            match previous with
            | None -> corrupt ()
            | Some replayed when Claim.view replayed <> lastBusiness -> corrupt ()
            | Some _ -> ()

            let! final =
                DataAuditDispositionCursor.advance
                    connection
                    transaction
                    witness
                    cutoff
                    caseId
                    cursor
                    Int64.MaxValue
                    previous
                    ct

            if
                cursor.Consumed <> dispositionCount
                || (final |> Option.map Claim.view) <> Some(Claim.view current)
            then
                corrupt ()
        }

    let private replayAcceptedPages
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        zone
        (witness: WitnessProtocol)
        cutoff
        caseId
        reference
        cursor
        (ct: CancellationToken)
        =
        task {
            let mutable after = 0L
            let mutable previous: Claim option = None
            let mutable accepted = 0L
            let mutable more = true

            while more do
                let! rows = DataAuditAcceptedRows.readPage connection transaction reference after ct

                match List.tryLast rows with
                | None -> more <- false
                | Some last ->
                    let! replayed =
                        replayPage
                            connection
                            transaction
                            zone
                            witness
                            cutoff
                            caseId
                            cursor
                            previous
                            rows
                            ct

                    previous <- replayed
                    accepted <- accepted + int64 rows.Length
                    after <- (Claim.view last.Receipt.Case).Version

            return previous, accepted
        }

    let replayCase
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        zone
        (witness: WitnessProtocol)
        cutoff
        caseId
        (current: Claim)
        dispositionCount
        (lastBusiness: CaseView)
        (ct: CancellationToken)
        =
        task {
            let reference = (Claim.view current).Fields.CaseReference
            let cursor = DataAuditDispositionCursor.create ()

            let! previous, accepted =
                replayAcceptedPages
                    connection
                    transaction
                    zone
                    witness
                    cutoff
                    caseId
                    reference
                    cursor
                    ct

            do!
                finish
                    connection
                    transaction
                    witness
                    cutoff
                    caseId
                    cursor
                    previous
                    current
                    lastBusiness
                    dispositionCount
                    ct

            return accepted
        }
