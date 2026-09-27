namespace ClaimCore.Database

open System
open System.Threading
open Npgsql
open ClaimCore.Postgres

/// Terminal evidence needs two currently enabled human holders with distinct signer purposes
/// and live custody grants. Historical key registration alone is insufficient.
module internal DatabaseTerminalCopyAbsenceSigners =
    let private read connection transaction keyId purpose caseId cutoff (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT s.ed25519_public_key,s.public_key_sha256,s.active,"
                    + "s.holder_actor_id,a.principal_kind,a.enabled,s.signer_purpose,"
                    + "(SELECT e.witness_sequence FROM claimcore.managed_copy_signer_events e "
                    + "WHERE e.signing_key_id=s.signing_key_id AND e.revision=1),"
                    + "(SELECT e.witness_sequence FROM claimcore.managed_copy_signer_events e "
                    + "WHERE e.signing_key_id=s.signing_key_id AND e.revision=2),"
                    + "EXISTS(SELECT 1 FROM claimcore.actor_grants g "
                    + "WHERE g.actor_id=s.holder_actor_id AND g.active "
                    + "AND g.role_name IN ('AUDITOR_CUSTODIAN','DATA_STEWARD') "
                    + "AND (g.scope_kind='INSTALLATION' "
                    + "OR (g.scope_kind='CASE' AND g.scope_case_id=@case))) "
                    + "FROM claimcore.managed_copy_signers s "
                    + "JOIN claimcore.actors a ON a.actor_id=s.holder_actor_id "
                    + "WHERE s.signing_key_id=@key",
                    connection,
                    transaction
                )

            Sql.uuid command "key" keyId
            Sql.uuid command "case" caseId
            use! reader = command.ExecuteReaderAsync(ct)

            if not (reader.Read()) then
                return None
            else
                let publicKey = reader.GetFieldValue<byte array>(0)
                let holder = reader.GetGuid(3)

                let registered =
                    if reader.IsDBNull(7) then
                        Int64.MaxValue
                    else
                        reader.GetInt64(7)

                let valid =
                    reader.GetFieldValue<byte array>(1) =
                        Security.Cryptography.SHA256.HashData(publicKey)
                    && reader.GetBoolean(2)
                    && reader.GetString(4) = "HUMAN"
                    && reader.GetBoolean(5)
                    && reader.GetString(6) = purpose
                    && registered > 0L
                    && registered <= cutoff
                    && reader.IsDBNull(8)
                    && reader.GetBoolean(9)
                    && not (reader.Read())

                return if valid then Some(holder, publicKey) else None
        }

    let verify
        connection
        transaction
        caseId
        cutoff
        (evidence: SignedCopyLocationEvidence)
        (ct: CancellationToken)
        =
        task {
            if evidence.RegistryKeyId = evidence.InspectorKeyId then
                return None
            else
                let! registry =
                    read
                        connection
                        transaction
                        evidence.RegistryKeyId
                        "LOCATION_REGISTRY"
                        caseId
                        cutoff
                        ct

                let! verifier =
                    read
                        connection
                        transaction
                        evidence.InspectorKeyId
                        "DELETION_VERIFIER"
                        caseId
                        cutoff
                        ct

                match registry, verifier with
                | Some(registryHolder, registryKey), Some(verifierHolder, verifierKey) when
                    registryHolder <> verifierHolder
                    && ManagedCopySignature.verify
                        registryKey
                        evidence.RegistryCanonical
                        evidence.RegistrySignature
                    && ManagedCopySignature.verify
                        verifierKey
                        evidence.InspectionCanonical
                        evidence.InspectionSignature
                    ->
                    return Some(registryHolder, verifierHolder)
                | _ -> return None
        }
