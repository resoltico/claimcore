module internal ClaimCore.IntegrationTests.ManagedCopyAuditTestSupport

open System.IO
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures

let private rejectFakeProof (connection: NpgsqlConnection) copyId =
    use fakeProof =
        new NpgsqlCommand(
            "UPDATE claimcore.managed_copies SET verification_proof_sha256=decode(repeat('a',64),'hex') "
            + "WHERE copy_id=@copy",
            connection
        )

    fakeProof.Parameters.AddWithValue("copy", copyId) |> ignore

    Expect.throwsT<PostgresException>
        (fun () -> fakeProof.ExecuteNonQuery() |> ignore)
        "A REGISTER projection cannot be made verified by writing a digest."

let assertOwnerCopyAudit owner app writer witness copyId =
    use source = RuntimeDataSource.create app
    use audit = RuntimeDatabase.openConnection source
    let verified = DataAudit.run audit witness CancellationToken.None |> await
    Expect.equal verified.OwnerManagedCopies 1L "One signed owner copy is fully audited."

    use reopened =
        Runtime.OpenPostgres(
            app,
            writer,
            witnessKey (),
            suppressionKeyFile (),
            artifactKeyRingFile (),
            CancellationToken.None
        )
        |> await
        |> accepted

    use connection = new NpgsqlConnection(owner)
    connection.Open()
    rejectFakeProof connection copyId

    use tamper =
        new NpgsqlCommand(
            "UPDATE claimcore.managed_copies SET wal_end_lsn='0/3' WHERE copy_id=@copy",
            connection
        )

    tamper.Parameters.AddWithValue("copy", copyId) |> ignore
    Expect.equal (tamper.ExecuteNonQuery()) 1 "One synthetic copy projection changed."

    Expect.throwsT<InvalidDataException>
        (fun () -> DataAudit.run audit witness CancellationToken.None |> await |> ignore)
        "A changed WAL range under one signed attestation must fail full audit."

    match
        Runtime.OpenPostgres(
            app,
            writer,
            witnessKey (),
            suppressionKeyFile (),
            artifactKeyRingFile (),
            CancellationToken.None
        )
        |> await
    with
    | Error RuntimeOpenFault.RuntimeStoreIntegrityError -> ()
    | _ -> failtest "Startup must quarantine altered managed-copy evidence."
