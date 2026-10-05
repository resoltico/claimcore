namespace ClaimCore.Postgres

open System
open System.IO
open System.Threading
open Npgsql
open NpgsqlTypes
open ClaimCore.Domain

type internal LifecycleAuditVerification =
    {
        Count: int64
        LastBusinessSnapshot: CaseView
        DispositionCount: int64
        TipSequence: int64
        TipHash: byte array
    }

type internal LifecycleDispositionPage =
    {
        Items: (int64 * byte array) list
        NextAfter: int64 option
    }

/// Full read-only per-case replay. The caller owns one stable primary snapshot and the
/// quiescent independent witness cutoff; this helper never settles or appends evidence.
module internal CaseLifecycleAudit =
    let private reject () : 'a =
        raise (InvalidDataException("Case lifecycle audit differs."))

    let private currentProjection connection transaction caseId (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT disposition,privacy_phase,lifecycle_sequence,lifecycle_event_hash "
                    + "FROM claimcore.cases WHERE case_id=@caseId",
                    connection,
                    transaction
                )

            command.Parameters.AddWithValue("caseId", NpgsqlDbType.Uuid, caseId) |> ignore
            let! result = command.ExecuteReaderAsync(ct)
            use reader = result
            let! found = reader.ReadAsync(ct)

            if not found then
                reject ()

            let disposition =
                CaseLifecycleCandidate.parseDisposition (reader.GetString(0))
                |> Option.defaultWith reject

            let privacy =
                CaseLifecycleCandidate.parsePrivacy (reader.GetString(1))
                |> Option.defaultWith reject

            let sequence = reader.GetInt64(2)
            let eventHash = reader.GetFieldValue<byte array>(3)
            let! extra = reader.ReadAsync(ct)

            if extra then
                reject ()

            return disposition, privacy, sequence, eventHash
        }

    let verifyCase
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (cutoff: int64)
        (caseId: Guid)
        (currentClaim: Claim)
        (ct: CancellationToken)
        =
        task {
            ct.ThrowIfCancellationRequested()
            do! CaseLifecycleAuditEventCheck.requireApprovalLimit connection transaction caseId ct

            let! approvalCount =
                CaseLifecycleAuditScan.approvals connection transaction witness cutoff caseId ct

            let cursor = LifecycleBusinessCursor(connection, transaction, caseId)

            let! final =
                CaseLifecycleAuditScan.events connection transaction witness cutoff caseId cursor ct

            let! disposition, privacy, sequence, eventHash =
                currentProjection connection transaction caseId ct

            let! holdCount =
                CaseLifecycleAuditRelations.holdCount connection transaction caseId ct

            let! activeHoldCount =
                CaseLifecycleAuditRelations.activeHoldCount connection transaction caseId ct

            let lastBusinessSnapshot =
                CaseLifecycleAuditReplay.finish
                    final
                    currentClaim
                    disposition
                    privacy
                    sequence
                    eventHash
                    holdCount
                    activeHoldCount

            return
                {
                    Count = final.Sequence + approvalCount
                    LastBusinessSnapshot = lastBusinessSnapshot
                    DispositionCount = final.DispositionCount
                    TipSequence = final.Sequence
                    TipHash = final.EventHash
                }
        }

    let readVerifiedDispositionPage
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (cutoff: int64)
        (caseId: Guid)
        (afterRevision: int64)
        limit
        (ct: CancellationToken)
        =
        task {
            if limit < 1 || limit > 50 || afterRevision < 0L then
                invalidArg (nameof limit) "Use a 1-50 disposition page."

            let! rows =
                CaseLifecycleAuditRows.dispositionPage
                    connection
                    transaction
                    caseId
                    afterRevision
                    limit
                    ct

            let items = ResizeArray<int64 * byte array>()

            for row in rows do
                ct.ThrowIfCancellationRequested()

                let! event =
                    CaseLifecycleAuditEvidence.event
                        witness
                        cutoff
                        caseId
                        row.PreviousHash
                        row.Sequence
                        row
                        ct

                match event.Snapshot with
                | Some bytes -> items.Add(row.BusinessRevision, bytes)
                | None -> reject ()

            return
                {
                    Items = List.ofSeq items
                    NextAfter =
                        if rows.Length = limit then
                            items |> Seq.tryLast |> Option.map fst
                        else
                            None
                }
        }
