namespace ClaimCore.Postgres

open System
open Npgsql
open ClaimCore.Application

[<NoEquality; NoComparison>]
type private AdoptionSigner =
    {
        Purpose: string
        Holder: Guid
        Active: bool
    }

module internal ManagedCopyAdoptionApprovalSigners =
    let private read connection transaction keyId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT signer_purpose,holder_actor_id,active "
                    + "FROM claimcore.managed_copy_signers WHERE signing_key_id=@key",
                    connection,
                    transaction
                )

            Sql.uuid command "key" keyId
            use! reader = command.ExecuteReaderAsync()

            if not (reader.Read()) then
                return None
            else
                let value =
                    {
                        Purpose = reader.GetString(0)
                        Holder = reader.GetGuid(1)
                        Active = reader.GetBoolean(2)
                    }

                return if reader.Read() then None else Some value
        }

    let valid connection transaction (request: CopyAdoptionApprovalRequest) =
        task {
            let! custodian = read connection transaction request.CustodianSigningKeyId
            let! registry = read connection transaction request.RegistrySigningKeyId
            let! inspector = read connection transaction request.InspectorSigningKeyId

            return
                match custodian, registry, inspector with
                | Some c, Some r, Some i ->
                    c.Active
                    && r.Active
                    && i.Active
                    && c.Purpose = "COPY_ATTESTOR"
                    && r.Purpose = "LOCATION_REGISTRY"
                    && i.Purpose = "LOCATION_INSPECTOR"
                    && c.Holder <> i.Holder
                    && r.Holder <> i.Holder
                | _ -> false
        }
