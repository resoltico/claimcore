module ClaimCore.IntegrationTests.RestoreWriterHandoffPhysicalTests

open ClaimCore.TestSupport
open Expecto
open System
open System.IO
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

let private checkCleanupScenario bodyFails stopKind =
    let identifiers = [ "witness"; "primary" ]

    let stopFails = stopKind <> "settled"
    let directory = RestorePhysicalProcess.privateScratch ()
    let stopped = ResizeArray<string>()
    let bodyError = InvalidOperationException("PRIVATE-BODY")

    try
        let mutable observed: exn option = None

        try
            FixtureCleanup.physical
                (fun id ->
                    stopped.Add(id)

                    if id = "witness" && stopKind = "exception" then
                        raise (TimeoutException("PRIVATE-STOP"))

                    if id = "witness" && stopKind = "nonzero" then 1 else 0)
                (fun () -> identifiers)
                directory
                (fun () ->
                    if bodyFails then
                        raise bodyError)
        with error ->
            observed <- Some error

        Expect.equal (List.ofSeq stopped) identifiers "Both known owned stops were attempted"

        Expect.equal observed.IsSome (bodyFails || stopFails) "Any unsettled stage refuses"

        Expect.equal
            (Directory.Exists(directory))
            (bodyFails || stopFails)
            "Delete only fully settled successful evidence"

        if bodyFails then
            Expect.isTrue
                (observed |> Option.exists (fun error -> obj.ReferenceEquals(error, bodyError)))
                "Original body failure survives"

        if bodyFails && stopKind = "exception" then
            Expect.equal
                bodyError.Data["FixtureCleanupFailure"]
                (box "timeout")
                "Thrown stop has a closed diagnostic"

            Expect.equal
                bodyError.Data["FixtureCleanupSettlement"]
                (box "unknown")
                "Thrown stop has no confirmed cleanup"
    finally
        if Directory.Exists(directory) then
            Directory.Delete(directory, true)

let private cleanupFailures =
    testCase
        "[CC-BACKUP-001] owned restore cleanup attempts every stop and retains unsettled evidence"
        (fun () ->
            for bodyFails, stopKind in
                [
                    true, "nonzero"
                    false, "nonzero"
                    true, "exception"
                    false, "exception"
                    true, "settled"
                    false, "settled"
                ] do
                checkCleanupScenario bodyFails stopKind)

let tests =
    testList
        "physical restored W1 owner handoff"
        [
            cleanupFailures
            testCase
                "[CC-BACKUP-001] signed physical report and post-isolation fence drive owner W1"
                (fun _ -> withAuthorityRuntimeDatabase (withSettledPhysicalPair (fun _ -> ())))
        ]
