namespace ClaimCore.Postgres

open System
open System.Threading
open System.IO
open System.Security.Cryptography
open System.Text.Json
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain

[<NoEquality; NoComparison>]
type private ExistingTerminalApproval =
    {
        ApprovalId: Guid
        ActorId: Guid
        GrantRevision: int64
        ApprovedAt: DateTimeOffset
        ExpiresAt: DateTimeOffset
        Canonical: byte array
        CandidateHash: byte array
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessHash: byte array
    }

/// All approval slots for one owner event must attest the identical tagged proposal, not merely
/// share an event ID. Read at most the two allowed slots plus one corruption sentinel.
module internal CaseTombstoneTerminalApprovalSet =
    let private rows connection transaction eventId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT approval_id,approver_actor_id,approver_grant_revision,"
                    + "approved_at,expires_at,canonical_action,candidate_sha256,"
                    + "witness_sequence,approval_witness_epoch,witness_entry_hash "
                    + "FROM claimcore.case_erasure_terminal_approvals "
                    + "WHERE terminal_event_id=@event ORDER BY approval_id LIMIT 3",
                    connection,
                    transaction
                )

            Sql.uuid command "event" eventId
            use! reader = command.ExecuteReaderAsync()
            let values = ResizeArray<ExistingTerminalApproval>()

            while reader.Read() do
                values.Add
                    {
                        ApprovalId = reader.GetGuid(0)
                        ActorId = reader.GetGuid(1)
                        GrantRevision = reader.GetInt64(2)
                        ApprovedAt = reader.GetFieldValue<DateTimeOffset>(3)
                        ExpiresAt = reader.GetFieldValue<DateTimeOffset>(4)
                        Canonical = reader.GetFieldValue<byte array>(5)
                        CandidateHash = reader.GetFieldValue<byte array>(6)
                        WitnessSequence = reader.GetInt64(7)
                        WitnessEpoch = reader.GetInt64(8)
                        WitnessHash = reader.GetFieldValue<byte array>(9)
                    }

            return values |> Seq.toList
        }

    let private proposalBytes (canonical: byte array) =
        try
            use document = JsonDocument.Parse(ReadOnlyMemory<byte>(canonical))
            let root = document.RootElement

            if
                root.GetProperty("kind").GetString() <> "CASE_TERMINAL_ERASURE_APPROVAL"
                || root.GetProperty("version").GetInt32() <> 1
            then
                raise (InvalidDataException("Terminal approval candidate is malformed."))

            root.GetProperty("proposal").GetBytesFromBase64()
        with _ ->
            raise (InvalidDataException("Terminal approval candidate is malformed."))

    let private requireCanonical proposal (row: ExistingTerminalApproval) =
        let canonical =
            CaseTombstoneTerminalCandidate.approval
                proposal
                row.ApprovalId
                row.ActorId
                row.GrantRevision
                row.ApprovedAt
                row.ExpiresAt

        try
            if row.Canonical <> canonical then
                raise (InvalidDataException("Terminal approval canonical bytes diverged."))
        finally
            CryptographicOperations.ZeroMemory(canonical)

    let private same
        (witness: WitnessProtocol)
        caseId
        expected
        proposal
        (row: ExistingTerminalApproval)
        ct
        =
        task {
            if row.CandidateHash <> SHA256.HashData(row.Canonical) then
                raise (InvalidDataException("Terminal approval candidate hash diverged."))

            let embedded = proposalBytes row.Canonical

            try
                if embedded <> expected then
                    return false
                else
                    requireCanonical proposal row

                    do!
                        witness.VerifyAuthorityEvidenceForCase(
                            row.ApprovalId,
                            row.WitnessSequence,
                            row.WitnessEpoch,
                            row.WitnessHash,
                            row.CandidateHash,
                            caseId,
                            ct
                        )

                    return true
            finally
                CryptographicOperations.ZeroMemory(embedded)
        }

    let matches connection transaction witness proposal ct =
        task {
            let value = TombstoneTerminalProposal.copy proposal
            let expected = CaseTombstoneTerminalCandidate.proposal proposal

            try
                let! existing = rows connection transaction value.EventId

                if existing.Length > 2 then
                    raise (InvalidDataException("Terminal approval capacity was exceeded."))

                let mutable matching = true

                for row in existing do
                    if matching then
                        let! accepted = same witness value.CaseId expected proposal row ct
                        matching <- accepted

                return matching
            finally
                CryptographicOperations.ZeroMemory(expected)
        }

    let available
        connection
        transaction
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        proposal
        ct
        =
        task {
            let value = TombstoneTerminalProposal.copy proposal
            let! holds = CaseTombstoneRead.activeHolds connection transaction value.CaseId

            let! approvers =
                CaseTombstoneTerminalRead.approvers connection transaction value.EventId

            let! sameDraft = matches connection transaction witness proposal ct

            if not holds.IsEmpty then
                return Error LifecycleRefusal.HoldActive
            elif not sameDraft then
                return Error LifecycleRefusal.ApprovalMismatch
            elif approvers |> List.contains context.Binding.ActorId then
                return Error LifecycleRefusal.ApprovalMismatch
            elif approvers.Length >= 2 then
                return Error LifecycleRefusal.ApprovalCapacityExceeded
            else
                do!
                    witness.RequireSettled(
                        value.PruneEventId,
                        ClaimCore.Witness.SettledAuthority,
                        ct
                    )

                return Ok()
        }
