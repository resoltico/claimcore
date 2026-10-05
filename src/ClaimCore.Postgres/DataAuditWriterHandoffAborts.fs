namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Witness
open DataAuditCommon

[<NoEquality; NoComparison>]
type private AbortAuditRow =
    {
        HandoffId: Guid
        OldGeneration: int64
        First: Guid
        Second: Guid
        Canonical: byte array
        Candidate: byte array
        Sequence: int64
        Hash: byte array
    }

/// Both directions of the owner abort receipt: primary rows against A1, and
/// witness-only A1 remains explicitly pending until primary backfill and A3.
module internal DataAuditWriterHandoffAborts =
    let private page (connection: NpgsqlConnection) transaction after (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT handoff_id,old_generation,approval_one_id,approval_two_id,"
                    + "abort_canonical,abort_candidate_sha256,abort_sequence,abort_hash "
                    + "FROM claimcore.writer_handoff_aborts WHERE abort_sequence>@after "
                    + "ORDER BY abort_sequence LIMIT 50",
                    connection,
                    transaction
                )

            Sql.integer command "after" after
            use! reader = command.ExecuteReaderAsync(ct)
            let rows = ResizeArray<AbortAuditRow>()

            while reader.Read() do
                rows.Add
                    {
                        HandoffId = reader.GetGuid(0)
                        OldGeneration = reader.GetInt64(1)
                        First = reader.GetGuid(2)
                        Second = reader.GetGuid(3)
                        Canonical = reader.GetFieldValue<byte array>(4)
                        Candidate = reader.GetFieldValue<byte array>(5)
                        Sequence = reader.GetInt64(6)
                        Hash = reader.GetFieldValue<byte array>(7)
                    }

            return rows.ToArray()
        }

    let private ownerMatches
        (reader: System.Data.Common.DbDataReader)
        (value: WriterHandoffAbort)
        first
        =
        reader.GetGuid(1) =
            (if first then
                 value.AbortSigningKeyOneId
             else
                 value.AbortSigningKeyTwoId)
        && reader.GetGuid(2) =
            (if first then
                 value.OwnerOneActorId
             else
                 value.OwnerTwoActorId)
        && reader.GetInt64(3) =
            (if first then
                 value.OwnerOneGrantRevision
             else
                 value.OwnerTwoGrantRevision)

    let private evidenceMatches
        (reader: System.Data.Common.DbDataReader)
        (value: WriterHandoffAbort)
        (row: AbortAuditRow)
        (entry: WriterHandoffEvidence)
        first
        =
        let signature =
            if first then
                entry.AbortSignatureOne
            else
                entry.AbortSignatureTwo

        reader.GetFieldValue<byte array>(4) = row.Canonical
        && Some(reader.GetFieldValue<byte array>(5)) = signature
        && reader.GetFieldValue<byte array>(6) = row.Candidate
        && reader.GetFieldValue<DateTimeOffset>(7) = value.ValidUntil
        && reader.GetInt64(8) = row.Sequence
        && reader.GetInt64(9) = value.Epoch
        && reader.GetFieldValue<byte array>(10) = row.Hash
        && not (reader.IsDBNull(11))
        && reader.GetGuid(11) = row.HandoffId

    let private approvals
        (connection: NpgsqlConnection)
        transaction
        (value: WriterHandoffAbort)
        (row: AbortAuditRow)
        (entry: WriterHandoffEvidence)
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT a.approval_id,a.signing_key_id,a.owner_actor_id,"
                    + "a.owner_grant_revision,a.abort_canonical,a.ed25519_signature,"
                    + "a.candidate_sha256,a.expires_at,a.witness_sequence,a.witness_epoch,"
                    + "a.witness_entry_hash,u.handoff_id "
                    + "FROM claimcore.writer_handoff_abort_approvals a "
                    + "LEFT JOIN claimcore.writer_handoff_abort_approval_uses u "
                    + "ON u.approval_id=a.approval_id "
                    + "WHERE a.handoff_id=@handoff ORDER BY a.approval_id",
                    connection,
                    transaction
                )

            Sql.uuid command "handoff" row.HandoffId
            use! reader = command.ExecuteReaderAsync(ct)
            let mutable seen = Set.empty<Guid>

            while reader.Read() do
                let id = reader.GetGuid(0)
                let first = id = value.ApprovalOneId
                let second = id = value.ApprovalTwoId

                if
                    (not first && not second)
                    || seen.Contains id
                    || not (ownerMatches reader value first)
                    || not (evidenceMatches reader value row entry first)
                then
                    corrupt ()

                seen <- seen.Add id

            if seen <> set [ value.ApprovalOneId; value.ApprovalTwoId ] then
                corrupt ()
        }

    let private rowMatches
        (value: WriterHandoffAbort)
        (row: AbortAuditRow)
        (ticket: Ticket)
        (entry: WriterHandoffEvidence)
        expected
        cutoff
        =
        value.HandoffId = row.HandoffId
        && value.OldGeneration = row.OldGeneration
        && value.ApprovalOneId = row.First
        && value.ApprovalTwoId = row.Second
        && expected = row.Candidate
        && ticket.Sequence = row.Sequence
        && ticket.EntryHash = row.Hash
        && row.Sequence <= cutoff
        && (entry.AbortRecordedAt |> Option.exists (fun time -> value.ValidUntil > time))

    let private verifyRow
        connection
        transaction
        (witness: WitnessProtocol)
        cutoff
        (row: AbortAuditRow)
        (ct: CancellationToken)
        =
        task {
            let value = WriterHandoffAbort.parse row.Canonical |> Option.defaultWith corrupt

            let! preparation =
                WriterHandoffOwnerRead.preparation connection transaction witness row.HandoffId ct

            let prepared = preparation |> Option.defaultWith corrupt

            let! retained = witness.EvidenceStore.TryReadHandoff(row.HandoffId, ct)
            let entry = retained |> Option.defaultWith corrupt

            let signatureOne = entry.AbortSignatureOne |> Option.defaultWith corrupt
            let signatureTwo = entry.AbortSignatureTwo |> Option.defaultWith corrupt

            let! observed =
                WriterHandoffOwnerAbortEvidence.read
                    witness
                    prepared
                    value
                    row.Canonical
                    signatureOne
                    signatureTwo
                    ct

            let ticket = observed |> Option.defaultWith corrupt

            let expected =
                WriterHandoffWitnessAbortCommands.candidate row.Canonical signatureOne signatureTwo

            if not (rowMatches value row ticket entry expected cutoff) then
                corrupt ()

            do! approvals connection transaction value row entry ct

            do!
                WriterHandoffOwnerAbortEvidence.verifyHistoricalOwners
                    connection
                    transaction
                    witness
                    value
                    row.Canonical
                    signatureOne
                    signatureTwo
                    ct
        }

    let verify connection transaction witness cutoff (ct: CancellationToken) =
        task {
            let mutable after = 0L
            let mutable more = true
            let mutable count = 0L

            while more do
                let! rows = page connection transaction after ct

                for row in rows do
                    if row.Sequence <= after then
                        corrupt ()

                    do! verifyRow connection transaction witness cutoff row ct
                    after <- row.Sequence
                    count <- count + 1L

                more <- rows.Length = 50

            let! snapshot = witness.Snapshot(ct)

            let primaryAbort =
                use command =
                    new NpgsqlCommand(
                        "SELECT last_aborted_handoff_id,last_aborted_handoff_sequence,"
                        + "last_aborted_handoff_hash FROM claimcore.installation_lineage WHERE singleton",
                        connection,
                        transaction
                    )

                use reader = command.ExecuteReader()

                if not (reader.Read()) then
                    corrupt ()

                let id = if reader.IsDBNull(0) then None else Some(reader.GetGuid(0))

                let sequence =
                    if reader.IsDBNull(1) then
                        None
                    else
                        Some(reader.GetInt64(1))

                let hash =
                    if reader.IsDBNull(2) then
                        None
                    else
                        Some(reader.GetFieldValue<byte array>(2))

                if reader.Read() then
                    corrupt ()

                id, sequence, hash

            if
                not snapshot.HandoffPending
                && primaryAbort
                   <> (snapshot.LastAbortedHandoffId,
                       snapshot.LastAbortedHandoffSequence,
                       snapshot.LastAbortedHandoffHash)
            then
                corrupt ()

            return count
        }
