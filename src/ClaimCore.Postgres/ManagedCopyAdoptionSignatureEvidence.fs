namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application

[<NoEquality; NoComparison>]
type internal CopyAdoptionSignerEvidence =
    {
        KeyId: Guid
        HolderId: Guid
        RegisteredSequence: int64
        RetiredSequence: int64 option
    }

/// The incoming signed bytes cannot enroll their own key. Each purpose and human holder is
/// resolved from the independently witnessed roster under the owner transaction.
module internal ManagedCopyAdoptionSignatureEvidence =
    let private query =
        "SELECT s.ed25519_public_key,s.public_key_sha256,s.active,s.signer_purpose,"
        + "s.holder_actor_id,a.principal_kind,a.enabled,"
        + "(SELECT e.witness_sequence FROM claimcore.managed_copy_signer_events e "
        + "WHERE e.signing_key_id=s.signing_key_id AND e.revision=1),"
        + "(SELECT e.witness_sequence FROM claimcore.managed_copy_signer_events e "
        + "WHERE e.signing_key_id=s.signing_key_id AND e.revision=2) "
        + "FROM claimcore.managed_copy_signers s "
        + "JOIN claimcore.actors a ON a.actor_id=s.holder_actor_id "
        + "WHERE s.signing_key_id=@key"

    let verify connection transaction keyId purpose canonical signature cutoff =
        task {
            use command = new NpgsqlCommand(query, connection, transaction)
            Sql.uuid command "key" keyId
            use! reader = command.ExecuteReaderAsync()

            if not (reader.Read()) || reader.IsDBNull(7) then
                return None
            else
                let publicKey = reader.GetFieldValue<byte array>(0)
                let digest = reader.GetFieldValue<byte array>(1)
                let holder = reader.GetGuid(4)
                let registered = reader.GetInt64(7)

                let retired =
                    if reader.IsDBNull(8) then
                        None
                    else
                        Some(reader.GetInt64(8))

                let valid =
                    reader.GetBoolean(2)
                    && reader.GetString(3) = purpose
                    && reader.GetString(5) = "HUMAN"
                    && reader.GetBoolean(6)
                    && registered < cutoff
                    && retired.IsNone
                    && digest = SHA256.HashData(publicKey)
                    && ManagedCopySignature.verify publicKey canonical signature
                    && not (reader.Read())

                return
                    if valid then
                        Some
                            {
                                KeyId = keyId
                                HolderId = holder
                                RegisteredSequence = registered
                                RetiredSequence = retired
                            }
                    else
                        None
        }

    let distinct
        owner
        (custodian: CopyAdoptionSignerEvidence)
        (registry: CopyAdoptionSignerEvidence)
        (inspector: CopyAdoptionSignerEvidence)
        =
        custodian.KeyId <> registry.KeyId
        && custodian.KeyId <> inspector.KeyId
        && registry.KeyId <> inspector.KeyId
        && custodian.HolderId <> inspector.HolderId
        && registry.HolderId <> inspector.HolderId
        && owner <> inspector.HolderId

    let owners
        connection
        transaction
        (request: CopyAdoptionApprovalRequest)
        (submission: CopyAdoptionSubmission)
        ownerActorId
        cutoff
        =
        task {
            let! custodian =
                verify
                    connection
                    transaction
                    request.CustodianSigningKeyId
                    "COPY_ATTESTOR"
                    submission.Custodian.Canonical
                    submission.Custodian.Signature
                    cutoff

            let! registry =
                verify
                    connection
                    transaction
                    request.RegistrySigningKeyId
                    "LOCATION_REGISTRY"
                    submission.Registry.Canonical
                    submission.Registry.Signature
                    cutoff

            let! inspector =
                verify
                    connection
                    transaction
                    request.InspectorSigningKeyId
                    "LOCATION_INSPECTOR"
                    submission.Inspection.Canonical
                    submission.Inspection.Signature
                    cutoff

            return
                match custodian, registry, inspector with
                | Some c, Some r, Some i when distinct ownerActorId c r i -> Some(c, r, i)
                | _ -> None
        }
