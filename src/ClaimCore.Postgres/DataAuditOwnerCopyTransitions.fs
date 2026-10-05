namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Witness
open DataAuditCommon

[<NoEquality; NoComparison>]
type internal OwnerCopyProjection =
    {
        CopyId: Guid
        Registered: ManagedCopyAttestation
        RegistrationHash: byte array
        State: string
        Revision: int64
        EventHash: byte array
        VerificationProofSha256: byte array option
        LastVerifiedAt: DateTimeOffset option
        DeletionProofSha256: byte array option
    }

[<NoEquality; NoComparison>]
type internal OwnerCopyReplay =
    {
        Revision: int64
        State: string
        EventHash: byte array
        VerificationProofSha256: byte array option
        DeletionProofSha256: byte array option
        LastVerifiedAt: DateTimeOffset option
    }

/// Replays every owner-signed, witnessed transition under the caller's fenced snapshot.
module internal DataAuditOwnerCopyTransitions =
    let private query =
        "SELECT e.event_id,e.revision,e.event_kind,e.canonical_attestation,"
        + "e.ed25519_signature,e.signing_key_id,e.candidate_sha256,e.previous_hash,"
        + "e.event_hash,e.witness_sequence,e.witness_epoch,e.witness_entry_hash,"
        + "s.ed25519_public_key,s.public_key_sha256,"
        + "(SELECT r.witness_sequence FROM claimcore.managed_copy_signer_events r "
        + "WHERE r.signing_key_id=e.signing_key_id AND r.revision=1),"
        + "(SELECT r.witness_sequence FROM claimcore.managed_copy_signer_events r "
        + "WHERE r.signing_key_id=e.signing_key_id AND r.revision=2),u.approval_id,s.signer_purpose "
        + "FROM claimcore.managed_copy_events e JOIN claimcore.managed_copy_signers s "
        + "ON s.signing_key_id=e.signing_key_id "
        + "LEFT JOIN claimcore.managed_copy_deletion_approval_uses u "
        + "ON u.deletion_event_id=e.event_id "
        + "WHERE e.copy_id=@copy AND e.producer_kind='OWNER_ATTESTED' "
        + "AND e.revision>@after ORDER BY e.revision LIMIT 100"

    let private bytes (reader: NpgsqlDataReader) index = reader.GetFieldValue<byte array>(index)

    let private identityMatches
        (projection: OwnerCopyProjection)
        (next: ManagedCopyTransition)
        (reader: NpgsqlDataReader)
        previousRevision
        =
        next.Copy.CopyId = projection.CopyId
        && ManagedCopyTransitionPolicy.sameCopy projection.Registered next.Copy
        && next.Copy.EventId = reader.GetGuid(0)
        && next.Revision = previousRevision + 1L
        && next.Revision = reader.GetInt64(1)
        && next.EventKind = reader.GetString(2)
        && next.Copy.SigningKeyId = reader.GetGuid(5)

    let private proofMatches
        (reader: NpgsqlDataReader)
        (next: ManagedCopyTransition)
        (canonical: byte array)
        (signature: byte array)
        (previousHash: byte array)
        (eventHash: byte array)
        (candidate: byte array)
        =
        next.PreviousEventHash = previousHash
        && bytes reader 7 = previousHash
        && eventHash = bytes reader 8
        && SHA256.HashData(candidate) = bytes reader 6
        && SHA256.HashData(bytes reader 12) = bytes reader 13
        && reader.GetString(17) = "COPY_ATTESTOR"
        && ManagedCopySignature.verify (bytes reader 12) canonical signature

    let private sequenceMatches (witness: WitnessProtocol) cutoff (reader: NpgsqlDataReader) =
        let sequence = reader.GetInt64(9)

        sequence > reader.GetInt64(14)
        && (reader.IsDBNull(15) || sequence < reader.GetInt64(15))
        && sequence <= cutoff
        && reader.GetInt64(10) = witness.Identity.Epoch

    let private transitionAllowed
        previousState
        (previousProof: byte array option)
        (previousVerified: DateTimeOffset option)
        (next: ManagedCopyTransition)
        (reader: NpgsqlDataReader)
        =
        let useId =
            if reader.IsDBNull(16) then
                None
            else
                Some(reader.GetGuid(16))

        match previousState, next.EventKind, next.State with
        | "UNVERIFIED", "VERIFY", "RETAINED" ->
            previousProof.IsNone
            && previousVerified.IsNone
            && next.Copy.VerificationProofSha256.IsSome
            && next.LastVerifiedAt.IsSome
            && next.DeletionProofSha256.IsNone
            && next.DeletionApprovalId.IsNone
            && useId.IsNone
        | ("UNVERIFIED" | "RETAINED"), "DELETE_REQUEST", "DELETE_PENDING"
        | ("UNVERIFIED" | "RETAINED" | "DELETE_PENDING"), "UNKNOWN", "UNKNOWN" ->
            next.Copy.VerificationProofSha256 = previousProof
            && next.LastVerifiedAt = previousVerified
            && next.DeletionProofSha256.IsNone
            && useId.IsNone
        | "DELETE_PENDING", "VERIFIED_DELETED", "VERIFIED_DELETED" ->
            next.Copy.VerificationProofSha256 = previousProof
            && next.DeletionProofSha256.IsSome
            && next.LastVerifiedAt.IsSome
            && next.DeletionApprovalId = useId
        | _ -> false

    let private verifyEvent
        (witness: WitnessProtocol)
        cutoff
        (projection: OwnerCopyProjection)
        (reader: NpgsqlDataReader)
        (previous: OwnerCopyReplay)
        =
        let canonical = bytes reader 3
        let signature = bytes reader 4

        let next =
            ManagedCopyTransitionAttestation.parse canonical |> Option.defaultWith corrupt

        let eventHash =
            ManagedCopyEventHash.compute previous.EventHash canonical (Some signature)

        let candidate = ManagedCopyTransitionPolicy.candidate next canonical signature

        try
            if
                not (identityMatches projection next reader previous.Revision)
                || not (
                    proofMatches
                        reader
                        next
                        canonical
                        signature
                        previous.EventHash
                        eventHash
                        candidate
                )
                || not (sequenceMatches witness cutoff reader)
                || not (
                    transitionAllowed
                        previous.State
                        previous.VerificationProofSha256
                        previous.LastVerifiedAt
                        next
                        reader
                )
            then
                corrupt ()

            let replay =
                {
                    Revision = next.Revision
                    State = next.State
                    EventHash = eventHash
                    VerificationProofSha256 = next.Copy.VerificationProofSha256
                    DeletionProofSha256 = next.DeletionProofSha256
                    LastVerifiedAt = next.LastVerifiedAt
                }

            replay, DataAuditOwnerCopyWitnessProof.fromRow reader next
        finally
            CryptographicOperations.ZeroMemory(candidate)

    let private projectionMatches (projection: OwnerCopyProjection) (replay: OwnerCopyReplay) =
        replay.Revision = projection.Revision
        && replay.State = projection.State
        && replay.EventHash = projection.EventHash
        && replay.VerificationProofSha256 = projection.VerificationProofSha256
        && replay.DeletionProofSha256 = projection.DeletionProofSha256
        && replay.LastVerifiedAt = projection.LastVerifiedAt

    let verify
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        cutoff
        (projection: OwnerCopyProjection)
        (ct: CancellationToken)
        =
        task {
            let mutable replay =
                {
                    Revision = 1L
                    State = "UNVERIFIED"
                    EventHash = projection.RegistrationHash
                    VerificationProofSha256 = None
                    DeletionProofSha256 = None
                    LastVerifiedAt = None
                }

            let mutable reading = true

            while reading do
                use command = new NpgsqlCommand(query, connection, transaction)
                Sql.uuid command "copy" projection.CopyId
                Sql.integer command "after" replay.Revision

                let! proofs =
                    task {
                        use! reader = command.ExecuteReaderAsync(ct)
                        let page = ResizeArray<OwnerCopyTransitionProof>()

                        while reader.Read() do
                            let next, proof = verifyEvent witness cutoff projection reader replay
                            replay <- next
                            page.Add proof

                        return page.ToArray()
                    }

                for proof in proofs do
                    do!
                        DataAuditOwnerCopyWitnessProof.verify
                            connection
                            transaction
                            witness
                            cutoff
                            proof
                            ct

                reading <- proofs.Length = 100

            if not (projectionMatches projection replay) then
                corrupt ()
        }
