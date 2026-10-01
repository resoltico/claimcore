namespace ClaimCore.Postgres

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.Witness

/// The service composes this with a separately administered witness connection and private key.
/// Its payload is encrypted before the witness database receives claimant-bearing bytes.
type internal WitnessProtocol
    (store: Store, custody: IKeyCustody, identity: Identity, beforeSettlement: unit -> unit) =
    let associatedData = WitnessProof.associatedData identity
    let settlementName = WitnessProof.settlementName
    let verifyEvidence = WitnessProof.verifyEvidence store custody identity

    let verifySettlement operationId phase (intent: WitnessIntent) (evidence: Evidence) =
        let ticket = evidence.Ticket
        let aad = associatedData operationId (settlementName phase)
        let plain = custody.Decrypt(ticket.KeyId, aad, evidence.EncryptedPayload)

        try
            if
                ticket.OperationId <> operationId
                || ticket.Phase <> phase
                || ticket.Epoch <> intent.Ticket.Epoch
                || ticket.KeyId <> intent.Ticket.KeyId
                || ticket.ScopeKind <> intent.Ticket.ScopeKind
                || ticket.SubjectCaseId <> intent.Ticket.SubjectCaseId
                || ticket.Sequence <= intent.Ticket.Sequence
                || plain <> intent.CandidateHash
            then
                raise WitnessPending

            ticket
        finally
            CryptographicOperations.ZeroMemory(plain)

    let settle operationId phase (intent: WitnessIntent) =
        let existing = store.TryReadEvidence(operationId, phase)

        match existing with
        | Some evidence -> verifySettlement operationId phase intent evidence
        | None ->
            beforeSettlement ()
            let aad = associatedData operationId (settlementName phase)
            let encrypted = custody.Encrypt(intent.Ticket.KeyId, aad, intent.CandidateHash)

            try
                store.Append(operationId, None, phase, intent.Ticket.KeyId, encrypted)
            with _ ->
                // A concurrent exact settlement may have won after the initial read.
                // Only an independent committed readback can turn this into a definite result.
                try
                    store.TryReadEvidence(operationId, phase)
                    |> Option.defaultWith (fun () -> raise WitnessPending)
                    |> verifySettlement operationId phase intent
                with _ ->
                    raise WitnessPending

    new(store: Store, custody: IKeyCustody, identity: Identity) =
        new WitnessProtocol(store, custody, identity, fun () -> ())

    member _.Identity = identity
    member internal _.EvidenceStore = store
    member internal _.KeyCustody = custody
    member internal _.AssociatedData(operation, phase) = associatedData operation phase
    member _.Snapshot() = store.Snapshot()
    member internal _.ReadDataUseActivation() = store.ReadDataUseActivation()

    member _.AcquireReadFence(expectedGeneration) =
        store.AcquireReadFence(expectedGeneration)

    member _.ReadPage(afterSequence, expectedPreviousHash, cutoffSequence, limit) =
        store.ReadPage(afterSequence, expectedPreviousHash, cutoffSequence, limit)

    member _.TryReadHashAtSequence(sequence: int64) =
        store.TryReadVerifiedEntryHash(sequence)

    /// Read-only full-audit proof; this never appends a missing settlement.
    member _.VerifyAcceptedEvidence(operationId, sequence, epoch, entryHash, candidateDigest) =
        verifyEvidence operationId SettledAccepted sequence epoch entryHash candidateDigest
        |> ignore

    member _.VerifyAcceptedEvidenceForCase
        (operationId, sequence, epoch, entryHash, candidateDigest, caseId)
        =
        verifyEvidence operationId SettledAccepted sequence epoch entryHash candidateDigest
        |> WitnessProof.requireScope (Some caseId)

    member _.VerifyRevokedEvidence(operationId, sequence, epoch, entryHash, candidateDigest) =
        verifyEvidence operationId SettledRevoked sequence epoch entryHash candidateDigest
        |> ignore

    member _.VerifyRevokedEvidenceForCase
        (operationId, sequence, epoch, entryHash, candidateDigest, caseId)
        =
        verifyEvidence operationId SettledRevoked sequence epoch entryHash candidateDigest
        |> WitnessProof.requireScope (Some caseId)

    member _.VerifyAuthorityEvidence(operationId, sequence, epoch, entryHash, candidateDigest) =
        verifyEvidence operationId SettledAuthority sequence epoch entryHash candidateDigest
        |> ignore

    member _.VerifyAuthorityEvidenceForCase
        (operationId, sequence, epoch, entryHash, candidateDigest, caseId)
        =
        verifyEvidence operationId SettledAuthority sequence epoch entryHash candidateDigest
        |> WitnessProof.requireScope (Some caseId)

    member _.VerifyAuthorityEvidenceForInstallation
        (operationId, sequence, epoch, entryHash, candidateDigest)
        =
        verifyEvidence operationId SettledAuthority sequence epoch entryHash candidateDigest
        |> WitnessProof.requireScope None

    member _.VerifyKeyRotated(record: JournalRecord, expectedOldKeyId: Guid) =
        let ticket = record.Evidence.Ticket

        if
            ticket.Phase <> KeyRotated
            || ticket.ScopeKind <> Installation
            || ticket.SubjectCaseId.IsSome
            || ticket.KeyId = expectedOldKeyId
            || not (custody.HasKey ticket.KeyId)
        then
            raise WitnessPending

        KeyCheck.verifyRotation
            custody
            identity.InstallationId
            identity.LineageId
            ticket.Epoch
            ticket.OperationId
            expectedOldKeyId
            ticket.KeyId
            record.Evidence.EncryptedPayload

        ticket.KeyId

    member _.Admit() =
        store.Admit()
        WitnessCustodyAdmission.verify store custody identity

    member _.AdmitReadOnly() =
        store.AdmitReadOnly()
        WitnessCustodyAdmission.verify store custody identity

    member _.BeginRevocation
        (operationId: Guid, requestSha256: string, actorEvidence: RevocationActorEvidence)
        =
        let eventId = WitnessEventIdentity.revocationEventId operationId

        try
            if store.TryReadEvidence(eventId, Intent).IsSome then
                raise WitnessPending
        with _ ->
            raise WitnessPending

        let plain = WitnessCandidate.revoked operationId requestSha256 actorEvidence

        try
            let digest = SHA256.HashData(plain)
            let keyId = custody.ActiveKeyId
            let encrypted = custody.Encrypt(keyId, associatedData eventId "INTENT", plain)

            let ticket =
                try
                    store.Append(eventId, Some actorEvidence.CaseId, Intent, keyId, encrypted)
                with _ ->
                    raise WitnessPending

            {
                Ticket = ticket
                CandidateHash = digest
            }
        finally
            CryptographicOperations.ZeroMemory(plain)

    member _.BeginAuthority
        (operationId: Guid, canonicalActionBytes: byte array, subjectCaseId: Guid option)
        =
        if
            operationId = Guid.Empty
            || canonicalActionBytes.Length = 0
            || canonicalActionBytes.Length > 1040000
        then
            invalidArg (nameof canonicalActionBytes) "Witness authority candidate is invalid."

        try
            if store.TryReadEvidence(operationId, Intent).IsSome then
                raise WitnessPending
        with _ ->
            raise WitnessPending

        let digest = SHA256.HashData(canonicalActionBytes)
        let keyId = custody.ActiveKeyId

        let encrypted =
            custody.Encrypt(keyId, associatedData operationId "INTENT", canonicalActionBytes)

        let ticket =
            try
                store.Append(operationId, subjectCaseId, Intent, keyId, encrypted)
            with _ ->
                raise WitnessPending

        {
            Ticket = ticket
            CandidateHash = digest
        }

    member _.SettleAccepted(operationId: Guid, intent: WitnessIntent) =
        settle operationId SettledAccepted intent

    member _.SettleRevoked(operationId: Guid, intent: WitnessIntent) =
        settle operationId SettledRevoked intent

    member _.SettleAuthority(operationId: Guid, intent: WitnessIntent) =
        settle operationId SettledAuthority intent

    member _.RequireSettled(operationId: Guid, settlement: Phase) =
        let intent =
            store.TryReadEvidence(operationId, Intent)
            |> Option.defaultWith (fun () -> raise WitnessPending)

        let outcome =
            store.TryReadEvidence(operationId, settlement)
            |> Option.defaultWith (fun () -> raise WitnessPending)

        if outcome.Ticket.Sequence <= intent.Ticket.Sequence then
            raise WitnessPending

        let first =
            custody.Decrypt(
                intent.Ticket.KeyId,
                associatedData operationId "INTENT",
                intent.EncryptedPayload
            )

        try
            let second =
                custody.Decrypt(
                    outcome.Ticket.KeyId,
                    associatedData operationId (settlementName settlement),
                    outcome.EncryptedPayload
                )

            try
                if SHA256.HashData(first) <> second then
                    raise WitnessPending
            finally
                CryptographicOperations.ZeroMemory(second)
        finally
            CryptographicOperations.ZeroMemory(first)

    interface IDisposable with
        member _.Dispose() =
            (store :> IDisposable).Dispose()
            custody.Dispose()
