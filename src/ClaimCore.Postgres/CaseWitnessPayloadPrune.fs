namespace ClaimCore.Postgres

open System
open System.Threading
open System.IO
open System.Security.Cryptography
open Npgsql
open NpgsqlTypes
open ClaimCore.Witness

[<NoEquality; NoComparison>]
type internal WitnessPruneSettlement = { Ticket: Ticket; DeletedCount: int64 }

/// Only the separately held witness schema-owner connection can call the atomic function.
/// A returned deleted count is physical primary-witness state, never backup-erasure certification.
module internal CaseWitnessPayloadPrune =
    let readExactIntent
        (witness: WitnessProtocol)
        caseId
        eventId
        sequence
        (entryHash: byte array)
        (canonical: byte array)
        (ct: CancellationToken)
        =
        task {
            let! observed = witness.EvidenceStore.TryReadEvidence(eventId, Intent, ct)

            let evidence =
                observed
                |> Option.defaultWith (fun () ->
                    raise (InvalidDataException("Prune intent is absent.")))

            let ticket = evidence.Ticket

            if
                ticket.Sequence <> sequence
                || ticket.Epoch <> witness.Identity.Epoch
                || ticket.EntryHash <> entryHash
                || ticket.ScopeKind <> Case
                || ticket.SubjectCaseId <> Some caseId
            then
                raise (InvalidDataException("Prune intent identity diverged."))

            let plain =
                witness.KeyCustody.Decrypt(
                    ticket.KeyId,
                    witness.AssociatedData(eventId, "INTENT"),
                    evidence.EncryptedPayload
                )

            try
                if plain <> canonical then
                    raise (InvalidDataException("Prune intent candidate diverged."))

                return
                    {
                        Ticket = ticket
                        CandidateHash = SHA256.HashData(plain)
                    }
            finally
                CryptographicOperations.ZeroMemory(plain)
        }

    let private sql =
        "SELECT sequence,entry_hash,payload_sha256,deleted_count "
        + "FROM claimcore_witness.settle_and_prune(ROW(@installation,@lineage,@epoch)::claimcore_witness.installation_identity,ROW(@case,ROW(@purge,@purgeSequence,@purgeHash)::claimcore_witness.journal_ticket,ROW(@cutoff,@cutoffHash)::claimcore_witness.journal_tip,@targetCount,@targetDigest)::claimcore_witness.prune_source,ROW(@event,ROW(@intentSequence,@intentHash)::claimcore_witness.journal_tip,ROW(@approvalOne,@approvalOneSequence,@approvalOneHash)::claimcore_witness.journal_ticket,ROW(@approvalTwo,@approvalTwoSequence,@approvalTwoHash)::claimcore_witness.journal_ticket)::claimcore_witness.prune_authority,ROW(@key,@settlement,@writerCapability)::claimcore_witness.writer_delivery)"

    let private bind
        (command: NpgsqlCommand)
        (witness: WitnessProtocol)
        caseId
        purgeEventId
        purgeSequence
        (purgeHash: byte array)
        (seal: WitnessPruneSeal)
        (intent: WitnessIntent)
        (approvals: PruneApprovalReceipt list)
        (ciphertext: byte array)
        (writerCapability: byte array)
        (ct: CancellationToken)
        =
        task {
            Sql.uuid command "installation" witness.Identity.InstallationId
            Sql.uuid command "lineage" witness.Identity.LineageId
            Sql.integer command "epoch" witness.Identity.Epoch
            Sql.uuid command "event" intent.Ticket.OperationId
            Sql.uuid command "case" caseId
            Sql.integer command "intentSequence" intent.Ticket.Sequence
            Sql.add command "intentHash" NpgsqlDbType.Bytea (box intent.Ticket.EntryHash)
            Sql.uuid command "purge" purgeEventId
            Sql.integer command "purgeSequence" purgeSequence
            Sql.add command "purgeHash" NpgsqlDbType.Bytea (box purgeHash)
            Sql.integer command "cutoff" seal.CutoffSequence
            Sql.add command "cutoffHash" NpgsqlDbType.Bytea (box seal.CutoffHash)
            Sql.integer command "targetCount" seal.TargetCount

            let! count, witnessDigest =
                CaseWitnessPayloadTargets.witnessDigest
                    witness
                    caseId
                    seal.CutoffSequence
                    seal.CutoffHash
                    ct

            if count <> seal.TargetCount then
                raise (InvalidDataException("Witness prune metadata count diverged."))

            Sql.add command "targetDigest" NpgsqlDbType.Bytea (box witnessDigest)
            let first = approvals[0]
            let second = approvals[1]
            Sql.uuid command "approvalOne" first.ApprovalId
            Sql.integer command "approvalOneSequence" first.WitnessSequence
            Sql.add command "approvalOneHash" NpgsqlDbType.Bytea (box first.WitnessHash)
            Sql.uuid command "approvalTwo" second.ApprovalId
            Sql.integer command "approvalTwoSequence" second.WitnessSequence
            Sql.add command "approvalTwoHash" NpgsqlDbType.Bytea (box second.WitnessHash)
            Sql.uuid command "key" intent.Ticket.KeyId
            Sql.add command "settlement" NpgsqlDbType.Bytea (box ciphertext)

            Sql.add command "writerCapability" NpgsqlDbType.Bytea (box writerCapability)
        }

    let private requireReceipt
        caseId
        purgeEventId
        purgeSequence
        (purgeHash: byte array)
        (seal: WitnessPruneSeal)
        (intent: WitnessIntent)
        (approvals: PruneApprovalReceipt list)
        =
        if
            caseId = Guid.Empty
            || purgeEventId = Guid.Empty
            || purgeSequence < 1L
            || purgeHash.Length <> 32
            || intent.Ticket.Phase <> Intent
            || intent.Ticket.ScopeKind <> Case
            || intent.Ticket.SubjectCaseId <> Some caseId
            || intent.Ticket.Sequence <= seal.CutoffSequence
            || seal.TargetCount < 1L
            || approvals.Length <> 2
            || approvals[0].ApprovalId = approvals[1].ApprovalId
        then
            raise (InvalidDataException("Witness prune receipt is incomplete."))

    let private readSettlement
        (witness: WitnessProtocol)
        caseId
        (intent: WitnessIntent)
        (reader: Data.Common.DbDataReader)
        (ct: CancellationToken)
        =
        task {
            let! found = reader.ReadAsync(ct)

            if not found then
                raise (InvalidDataException("Witness prune settlement is absent."))

            let ticket =
                {
                    Sequence = reader.GetInt64(0)
                    Epoch = witness.Identity.Epoch
                    KeyId = intent.Ticket.KeyId
                    EntryHash = reader.GetFieldValue<byte array>(1)
                    PayloadHash = reader.GetFieldValue<byte array>(2)
                    OperationId = intent.Ticket.OperationId
                    Phase = SettledAuthority
                    ScopeKind = Case
                    SubjectCaseId = Some caseId
                }

            let deleted = reader.GetInt64(3)

            let! duplicate = reader.ReadAsync(ct)

            if duplicate || ticket.Sequence <= intent.Ticket.Sequence then
                raise (InvalidDataException("Witness prune settlement diverged."))

            return
                {
                    Ticket = ticket
                    DeletedCount = deleted
                }
        }

    let private writeSettlement
        ownerWitnessConnection
        (witness: WitnessProtocol)
        caseId
        purgeEventId
        purgeSequence
        purgeHash
        seal
        (intent: WitnessIntent)
        (approvals: PruneApprovalReceipt list)
        (ciphertext: byte array)
        (ct: CancellationToken)
        =
        witness.EvidenceStore.WithWriterCapability(fun writerCapability ->
            task {
                use connection = new NpgsqlConnection(ownerWitnessConnection)
                do! connection.OpenAsync(ct)
                use command = new NpgsqlCommand(sql, connection)

                do!
                    bind
                        command
                        witness
                        caseId
                        purgeEventId
                        purgeSequence
                        purgeHash
                        seal
                        intent
                        approvals
                        ciphertext
                        writerCapability
                        ct

                ct.ThrowIfCancellationRequested()
                use! reader = command.ExecuteReaderAsync(CancellationToken.None)
                let! result = readSettlement witness caseId intent reader CancellationToken.None
                reader.Close()

                do!
                    witness.RequireSettled(
                        intent.Ticket.OperationId,
                        SettledAuthority,
                        CancellationToken.None
                    )

                return result
            })

    let settle
        ownerWitnessConnection
        (witness: WitnessProtocol)
        caseId
        purgeEventId
        purgeSequence
        (purgeHash: byte array)
        (seal: WitnessPruneSeal)
        (intent: WitnessIntent)
        (approvals: PruneApprovalReceipt list)
        (ct: CancellationToken)
        =
        task {
            requireReceipt caseId purgeEventId purgeSequence purgeHash seal intent approvals

            let! ciphertext =
                WitnessSettlement.authorityCiphertext
                    witness.EvidenceStore
                    witness.KeyCustody
                    (fun operation phase -> witness.AssociatedData(operation, phase))
                    intent
                    ct

            try
                return!
                    writeSettlement
                        ownerWitnessConnection
                        witness
                        caseId
                        purgeEventId
                        purgeSequence
                        purgeHash
                        seal
                        intent
                        approvals
                        ciphertext
                        ct
            finally
                CryptographicOperations.ZeroMemory(ciphertext)
        }
