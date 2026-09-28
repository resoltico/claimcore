module internal ClaimCore.IntegrationTests.InstallationLossRetirementDenialAudit

open System.IO
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.InstallationLossRetirementFixture

/// Rollback-independent synthetic mutation proves the terminal denial is audited.
let verifyTamper (context: Context) retirementId =
    use read =
        new NpgsqlCommand(
            "SELECT operation_commitment FROM claimcore.installation_loss_operation_denials "
            + "WHERE retirement_id=@retirement",
            context.Primary
        )

    read.Parameters.AddWithValue("retirement", retirementId) |> ignore

    let commitment =
        match read.ExecuteScalar() with
        | :? (byte array) as value -> value
        | _ -> failtest "Synthetic terminal denial is absent."

    use delete =
        new NpgsqlCommand(
            "DELETE FROM claimcore.installation_loss_operation_denials "
            + "WHERE operation_commitment=@commitment",
            context.Primary
        )

    delete.Parameters.AddWithValue("commitment", commitment) |> ignore
    Expect.equal (delete.ExecuteNonQuery()) 1 "Synthetic owner removed one denial."

    try
        use audit = new NpgsqlConnection(context.OwnerConnectionString)
        audit.Open()

        Expect.throwsT<InvalidDataException>
            (fun () ->
                DataAudit.run audit context.Witness CancellationToken.None |> await |> ignore)
            "Full audit detects a missing terminal operation denial."
    finally
        use restore =
            new NpgsqlCommand(
                "INSERT INTO claimcore.installation_loss_operation_denials "
                + "(operation_commitment,retirement_id) VALUES (@commitment,@retirement)",
                context.Primary
            )

        restore.Parameters.AddWithValue("commitment", commitment) |> ignore
        restore.Parameters.AddWithValue("retirement", retirementId) |> ignore
        Expect.equal (restore.ExecuteNonQuery()) 1 "Synthetic denial was restored."
