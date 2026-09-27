module ClaimCore.IntegrationTests.ManagedCopyVerifiedDeletionTests

open System
open System.IO
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture
open ClaimCore.IntegrationTests.ManagedCopyVerifiedDeletionFixture
open ClaimCore.IntegrationTests.ManagedCopyVerifiedDeletionCompletion

let private qualifyCopy
    owner
    writer
    connection
    witness
    runtime
    directory
    copyKey
    registryKey
    verifierKey
    algorithm
    copyKeyId
    registryKeyId
    verifierKeyId
    verifierPrincipal
    =
    let pending =
        pendingCopy owner connection witness copyKey algorithm copyKeyId directory

    finishPending
        owner
        writer
        witness
        runtime
        directory
        registryKey
        verifierKey
        copyKey
        algorithm
        registryKeyId
        verifierKeyId
        verifierPrincipal
        pending

let private auditAndTamper owner app witness copyId =
    use source = RuntimeDataSource.create app
    use audit = RuntimeDatabase.openConnection source
    let verified = DataAudit.run audit witness CancellationToken.None |> await

    Expect.equal
        verified.CopyDeletionApprovals
        1L
        "One exact witnessed deletion approval was audited."

    Expect.equal verified.OwnerManagedCopies 1L "Deleted copy retains full signed history."
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use tamper =
        new NpgsqlCommand(
            "UPDATE claimcore.managed_copies SET last_verified_at=last_verified_at + interval '1 second' "
            + "WHERE copy_id=@copy",
            connection
        )

    Sql.uuid tamper "copy" copyId
    Expect.equal (tamper.ExecuteNonQuery()) 1 "One synthetic verification time was altered."

    Expect.throwsT<InvalidDataException>
        (fun () -> DataAudit.run audit witness CancellationToken.None |> await |> ignore)
        "Full audit refuses a changed verified-deletion projection."

let private verifiedDeletion owner app writer witness =
    let ownerPrincipal = human "delete-owner"
    provision owner witness ownerPrincipal |> applied
    use runtime = openRuntime app writer
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    let (copyKey,
         registryKey,
         verifierKey,
         algorithm,
         copyKeyId,
         registryKeyId,
         verifierKeyId,
         verifierPrincipal) =
        keys runtime ownerPrincipal witness connection

    use copyKey = copyKey
    use registryKey = registryKey
    use verifierKey = verifierKey
    let directory = privateRoot ()

    try
        let copyId =
            qualifyCopy
                owner
                writer
                connection
                witness
                runtime
                directory
                copyKey
                registryKey
                verifierKey
                algorithm
                copyKeyId
                registryKeyId
                verifierKeyId
                verifierPrincipal

        auditAndTamper owner app witness copyId
    finally
        Directory.Delete(directory, true)

let tests =
    testList
        "managed-copy verified deletion"
        [
            testCase
                "[CC-BACKUP-001] independent absence and witnessed one-use approval co-commit verified deletion"
                (fun _ -> withAuthorityRuntimeDatabase verifiedDeletion)
        ]
