module internal ClaimCore.IntegrationTests.RestorePhysicalReplayFixture

open System
open System.IO
open System.Text.RegularExpressions
open Expecto
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.RestorePhysicalArchiveEvidence
open ClaimCore.IntegrationTests.RestorePhysicalConnections
open ClaimCore.IntegrationTests.RestorePhysicalCopyFixture
open ClaimCore.IntegrationTests.RestorePhysicalProcess
open ClaimCore.TestSupport

let withAdvancedPair
    (capture: PhysicalCopyCapture)
    owner
    app
    writer
    (witness: WitnessProtocol)
    action
    =
    let script =
        Path.Combine(RepositoryRoot.find (), "eng/backup/Restore-AdvanceIsolatedPair.sh")

    let code, output, stage =
        run
            "bash"
            [
                script
                capture.SourcePrimaryContainerId
                capture.SourceWitnessContainerId
                capture.ScratchRoot
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

    try
        let facts = auditRestored owner app writer witness (int parts[1]) (int parts[3])
        let access = restoredAccess owner app writer (int parts[1]) (int parts[3])
        action facts access (parts[0], parts[2])
    finally
        for id in [ parts[2]; parts[0] ] do
            run "docker" [ "stop"; id ] |> ignore
