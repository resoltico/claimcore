namespace ClaimCore.Database

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Postgres.WitnessProtocolReconciliation
open ClaimCore.Witness

/// Owner-private terminal issuer. A signed all-ABSENT registry is checked against the exact
/// current copy set and each witnessed, approved deletion under the owner transaction lock.
[<Sealed>]
type internal DatabaseTerminalCopyAbsenceIssuer
    private (registryBytes: byte array, inspectionBytes: byte array, key: ManagedCopyCommitmentKey)
    =
    let mutable disposed = false

    let signedEvidence (witness: WitnessProtocol) (tip: Snapshot) observedAt =
        let evidence =
            DatabaseManagedCopyInventoryEvidence.parse registryBytes inspectionBytes observedAt

        if
            evidence.InstallationId <> witness.Identity.InstallationId
            || evidence.LineageId <> witness.Identity.LineageId
            || evidence.Epoch <> witness.Identity.Epoch
            || evidence.CutoffSequence > tip.TipSequence
            || evidence.CutoffSequence < 1L
            || not evidence.KnownUnmanaged.IsEmpty
            || (evidence.Observations |> List.exists (fun item -> item.Status <> "ABSENT"))
        then
            None
        else
            witness.VerifyHistoricalTip(evidence.CutoffSequence, evidence.CutoffHash)
            Some evidence

    let exactRows
        connection
        transaction
        (witness: WitnessProtocol)
        (tip: Snapshot)
        observedAt
        (evidence: SignedCopyLocationEvidence)
        ct
        =
        task {
            let! listed =
                DatabaseManagedCopyInventoryComparison.verify
                    connection
                    transaction
                    witness
                    tip.TipSequence
                    key
                    evidence
                    None
                    true
                    ct

            match listed with
            | None -> return None
            | Some count ->
                let! replayed, deletionDigest =
                    DatabaseTerminalCopyAbsenceRows.verify
                        connection
                        transaction
                        witness
                        evidence.CutoffSequence
                        tip.TipSequence
                        observedAt
                        ct

                return
                    if replayed = int64 count then
                        Some(replayed, deletionDigest)
                    else
                        None
        }

    let stableTip
        connection
        transaction
        (witness: WitnessProtocol)
        (tip: Snapshot)
        writerGeneration
        ct
        =
        task {
            let! _, pending = DataAuditJournal.scan connection transaction witness tip ct
            let latest = witness.Snapshot()

            return
                pending = 0L
                && latest.TipSequence = tip.TipSequence
                && latest.TipHash = tip.TipHash
                && latest.WriterGeneration = writerGeneration
        }

    let certifyEvidence
        connection
        transaction
        (witness: WitnessProtocol)
        (request: TerminalCopyAbsenceRequest)
        (tip: Snapshot)
        (evidence: SignedCopyLocationEvidence)
        ct
        =
        task {
            let! holders =
                DatabaseTerminalCopyAbsenceSigners.verify
                    connection
                    transaction
                    request.CaseId
                    evidence.CutoffSequence
                    evidence
                    ct

            match holders with
            | None -> return None
            | Some(registryHolder, verifierHolder) ->
                let! exact =
                    exactRows connection transaction witness tip request.ObservedAt evidence ct

                match exact with
                | None -> return None
                | Some(count, deletionDigest) ->
                    let! stable =
                        stableTip connection transaction witness tip request.WriterGeneration ct

                    if not stable then
                        return None
                    else
                        return
                            Some(
                                DatabaseTerminalCopyAbsenceCertificate.certify
                                    witness
                                    request
                                    evidence
                                    registryHolder
                                    verifierHolder
                                    count
                                    deletionDigest
                                    tip
                            )
        }

    let admit
        connection
        transaction
        (witness: WitnessProtocol)
        caseId
        pruneEventId
        cutoffSequence
        cutoffHash
        writerGeneration
        ct
        =
        task {
            OwnerConnection.requireIdentity connection
            SchemaBaseline.requireCurrent connection
            witness.AdmitReadOnly()
            do! DatabaseTerminalCopyAbsenceAdmission.lockCopies connection transaction ct

            return!
                DatabaseTerminalCopyAbsenceAdmission.verifyCase
                    connection
                    transaction
                    witness
                    caseId
                    pruneEventId
                    cutoffSequence
                    cutoffHash
                    writerGeneration
                    ct
        }

    let issue
        connection
        transaction
        (witness: WitnessProtocol)
        caseId
        pruneEventId
        cutoffSequence
        (cutoffHash: byte array)
        policyId
        suppressionUntil
        writerGeneration
        observedAt
        ct
        =
        task {
            if disposed || cutoffHash.Length <> 32 then
                return None
            else
                let! allowed =
                    admit
                        connection
                        transaction
                        witness
                        caseId
                        pruneEventId
                        cutoffSequence
                        cutoffHash
                        writerGeneration
                        ct

                if not allowed then
                    return None
                else
                    witness.VerifyHistoricalTip(cutoffSequence, cutoffHash)
                    let tip = witness.Snapshot()

                    match signedEvidence witness tip observedAt with
                    | None -> return None
                    | Some evidence ->
                        let request =
                            DatabaseTerminalCopyAbsenceCertificate.bind
                                caseId
                                pruneEventId
                                cutoffSequence
                                cutoffHash
                                policyId
                                suppressionUntil
                                writerGeneration
                                observedAt

                        return!
                            certifyEvidence connection transaction witness request tip evidence ct
        }

    interface ICopyErasureCertification with
        member _.RequireAllAbsent
            (
                primary,
                transaction,
                witness,
                caseId,
                pruneEventId,
                cutoffSequence,
                cutoffHash,
                policyId,
                suppressionUntil,
                writerGeneration,
                observedAt,
                ct
            ) =
            task {
                try
                    return!
                        issue
                            primary
                            transaction
                            witness
                            caseId
                            pruneEventId
                            cutoffSequence
                            cutoffHash
                            policyId
                            suppressionUntil
                            writerGeneration
                            observedAt
                            ct
                with _ ->
                    return None
            }

    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed <- true
                CryptographicOperations.ZeroMemory(registryBytes)
                CryptographicOperations.ZeroMemory(inspectionBytes)
                (key :> IDisposable).Dispose()

    static member TryLoadFromPrivateConfiguration() =
        DatabaseManagedCopyInventoryInputs.load ()
        |> Option.map (fun (registry, inspection, key) ->
            new DatabaseTerminalCopyAbsenceIssuer(registry, inspection, key))
