namespace ClaimCore.Postgres

open System
open System.Threading
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
    (
        store: Store,
        custody: IKeyCustody,
        identity: Identity,
        beforeSettlement: unit -> unit,
        observer: WitnessFailureStage -> WitnessFailureCause -> unit
    ) =
    let associatedData = WitnessProof.associatedData identity
    let verifyEvidence = WitnessProof.verifyEvidence store custody identity

    let settle operationId phase intent =
        WitnessSettlement.settle
            store
            custody
            associatedData
            beforeSettlement
            observer
            operationId
            phase
            intent

    new(store: Store, custody: IKeyCustody, identity: Identity, beforeSettlement: unit -> unit) =
        new WitnessProtocol(store, custody, identity, beforeSettlement, fun _ _ -> ())

    new(store: Store, custody: IKeyCustody, identity: Identity) =
        new WitnessProtocol(store, custody, identity, fun () -> ())

    member _.Identity = identity
    member internal _.EvidenceStore = store
    member internal _.KeyCustody = custody
    member internal _.AssociatedData(operation, phase) = associatedData operation phase
    member _.Snapshot(ct: CancellationToken) = store.Snapshot(ct)
    member internal _.ReadDataUseActivation(ct: CancellationToken) = store.ReadDataUseActivation(ct)

    member _.AcquireReadFence(expectedGeneration, ct: CancellationToken) =
        store.AcquireReadFence(expectedGeneration, ct)

    member _.ReadPage
        (afterSequence, expectedPreviousHash, cutoffSequence, limit, ct: CancellationToken)
        =
        store.ReadPage(afterSequence, expectedPreviousHash, cutoffSequence, limit, ct)

    member _.TryReadHashAtSequence(sequence: int64, ct: CancellationToken) =
        store.TryReadVerifiedEntryHash(sequence, ct)

    /// Read-only full-audit proof; this never appends a missing settlement.
    member _.VerifyAcceptedEvidence
        (operationId, sequence, epoch, entryHash, candidateDigest, ct: CancellationToken)
        =
        task {
            let! _ =
                verifyEvidence
                    operationId
                    SettledAccepted
                    sequence
                    epoch
                    entryHash
                    candidateDigest
                    ct

            return ()
        }

    member _.VerifyAcceptedEvidenceForCase
        (operationId, sequence, epoch, entryHash, candidateDigest, caseId, ct: CancellationToken)
        =
        task {
            let! evidence =
                verifyEvidence
                    operationId
                    SettledAccepted
                    sequence
                    epoch
                    entryHash
                    candidateDigest
                    ct

            return WitnessProof.requireScope (Some caseId) evidence
        }

    member _.VerifyRevokedEvidence
        (operationId, sequence, epoch, entryHash, candidateDigest, ct: CancellationToken)
        =
        task {
            let! _ =
                verifyEvidence
                    operationId
                    SettledRevoked
                    sequence
                    epoch
                    entryHash
                    candidateDigest
                    ct

            return ()
        }

    member _.VerifyRevokedEvidenceForCase
        (operationId, sequence, epoch, entryHash, candidateDigest, caseId, ct: CancellationToken)
        =
        task {
            let! evidence =
                verifyEvidence
                    operationId
                    SettledRevoked
                    sequence
                    epoch
                    entryHash
                    candidateDigest
                    ct

            return WitnessProof.requireScope (Some caseId) evidence
        }

    member _.VerifyAuthorityEvidence
        (operationId, sequence, epoch, entryHash, candidateDigest, ct: CancellationToken)
        =
        task {
            let! _ =
                verifyEvidence
                    operationId
                    SettledAuthority
                    sequence
                    epoch
                    entryHash
                    candidateDigest
                    ct

            return ()
        }

    member _.VerifyAuthorityEvidenceForCase
        (operationId, sequence, epoch, entryHash, candidateDigest, caseId, ct: CancellationToken)
        =
        task {
            let! evidence =
                verifyEvidence
                    operationId
                    SettledAuthority
                    sequence
                    epoch
                    entryHash
                    candidateDigest
                    ct

            return WitnessProof.requireScope (Some caseId) evidence
        }

    member _.VerifyAuthorityEvidenceForInstallation
        (operationId, sequence, epoch, entryHash, candidateDigest, ct: CancellationToken)
        =
        task {
            let! evidence =
                verifyEvidence
                    operationId
                    SettledAuthority
                    sequence
                    epoch
                    entryHash
                    candidateDigest
                    ct

            return WitnessProof.requireScope (None) evidence
        }

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

    member _.Admit(ct: CancellationToken) =
        task {
            do! store.Admit(ct)
            do! WitnessCustodyAdmission.verify store custody identity ct
        }

    member _.AdmitReadOnly(ct: CancellationToken) =
        task {
            do! store.AdmitReadOnly(ct)
            do! WitnessCustodyAdmission.verify store custody identity ct
        }

    member _.BeginRevocation
        (
            operationId: Guid,
            requestSha256: string,
            actorEvidence: RevocationActorEvidence,
            ct: CancellationToken
        ) =
        task {
            let eventId = WitnessEventIdentity.revocationEventId operationId
            let plain = WitnessCandidate.revoked operationId requestSha256 actorEvidence

            try
                return!
                    WitnessIntentAdmission.beginIntent
                        store
                        custody
                        associatedData
                        observer
                        eventId
                        (Some actorEvidence.CaseId)
                        plain
                        ct
            finally
                CryptographicOperations.ZeroMemory(plain)
        }

    member _.BeginAuthority
        (
            operationId: Guid,
            canonicalActionBytes: byte array,
            subjectCaseId: Guid option,
            ct: CancellationToken
        ) =
        if
            operationId = Guid.Empty
            || canonicalActionBytes.Length = 0
            || canonicalActionBytes.Length > 1040000
        then
            invalidArg (nameof canonicalActionBytes) "Witness authority candidate is invalid."

        WitnessIntentAdmission.beginIntent
            store
            custody
            associatedData
            observer
            operationId
            subjectCaseId
            canonicalActionBytes
            ct

    member _.SettleAccepted(operationId: Guid, intent: WitnessIntent) =
        settle operationId SettledAccepted intent

    member _.SettleRevoked(operationId: Guid, intent: WitnessIntent) =
        settle operationId SettledRevoked intent

    member _.SettleAuthority(operationId: Guid, intent: WitnessIntent) =
        settle operationId SettledAuthority intent

    member _.RequireSettled(operationId: Guid, settlement: Phase, ct: CancellationToken) =
        WitnessSettlement.requireSettled store custody associatedData operationId settlement ct

    interface IDisposable with
        member _.Dispose() =
            try
                (store :> IDisposable).Dispose()
            finally
                custody.Dispose()
