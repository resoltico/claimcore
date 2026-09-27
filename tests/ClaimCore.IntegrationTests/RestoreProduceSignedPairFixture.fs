module internal ClaimCore.IntegrationTests.RestoreProduceSignedPairFixture

open System
open System.Security.Cryptography
open Expecto
open NSec.Cryptography
open ClaimCore.Database
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopyPhysicalProcessTests
open ClaimCore.IntegrationTests.ManagedCopyPhysicalOwnerSetup
open ClaimCore.IntegrationTests.RestorePhysicalArchiveEvidence
open ClaimCore.IntegrationTests.RestorePhysicalConnections
open ClaimCore.IntegrationTests.RestorePhysicalReplayFixture
open ClaimCore.IntegrationTests.RestoreProduceSignerFixture
open ClaimCore.IntegrationTests.RestoreProduceSourceDocuments
open ClaimCore.IntegrationTests.RestoreProduceSignedPairChecks

let private completeCopyKinds (verified: VerifiedPhysicalCopy list) =
    Expect.isTrue
        (verified.Length >= 4)
        "Both physical BASE copies and finite WAL prefixes were retained"

    Expect.equal
        (verified
         |> List.map (fun item -> item.Cluster, item.Kind)
         |> List.distinct
         |> List.length)
        4
        "Verified copies include each cluster and copy kind"

let private advanced
    action
    (witness: WitnessProtocol)
    capture
    registered
    verified
    reportKeyId
    checkpointKeyId
    (reportKey: Key)
    (checkpointKey: Key)
    facts
    (access: RestoredPairAccess)
    containers
    =
    auditBorrowed access witness
    let keyId = witness.KeyCustody.ActiveKeyId
    use custody = new KeyRing(keyId, [ keyId, witnessKey () ]) :> IKeyCustody
    use suppression = SuppressionKeyFile.Load(suppressionKeyFile ())

    let input =
        build capture registered facts verified reportKeyId checkpointKeyId reportKey checkpointKey

    verifyCoverage input facts

    let produced =
        DatabaseRestoreProduceLive.produceSynthetic
            access.Owner
            access.WitnessAudit
            access.WitnessOwner
            custody
            suppression
            input

    verifyInventory input produced facts checkpointKey
    reportScope produced
    verifyRecheck access custody suppression input registered produced
    action capture registered verified facts access containers input produced checkpointKey

let withSignedPair action owner app writer (witness: WitnessProtocol) =
    withVerifiedRegisteredCopies
        (fun capture registered verified ->
            completeCopyKinds verified

            let reportKey, reportKeyId, checkpointKey, checkpointKeyId =
                reportAuthorities owner app writer witness

            use reportKey = reportKey
            use checkpointKey = checkpointKey
            Expect.notEqual reportKeyId checkpointKeyId "Signers have distinct registered keys"

            let previousArchive =
                Environment.GetEnvironmentVariable("CLAIMCORE_RESTORE_ARCHIVE_ROOT")

            Environment.SetEnvironmentVariable(
                "CLAIMCORE_RESTORE_ARCHIVE_ROOT",
                capture.ArchiveRoot
            )

            try
                withAdvancedPair capture owner app writer witness (fun facts access containers ->
                    advanced
                        action
                        witness
                        capture
                        registered
                        verified
                        reportKeyId
                        checkpointKeyId
                        reportKey
                        checkpointKey
                        facts
                        access
                        containers)
            finally
                Environment.SetEnvironmentVariable(
                    "CLAIMCORE_RESTORE_ARCHIVE_ROOT",
                    previousArchive
                ))
        owner
        app
        writer
        witness
