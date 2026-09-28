module internal ClaimCore.IntegrationTests.ManagedCopyAdoptedAbsenceCheck

open System
open System.Security.Cryptography
open System.Threading
open Expecto
open Npgsql
open System.IO
open ClaimCore.Postgres
open ClaimCore.Database
open ClaimCore.HostSecurity
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture

let private signedEvidence registryPath reportPath =
    DatabaseManagedCopyInventoryEvidence.parse
        (File.ReadAllBytes(registryPath))
        (File.ReadAllBytes(reportPath))
        DateTimeOffset.UtcNow

let private compare
    connection
    transaction
    witness
    registryPath
    reportPath
    keyPath
    (transition: AdoptedCopyTransition)
    =
    let evidence = signedEvidence registryPath reportPath

    let origin =
        ManagedCopyAdoptionEvidence.verifyOrigin
            connection
            transaction
            witness
            transition.ActionWitnessCutoffSequence
            transition.CopyId
            CancellationToken.None
        |> await

    Expect.isSome origin "Signed adopted origin survives deletion request"
    use commitment = ManagedCopyCommitmentKey.Load(keyPath)

    let count =
        DatabaseManagedCopyInventoryComparison.verify
            connection
            transaction
            witness
            transition.ActionWitnessCutoffSequence
            commitment
            evidence
            (Some transition.CopyId)
            false
            CancellationToken.None
        |> await

    Expect.isSome count "Signed adopted registry and ABSENT observation cover every managed copy"

    let wrongKeyPath =
        privateBytes (privateRoot ()) "wrong-adopted-hmac.key" (RandomNumberGenerator.GetBytes(32))

    use wrongKey = ManagedCopyCommitmentKey.Load(wrongKeyPath)

    let rejected =
        DatabaseManagedCopyInventoryComparison.verify
            connection
            transaction
            witness
            transition.ActionWitnessCutoffSequence
            wrongKey
            evidence
            (Some transition.CopyId)
            false
            CancellationToken.None
        |> await

    Expect.isNone rejected "Signed location with wrong HMAC custody is refused"

let private verifyPort connection transaction witness (transition: AdoptedCopyTransition) =
    use inventory =
        DatabaseManagedCopyInventory.TryLoadFromPrivateConfiguration()
        |> Option.defaultWith (fun () -> failtest "Signed private inventory did not load")

    let proof =
        (inventory :> ICopyAbsenceVerifier)
            .Verify(
                connection,
                transaction,
                witness,
                transition.CopyId,
                transition.ActionWitnessCutoffSequence,
                transition.ActionWitnessCutoffHash,
                DateTimeOffset.UtcNow,
                CancellationToken.None
            )
        |> await

    Expect.isSome proof "Signed all-location adopted absence passes production verifier"

let private rejectUnverifiedOrigin
    connection
    (transaction: NpgsqlTransaction)
    witness
    (transition: AdoptedCopyTransition)
    =
    transaction.Save("synthetic_unverified_adoption")

    try
        use tamper =
            new NpgsqlCommand(
                "UPDATE claimcore.managed_copy_adoptions "
                + "SET custodian_signature=set_byte(custodian_signature,0,"
                + "get_byte(custodian_signature,0) # 1) WHERE copy_id=@copy",
                connection,
                transaction
            )

        Sql.uuid tamper "copy" transition.CopyId
        Expect.equal (tamper.ExecuteNonQuery()) 1 "One synthetic adoption signature changed"

        use inventory =
            DatabaseManagedCopyInventory.TryLoadFromPrivateConfiguration()
            |> Option.defaultWith (fun () -> failtest "Signed private inventory did not load")

        let rejected =
            (inventory :> ICopyAbsenceVerifier)
                .Verify(
                    connection,
                    transaction,
                    witness,
                    transition.CopyId,
                    transition.ActionWitnessCutoffSequence,
                    transition.ActionWitnessCutoffHash,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None
                )
            |> await

        Expect.isNone rejected "Signed located registry without verified adoption is refused"
    finally
        transaction.Rollback("synthetic_unverified_adoption")

let require owner witness registryPath reportPath keyPath canonical =
    let transition =
        ManagedCopyAdoptedTransitionAttestation.parse canonical
        |> Option.defaultWith (fun () -> failtest "Synthetic adopted deletion canonical is invalid")

    withEnvironment registryPath reportPath keyPath (fun () ->
        use connection = new NpgsqlConnection(owner)
        connection.Open()
        use transaction = connection.BeginTransaction()
        compare connection transaction witness registryPath reportPath keyPath transition
        verifyPort connection transaction witness transition
        rejectUnverifiedOrigin connection transaction witness transition)
