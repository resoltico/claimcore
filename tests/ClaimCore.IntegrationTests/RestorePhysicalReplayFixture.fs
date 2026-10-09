module internal ClaimCore.IntegrationTests.RestorePhysicalReplayFixture

open System
open System.IO
open System.Text.RegularExpressions
open Expecto
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.RestorePhysicalArchiveEvidence
open ClaimCore.IntegrationTests.RestorePhysicalConnections
open ClaimCore.IntegrationTests.RestorePhysicalCopyFixture
open ClaimCore.IntegrationTests.RestorePhysicalProcess
open ClaimCore.IntegrationTests.RestorePhysicalWalRange
open ClaimCore.IntegrationTests.RestoreRegisteredWalCapture
open ClaimCore.TestSupport
open System.Threading

let private startAdvancedPair capture owner (writer: string) =
    let script =
        Path.Combine(RepositoryRoot.find (), "eng/backup/Restore-AdvanceIsolatedPair.sh")

    let primaryHorizon, primarySegment = completedWalEndpoint owner
    let witnessOwner = Npgsql.NpgsqlConnectionStringBuilder(witnessOwnerConnection ())
    witnessOwner.Database <- Npgsql.NpgsqlConnectionStringBuilder(writer).Database

    let witnessHorizon, witnessSegment =
        completedWalEndpoint witnessOwner.ConnectionString

    let code, output, stage =
        run
            "bash"
            [
                script
                capture.SourcePrimaryContainerId
                capture.SourceWitnessContainerId
                capture.ScratchRoot
                prefix capture "PRIMARY" primaryHorizon primarySegment
                prefix capture "WITNESS" witnessHorizon witnessSegment
            ]

    if code <> 0 then
        failtest ("Advanced isolated restore failed at " + stage)

    let parts = output.Split(' ', StringSplitOptions.RemoveEmptyEntries)

    if
        parts.Length <> 4
        || not (Regex.IsMatch(parts[0], "^[0-9a-f]{64}$"))
        || not (Regex.IsMatch(parts[2], "^[0-9a-f]{64}$"))
        || parts[0] = parts[2]
    then
        failtest "Advanced restored pair returned invalid container identities"

    parts

let withAdvancedPair capture owner app writer (witness: WitnessProtocol) action =
    let captured = witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()
    let parts = startAdvancedPair capture owner writer

    try
        let facts = auditRestored owner app writer witness (int parts[1]) (int parts[3])

        Expect.equal
            facts.WitnessCutoff
            captured.TipSequence
            "Advanced restore has the settled source cutoff"

        Expect.equal
            facts.WitnessCutoffHash
            (Convert.ToHexStringLower captured.TipHash)
            "Advanced restore has the exact settled source witness hash"

        let access = restoredAccess owner app writer (int parts[1]) (int parts[3])
        action facts access (parts[0], parts[2])
    finally
        for id in [ parts[2]; parts[0] ] do
            run "docker" [ "stop"; id ] |> ignore
