namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Witness
open DataAuditCommon
open WitnessProtocolReconciliation

/// Pages and replays every post-adoption event through the final projection.
module internal DataAuditAdoptedCopyTransitions =
    let private transitionAllowed
        (previousState: string)
        (previousProof: byte array option)
        (previousVerified: DateTimeOffset option)
        (next: AdoptedCopyTransition)
        (useId: Guid option)
        =
        let carries = next.VerificationProofSha256 = previousProof

        match previousState, next.EventKind, next.State with
        | ("UNVERIFIED" | "UNKNOWN" | "RETAINED"), "DELETE_REQUEST", "DELETE_PENDING" ->
            carries
            && next.LastVerifiedAt = previousVerified
            && next.DeletionProofSha256.IsNone
            && useId.IsNone
        | ("UNVERIFIED" | "RETAINED" | "DELETE_PENDING" | "UNKNOWN"), "UNKNOWN", "UNKNOWN" ->
            carries
            && next.LastVerifiedAt = previousVerified
            && next.DeletionProofSha256.IsNone
            && useId.IsNone
        | "DELETE_PENDING", "VERIFIED_DELETED", "VERIFIED_DELETED" ->
            carries
            && next.DeletionProofSha256.IsSome
            && next.LastVerifiedAt.IsSome
            && next.DeletionApprovalId = useId
        | _ -> false

    let private identityMatches
        (origin: VerifiedCopyAdoptionOrigin)
        (next: AdoptedCopyTransition)
        (row: AdoptedCopyEventRow)
        previousRevision
        =
        ManagedCopyAdoptedTransitionPolicy.identity origin next
        && row.ProducerKind = origin.ProducerKind
        && row.SigningKeyId = origin.CustodianSigningKeyId
        && row.Signature.Length = 64
        && row.EventId = next.EventId
        && row.Revision = previousRevision + 1L
        && row.Revision = next.Revision
        && row.EventKind = next.EventKind

    let private historyMatches
        (next: AdoptedCopyTransition)
        (row: AdoptedCopyEventRow)
        previousHash
        expectedHash
        (candidate: byte array)
        =
        row.PreviousHash = previousHash
        && next.PreviousEventHash = previousHash
        && row.EventHash = expectedHash
        && row.CandidateDigest = SHA256.HashData(candidate)

    let private witnessMatches
        (witness: WitnessProtocol)
        cutoff
        (origin: VerifiedCopyAdoptionOrigin)
        (next: AdoptedCopyTransition)
        (row: AdoptedCopyEventRow)
        =
        row.WitnessSequence > origin.WitnessSequence
        && row.WitnessSequence > next.ActionWitnessCutoffSequence
        && row.WitnessSequence <= cutoff
        && row.WitnessEpoch = witness.Identity.Epoch

    let private verifyAuthority
        connection
        transaction
        (witness: WitnessProtocol)
        cutoff
        (origin: VerifiedCopyAdoptionOrigin)
        (next: AdoptedCopyTransition)
        (row: AdoptedCopyEventRow)
        (ct: CancellationToken)
        =
        task {
            let! signer =
                DataAuditCopyAdoptionSignerRole.read
                    connection
                    transaction
                    row.SigningKeyId
                    "COPY_ATTESTOR"
                    row.WitnessSequence
                    ct

            if
                signer.Holder <> origin.CustodianHolderActorId
                || not (ManagedCopySignature.verify signer.PublicKey row.Canonical row.Signature)
            then
                corrupt ()

            witnessProof (fun () ->
                witness.VerifyHistoricalTip(
                    next.ActionWitnessCutoffSequence,
                    next.ActionWitnessCutoffHash
                ))

            do!
                CaseWitnessAuditEvidence.verify
                    connection
                    transaction
                    witness
                    cutoff
                    origin.CaseId
                    row.EventId
                    row.WitnessSequence
                    row.WitnessEpoch
                    row.WitnessEntryHash
                    row.CandidateDigest
                    SettledAuthority
        }

    let private verifyRow
        connection
        transaction
        (witness: WitnessProtocol)
        cutoff
        (origin: VerifiedCopyAdoptionOrigin)
        previousRevision
        previousHash
        previousState
        previousProof
        previousVerified
        (row: AdoptedCopyEventRow)
        (ct: CancellationToken)
        =
        task {
            let next =
                ManagedCopyAdoptedTransitionAttestation.parse row.Canonical
                |> Option.defaultWith corrupt

            let candidate =
                ManagedCopyAdoptedTransitionPolicy.candidate next row.Canonical row.Signature

            try
                let expectedHash =
                    ManagedCopyEventHash.compute previousHash row.Canonical (Some row.Signature)

                if
                    not (identityMatches origin next row previousRevision)
                    || not (historyMatches next row previousHash expectedHash candidate)
                    || not (witnessMatches witness cutoff origin next row)
                    || not (
                        transitionAllowed
                            previousState
                            previousProof
                            previousVerified
                            next
                            row.ApprovalUseId
                    )
                then
                    corrupt ()

                do! verifyAuthority connection transaction witness cutoff origin next row ct

                return next, expectedHash
            finally
                CryptographicOperations.ZeroMemory(candidate)
        }

    let private projection
        connection
        transaction
        (origin: VerifiedCopyAdoptionOrigin)
        revision
        hash
        state
        proof
        verified
        deletion
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT revision,event_hash,state,verification_proof_sha256,"
                    + "last_verified_at,deletion_proof_sha256 FROM claimcore.managed_copies "
                    + "WHERE copy_id=@copy AND producer_kind=@producer AND source_case_id=@case",
                    connection,
                    transaction
                )

            Sql.uuid command "copy" origin.CopyId
            Sql.text command "producer" origin.ProducerKind
            Sql.uuid command "case" origin.CaseId
            use! reader = command.ExecuteReaderAsync()

            if
                not (reader.Read())
                || reader.GetInt64(0) <> revision
                || reader.GetFieldValue<byte array>(1) <> hash
                || reader.GetString(2) <> state
                || (if reader.IsDBNull(3) then
                        None
                    else
                        Some(reader.GetFieldValue<byte array>(3)))
                   <> proof
                || (if reader.IsDBNull(4) then
                        None
                    else
                        Some(reader.GetFieldValue<DateTimeOffset>(4)))
                   <> verified
                || (if reader.IsDBNull(5) then
                        None
                    else
                        Some(reader.GetFieldValue<byte array>(5)))
                   <> deletion
                || reader.Read()
            then
                corrupt ()
        }

    let verify
        connection
        transaction
        (witness: WitnessProtocol)
        cutoff
        (origin: VerifiedCopyAdoptionOrigin)
        (ct: CancellationToken)
        =
        task {
            let mutable revision = origin.CopyRevision
            let mutable hash = origin.CopyEventHash

            let mutable state =
                if origin.ProducerKind = "PRODUCT_EXPORT" then
                    "UNVERIFIED"
                else
                    "UNKNOWN"

            let mutable proof: byte array option = None
            let mutable verified: DateTimeOffset option = None
            let mutable deletion: byte array option = None
            let mutable reading = true

            while reading do
                let! page =
                    DataAuditAdoptedCopyRows.page connection transaction origin.CopyId revision ct

                for row in page do
                    let! next, eventHash =
                        verifyRow
                            connection
                            transaction
                            witness
                            cutoff
                            origin
                            revision
                            hash
                            state
                            proof
                            verified
                            row
                            ct

                    revision <- row.Revision
                    hash <- eventHash
                    state <- next.State
                    proof <- next.VerificationProofSha256
                    verified <- next.LastVerifiedAt
                    deletion <- next.DeletionProofSha256

                reading <- page.Length = 100

            do! projection connection transaction origin revision hash state proof verified deletion
        }
