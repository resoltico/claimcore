module ClaimCore.IntegrationTests.RestoreWriterHandoffPhysicalTests

open Expecto
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.RestoreProduceSignedPairTests
open ClaimCore.IntegrationTests.RestoreWriterHandoffPhysicalPreparation
open ClaimCore.IntegrationTests.RestoreWriterHandoffPhysicalIsolation

let internal withSettledPhysicalPair onSettled owner app writer (witness: WitnessProtocol) =
    withSignedPair
        (fun capture registered _ facts access containers input produced checkpointKey ->
            withPrepared
                capture
                registered
                facts
                access
                containers
                input
                produced
                witness.KeyCustody.ActiveKeyId
                (fun prepared ->
                    withIsolated
                        onSettled
                        capture
                        registered
                        facts
                        access
                        containers
                        input
                        produced
                        checkpointKey
                        witness.KeyCustody.ActiveKeyId
                        prepared))
        owner
        app
        writer
        witness

let tests =
    testList
        "physical restored W1 owner handoff"
        [
            testCase
                "[CC-BACKUP-001] signed physical report and post-isolation fence drive owner W1"
                (fun _ -> withAuthorityRuntimeDatabase (withSettledPhysicalPair (fun _ -> ())))
        ]
