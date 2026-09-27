namespace ClaimCore.Postgres

open System
open System.Buffers.Binary
open System.Text

/// Signed adopted-copy transition identity, state graph and exact witness candidate.
module internal ManagedCopyAdoptedTransitionPolicy =
    let candidate (value: AdoptedCopyTransition) (canonical: byte array) (signature: byte array) =
        let length = Array.zeroCreate<byte> 4
        BinaryPrimitives.WriteInt32BigEndian(length, canonical.Length)

        Array.concat
            [
                Encoding.ASCII.GetBytes("CLAIMCORE_ADOPTED_MANAGED_COPY_TRANSITION_V1\000")
                value.EventId.ToByteArray()
                value.AdoptionEventId.ToByteArray()
                length
                canonical
                signature
            ]

    let identity (origin: VerifiedCopyAdoptionOrigin) (value: AdoptedCopyTransition) =
        value.CopyId = origin.CopyId
        && value.SourceCaseId = origin.CaseId
        && value.AdoptionEventId = origin.AdoptionEventId
        && value.ProducerKind = origin.ProducerKind
        && value.OriginEventHash = origin.CopyEventHash
        && value.Revision > origin.CopyRevision
        && value.ActionWitnessCutoffSequence >= origin.WitnessSequence

    let allowed
        previousState
        previousProof
        previousVerified
        retainUntil
        (value: AdoptedCopyTransition)
        now
        held
        =
        let carries =
            value.VerificationProofSha256 = previousProof
            && value.LastVerifiedAt = previousVerified

        match value.EventKind, value.State with
        | "DELETE_REQUEST", "DELETE_PENDING" ->
            (previousState = "UNVERIFIED"
             || previousState = "UNKNOWN"
             || previousState = "RETAINED")
            && now >= retainUntil
            && not held
            && carries
        | "UNKNOWN", "UNKNOWN" ->
            (previousState = "UNVERIFIED"
             || previousState = "RETAINED"
             || previousState = "DELETE_PENDING"
             || previousState = "UNKNOWN")
            && carries
        | "VERIFIED_DELETED", "VERIFIED_DELETED" ->
            previousState = "DELETE_PENDING"
            && not held
            && now >= retainUntil
            && value.VerificationProofSha256 = previousProof
        | _ -> false
