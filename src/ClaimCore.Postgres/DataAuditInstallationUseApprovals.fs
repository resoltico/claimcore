namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open DataAuditCommon

/// Global bounded replay catches orphan or altered actor approvals, even if never consumed.
module internal DataAuditInstallationUseApprovals =
    let private page
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        afterSequence
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT approval_id,witness_sequence "
                    + "FROM claimcore.installation_data_use_approvals "
                    + "WHERE witness_sequence>@after ORDER BY witness_sequence LIMIT 50",
                    connection,
                    transaction
                )

            Sql.integer command "after" afterSequence
            use! reader = command.ExecuteReaderAsync(ct)
            let items = ResizeArray<Guid * int64>()

            while reader.Read() do
                items.Add(reader.GetGuid(0), reader.GetInt64(1))

            return items |> Seq.toList
        }

    let verifyAll
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        cutoff
        (ct: CancellationToken)
        =
        task {
            let mutable after = -1L
            let mutable count = 0L
            let mutable more = true

            while more do
                let! items = page connection transaction after ct

                for approvalId, sequence in items do
                    if sequence > cutoff then
                        corrupt ()

                    // This read-only verifier uses InvalidOperationException for a failed
                    // stored-approval proof; translate it only at this audit evidence seam.
                    try
                        do!
                            InstallationUseActivationApprovals.verifyOneReadOnly
                                connection
                                transaction
                                witness
                                approvalId
                                cutoff
                                ct
                    with :? InvalidOperationException ->
                        corrupt ()

                    after <- sequence
                    count <- count + 1L

                more <- List.length items = 50

            return count
        }
