namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open DataAuditCommon

[<NoEquality; NoComparison>]
type internal CopyAdoptionSignerAudit =
    {
        Holder: Guid
        PublicKey: byte array
        RegisteredSequence: int64
        RetiredSequence: int64 option
    }

/// Signer registration/retirement is fully replayed elsewhere; this checks the approval's
/// historical purpose, holder separation and witness-time validity without using current active.
module internal DataAuditCopyAdoptionSignerRole =
    let private query =
        "SELECT s.signer_purpose,s.holder_actor_id,s.ed25519_public_key,s.public_key_sha256,"
        + "(SELECT e.witness_sequence FROM claimcore.managed_copy_signer_events e "
        + "WHERE e.signing_key_id=s.signing_key_id AND e.revision=1),"
        + "(SELECT e.witness_sequence FROM claimcore.managed_copy_signer_events e "
        + "WHERE e.signing_key_id=s.signing_key_id AND e.revision=2) "
        + "FROM claimcore.managed_copy_signers s WHERE s.signing_key_id=@key"

    let read connection transaction keyId purpose sequence (ct: CancellationToken) =
        task {
            use command = new NpgsqlCommand(query, connection, transaction)
            Sql.uuid command "key" keyId
            use! reader = command.ExecuteReaderAsync(ct)

            if not (reader.Read()) || reader.GetString(0) <> purpose || reader.IsDBNull(4) then
                corrupt ()

            let publicKey = reader.GetFieldValue<byte array>(2)

            let value =
                {
                    Holder = reader.GetGuid(1)
                    PublicKey = publicKey
                    RegisteredSequence = reader.GetInt64(4)
                    RetiredSequence =
                        if reader.IsDBNull(5) then
                            None
                        else
                            Some(reader.GetInt64(5))
                }

            if
                reader.GetFieldValue<byte array>(3) <> SHA256.HashData(publicKey)
                || not (ManagedCopySignature.validPublicKey publicKey)
                || value.RegisteredSequence >= sequence
                || value.RetiredSequence |> Option.exists (fun retired -> retired <= sequence)
                || reader.Read()
            then
                corrupt ()

            return value
        }

    let verify connection transaction copyKey registryKey inspectorKey actor sequence ct =
        task {
            let! custodian = read connection transaction copyKey "COPY_ATTESTOR" sequence ct

            let! registry =
                read connection transaction registryKey "LOCATION_REGISTRY" sequence ct

            let! inspector =
                read connection transaction inspectorKey "LOCATION_INSPECTOR" sequence ct

            if
                custodian.Holder = inspector.Holder
                || registry.Holder = inspector.Holder
                || actor = inspector.Holder
            then
                corrupt ()
        }
