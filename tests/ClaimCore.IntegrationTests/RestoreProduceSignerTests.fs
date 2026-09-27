module ClaimCore.IntegrationTests.RestoreProduceSignerTests

open System
open System.Security.Cryptography
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Database
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.ManagedCopyIngestTests

let private currentSigner owner keyId =
    use connection = new NpgsqlConnection(owner)
    connection.Open()
    use transaction = connection.BeginTransaction()

    ActorGrantRead.lockRevision connection transaction true CancellationToken.None
    |> await
    |> ignore

    let key, holder =
        DatabaseRestoreSignedEvidence.signer
            connection
            transaction
            keyId
            CopySignerPurpose.RestoreReport

    try
        Expect.notEqual holder Guid.Empty "Report signer has a current human holder"
    finally
        CryptographicOperations.ZeroMemory(key)

    transaction.Commit()

let private holderChanges owner app writer (witness: WitnessProtocol) =
    let principal = human "restore-signer-owner"
    let holder = human "restore-signer-custodian"
    provision owner witness principal |> applied
    use runtime = openRuntime app writer
    grantCustodian runtime principal holder
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    let key, _, keyId, _ =
        registeredSigner runtime principal holder CopySignerPurpose.RestoreReport witness connection

    use key = key
    currentSigner owner keyId
    let management = (runtime.ForActor principal).Management
    let revokeId = Guid.NewGuid()

    management.SetGrant(
        revokeId,
        holder,
        Role.AuditorCustodian,
        GrantTarget.Installation,
        false,
        CancellationToken.None
    )
    |> await
    |> appliedManagement revokeId

    Expect.throws
        (fun () -> currentSigner owner keyId)
        "Revoked human custodian cannot back a fresh signed report"

    let regrantId = Guid.NewGuid()

    management.SetGrant(
        regrantId,
        holder,
        Role.AuditorCustodian,
        GrantTarget.Installation,
        true,
        CancellationToken.None
    )
    |> await
    |> appliedManagement regrantId

    let disableId = Guid.NewGuid()

    management.SetEnabled(disableId, holder, false, CancellationToken.None)
    |> await
    |> appliedManagement disableId

    Expect.throws
        (fun () -> currentSigner owner keyId)
        "Disabled human custodian cannot back a fresh signed report"

let tests =
    testList
        "restored report signer authority"
        [
            testCase
                "[CC-BACKUP-001] revoked or disabled report-key holder refuses fresh qualification"
                (fun _ -> withAuthorityRuntimeDatabase holderChanges)
        ]
