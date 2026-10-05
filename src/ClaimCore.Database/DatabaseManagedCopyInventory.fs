namespace ClaimCore.Database

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Postgres.WitnessProtocolReconciliation
open ClaimCore.Application

/// Owner-only, known-location seal. It cannot discover undisclosed human-held copies.
[<Sealed>]
type internal DatabaseManagedCopyInventory
    private
    (registryBytes: byte array, inspectionBytes: byte array, commitmentKey: ManagedCopyCommitmentKey)
    =
    let mutable disposed = false

    let databaseNow (connection: NpgsqlConnection) transaction =
        task {
            use command = new NpgsqlCommand("SELECT clock_timestamp()", connection, transaction)
            let! value = command.ExecuteScalarAsync()

            return
                match value with
                | :? DateTimeOffset as instant -> instant
                | :? DateTime as instant when instant.Kind = DateTimeKind.Utc ->
                    DateTimeOffset instant
                | _ -> invalidOp "Copy inventory database clock is unavailable."
        }

    let signer connection transaction keyId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT s.ed25519_public_key,s.active,r.custodian_actor_id,r.witness_sequence,s.signer_purpose,s.holder_actor_id "
                    + "FROM claimcore.managed_copy_signers s JOIN claimcore.managed_copy_signer_events r "
                    + "ON r.signing_key_id=s.signing_key_id AND r.revision=1 "
                    + "WHERE s.signing_key_id=@key",
                    connection,
                    transaction
                )

            command.Parameters.AddWithValue("key", keyId) |> ignore
            use! reader = command.ExecuteReaderAsync()

            return
                if
                    reader.Read() && reader.GetBoolean(1) && reader.GetGuid(2) = reader.GetGuid(5)
                then
                    Some(
                        reader.GetFieldValue<byte array>(0),
                        reader.GetGuid(2),
                        reader.GetInt64(3),
                        ManagedCopySignerCandidate.purposeOfName (reader.GetString(4))
                    )
                else
                    None
        }

    let identityMatches
        (witness: WitnessProtocol)
        (evidence: SignedCopyLocationEvidence)
        cutoffSequence
        cutoffHash
        =
        evidence.InstallationId = witness.Identity.InstallationId
        && evidence.LineageId = witness.Identity.LineageId
        && evidence.Epoch = witness.Identity.Epoch
        && evidence.CutoffSequence = cutoffSequence
        && evidence.CutoffHash = cutoffHash
        && evidence.RegistryKeyId <> evidence.InspectorKeyId

    let verifySigners
        connection
        transaction
        (evidence: SignedCopyLocationEvidence)
        cutoffSequence
        inspectorPurpose
        =
        task {
            let! registrySigner = signer connection transaction evidence.RegistryKeyId
            let! inspectorSigner = signer connection transaction evidence.InspectorKeyId

            return
                match registrySigner, inspectorSigner with
                | Some(registryKey,
                       registryCustodian,
                       registrySequence,
                       CopySignerPurpose.LocationRegistry),
                  Some(inspectorKey, inspectorCustodian, inspectorSequence, actualPurpose) when
                    actualPurpose = inspectorPurpose
                    && registryCustodian <> inspectorCustodian
                    && registrySequence <= cutoffSequence
                    && inspectorSequence <= cutoffSequence
                    && ManagedCopySignature.verify
                        registryKey
                        evidence.RegistryCanonical
                        evidence.RegistrySignature
                    && ManagedCopySignature.verify
                        inspectorKey
                        evidence.InspectionCanonical
                        evidence.InspectionSignature
                    ->
                    Some(registryCustodian, inspectorCustodian)
                | _ -> None
        }

    let signedEvidence (witness: WitnessProtocol) cutoffSequence cutoffHash now ct =
        task {
            if disposed then
                return None
            else
                try
                    let evidence =
                        DatabaseManagedCopyInventoryEvidence.parse registryBytes inspectionBytes now

                    if identityMatches witness evidence cutoffSequence cutoffHash then
                        do! witness.VerifyHistoricalTip(cutoffSequence, cutoffHash, ct)
                        return Some evidence
                    else
                        return None
                with _ ->
                    return None
        }

    let privateSeal caseId cutoffSequence cutoffHash (evidence: SignedCopyLocationEvidence) count =
        {
            CaseId = caseId
            WitnessCutoffSequence = cutoffSequence
            WitnessCutoffHash = cutoffHash
            InventorySha256 = evidence.RegistrySha256
            CopyCount = int64 (count + evidence.KnownUnmanaged.Length)
            ObservedAt = evidence.ObservedAt
        }

    let inspect
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (caseId: Guid)
        (cutoffSequence: int64)
        (cutoffHash: byte array)
        (ct: CancellationToken)
        =
        task {
            try
                let! now = databaseNow connection transaction

                let! signed = signedEvidence witness cutoffSequence cutoffHash now ct

                match signed with
                | None -> return None
                | Some evidence ->
                    let! signers =
                        verifySigners
                            connection
                            transaction
                            evidence
                            cutoffSequence
                            CopySignerPurpose.LocationInspector

                    match signers with
                    | None -> return None
                    | Some _ ->
                        let! count =
                            DatabaseManagedCopyInventoryComparison.verify
                                connection
                                transaction
                                witness
                                cutoffSequence
                                commitmentKey
                                evidence
                                None
                                false
                                ct

                        return
                            count
                            |> Option.map (privateSeal caseId cutoffSequence cutoffHash evidence)
            with _ ->
                return None
        }

    let privateAbsence
        copyId
        cutoffSequence
        cutoffHash
        registryHolder
        verifierHolder
        (evidence: SignedCopyLocationEvidence)
        =
        {
            CopyId = copyId
            RegistrySha256 = evidence.RegistrySha256
            InspectionReportSha256 = SHA256.HashData(evidence.InspectionCanonical)
            RegistryHolderActorId = registryHolder
            VerifierSigningKeyId = evidence.InspectorKeyId
            VerifierHolderActorId = verifierHolder
            WitnessCutoffSequence = cutoffSequence
            WitnessCutoffHash = cutoffHash
            ObservedAt = evidence.ObservedAt
            RegistryExpiresAt = evidence.RegistryExpiresAt
            InspectionExpiresAt = evidence.InspectionExpiresAt
        }

    let verifyAbsence
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        copyId
        cutoffSequence
        cutoffHash
        checkedAt
        (ct: CancellationToken)
        =
        task {
            try
                let! signed = signedEvidence witness cutoffSequence cutoffHash checkedAt ct

                match signed with
                | None -> return None
                | Some evidence ->
                    let! signers =
                        verifySigners
                            connection
                            transaction
                            evidence
                            cutoffSequence
                            CopySignerPurpose.DeletionVerifier

                    match signers with
                    | None -> return None
                    | Some(registryHolder, verifierHolder) ->
                        let! count =
                            DatabaseManagedCopyInventoryComparison.verify
                                connection
                                transaction
                                witness
                                cutoffSequence
                                commitmentKey
                                evidence
                                (Some copyId)
                                false
                                ct

                        return
                            count
                            |> Option.map (fun _ ->
                                privateAbsence
                                    copyId
                                    cutoffSequence
                                    cutoffHash
                                    registryHolder
                                    verifierHolder
                                    evidence)
            with _ ->
                return None
        }

    interface IManagedCopyErasureClearance with
        member _.RequireCompleteInventory
            (connection, transaction, witness, caseId, cutoffSequence, cutoffHash, ct)
            =
            inspect connection transaction witness caseId cutoffSequence cutoffHash ct

    interface ICopyAbsenceVerifier with
        member _.Verify
            (connection, transaction, witness, copyId, cutoffSequence, cutoffHash, checkedAt, ct)
            =
            verifyAbsence
                connection
                transaction
                witness
                copyId
                cutoffSequence
                cutoffHash
                checkedAt
                ct

    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed <- true
                CryptographicOperations.ZeroMemory(registryBytes)
                CryptographicOperations.ZeroMemory(inspectionBytes)
                (commitmentKey :> IDisposable).Dispose()

    static member TryLoadFromPrivateConfiguration() =
        DatabaseManagedCopyInventoryInputs.load ()
        |> Option.map (fun (registry, inspection, key) ->
            new DatabaseManagedCopyInventory(registry, inspection, key))
