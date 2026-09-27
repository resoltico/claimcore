namespace ClaimCore.Postgres

open System
open Npgsql
open ClaimCore.Witness
open DataAuditCommon

/// Bounded two-stream merge: independent witness metadata pages and primary target pages.
/// No erased ciphertext is treated as present, and no primary target row is trusted alone.
module internal CaseTombstonePruneTargetAudit =
    [<NoEquality; NoComparison>]
    type private Cursor =
        {
            Connection: NpgsqlConnection
            Transaction: NpgsqlTransaction
            CaseId: Guid
            mutable After: int64
            mutable Buffer: StoredWitnessPruneTarget list
        }

    let private next (cursor: Cursor) =
        task {
            if cursor.Buffer.IsEmpty then
                let! page =
                    CaseTombstonePruneTargetRows.page
                        cursor.Connection
                        cursor.Transaction
                        cursor.CaseId
                        cursor.After

                cursor.Buffer <- page

            match cursor.Buffer with
            | [] -> return None
            | item :: rest ->
                cursor.Buffer <- rest
                cursor.After <- item.Target.Sequence
                return Some item
        }

    let private exact
        (receipt: StoredWitnessPruneReceipt)
        (metadata: MetadataRecord)
        (stored: StoredWitnessPruneTarget)
        =
        let ticket = metadata.Ticket
        let target = stored.Target

        if
            metadata.PayloadPresent
            || stored.PruneEventId <> receipt.EventId
            || target.Sequence <> ticket.Sequence
            || target.OperationId <> ticket.OperationId
            || target.Phase <> ticket.Phase
            || target.Epoch <> ticket.Epoch
            || target.EntryHash <> ticket.EntryHash
            || target.PayloadHash <> ticket.PayloadHash
        then
            corrupt ()

    let private targetOf (item: MetadataRecord) marker : WitnessPruneTarget =
        {
            Sequence = item.Ticket.Sequence
            OperationId = item.Ticket.OperationId
            Phase = item.Ticket.Phase
            Epoch = item.Ticket.Epoch
            EntryHash = item.Ticket.EntryHash
            PayloadHash = item.Ticket.PayloadHash
            IsExternalPublication = marker
        }

    let private metadataPage (witness: WitnessProtocol) after previousHash cutoff =
        witnessProof (fun () ->
            witness.EvidenceStore.ReadMetadataPage(after, previousHash, cutoff, 32))

    let verify
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (receipt: StoredWitnessPruneReceipt)
        =
        task {
            let cursor =
                {
                    Connection = connection
                    Transaction = transaction
                    CaseId = receipt.CaseId
                    After = 0L
                    Buffer = []
                }

            let mutable after = 0L
            let mutable previousHash = Array.zeroCreate<byte> 32
            let mutable count = 0L
            let mutable digest = CaseWitnessPayloadTargets.initialDigest ()

            while after < receipt.CutoffSequence do
                let page = metadataPage witness after previousHash receipt.CutoffSequence

                if page.Items.IsEmpty then
                    corrupt ()

                for item in page.Items do
                    if
                        item.Ticket.ScopeKind = Case
                        && item.Ticket.SubjectCaseId = Some receipt.CaseId
                    then
                        let! target = next cursor
                        let stored = target |> Option.defaultWith corrupt
                        exact receipt item stored

                        digest <-
                            CaseWitnessPayloadTargets.step
                                digest
                                (targetOf item stored.Target.IsExternalPublication)

                        count <- count + 1L

                    after <- item.Ticket.Sequence
                    previousHash <- item.Ticket.EntryHash

            let! extra = next cursor

            if
                extra.IsSome
                || after <> receipt.CutoffSequence
                || previousHash <> receipt.CutoffHash
                || count <> receipt.TargetCount
                || digest <> receipt.TargetDigest
            then
                corrupt ()

            return count
        }
