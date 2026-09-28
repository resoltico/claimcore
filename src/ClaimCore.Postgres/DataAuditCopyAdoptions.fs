namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Witness
open DataAuditCommon

/// Every signed ADOPT/known external REGISTER has one immutable owner receipt and
/// one-use approval; later transitions replay through the final projection.
module internal DataAuditCopyAdoptions =
    let private orphanSql =
        "SELECT EXISTS(SELECT 1 FROM claimcore.managed_copy_events e "
        + "LEFT JOIN claimcore.managed_copy_adoptions a "
        + "ON a.adoption_event_id=e.event_id AND a.copy_id=e.copy_id "
        + "WHERE (e.event_kind='ADOPT' OR "
        + "(e.producer_kind='ADOPTED_EXTERNAL' AND e.revision=1)) "
        + "AND a.adoption_event_id IS NULL) "
        + "OR EXISTS(SELECT 1 FROM claimcore.managed_copy_adoptions a "
        + "LEFT JOIN claimcore.managed_copy_adoption_approval_uses u "
        + "ON u.adoption_event_id=a.adoption_event_id AND u.approval_id=a.owner_approval_id "
        + "WHERE u.approval_id IS NULL) "
        + "OR EXISTS(SELECT 1 FROM claimcore.managed_copies c "
        + "LEFT JOIN claimcore.managed_copy_adoptions a ON a.copy_id=c.copy_id "
        + "WHERE (c.producer_kind='ADOPTED_EXTERNAL' "
        + "OR (c.producer_kind='PRODUCT_EXPORT' AND c.revision>1)) "
        + "AND a.copy_id IS NULL) "
        + "OR EXISTS(SELECT 1 FROM claimcore.managed_copy_events e "
        + "JOIN claimcore.managed_copies c ON c.copy_id=e.copy_id "
        + "WHERE e.producer_kind<>c.producer_kind)"

    let private requireNoOrphan connection transaction =
        task {
            use command = new NpgsqlCommand(orphanSql, connection, transaction)
            let! value = command.ExecuteScalarAsync()

            if unbox<bool> value then
                corrupt ()
        }

    let private page connection transaction after (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT copy_id FROM claimcore.managed_copy_adoptions "
                    + "WHERE copy_id>@after ORDER BY copy_id LIMIT 50",
                    connection,
                    transaction
                )

            Sql.uuid command "after" after
            use! reader = command.ExecuteReaderAsync(ct)
            let values = ResizeArray<Guid>()

            while reader.Read() do
                values.Add(reader.GetGuid(0))

            return values.ToArray()
        }

    let verifyAll connection transaction (witness: WitnessProtocol) cutoff (ct: CancellationToken) =
        task {
            do! requireNoOrphan connection transaction
            let mutable after = Guid.Empty
            let mutable more = true
            let mutable count = 0L

            while more do
                let! ids = page connection transaction after ct

                for copyId in ids do
                    if copyId <= after then
                        corrupt ()

                    let! found =
                        ManagedCopyAdoptionEvidence.verifyOrigin
                            connection
                            transaction
                            witness
                            cutoff
                            copyId
                            ct

                    let proof = found |> Option.defaultWith corrupt

                    do!
                        DataAuditAdoptedCopyTransitions.verify
                            connection
                            transaction
                            witness
                            cutoff
                            proof
                            ct

                    after <- copyId
                    count <- count + 1L

                more <- ids.Length = 50

            return count
        }
