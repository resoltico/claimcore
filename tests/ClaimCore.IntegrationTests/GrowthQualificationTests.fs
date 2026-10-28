module ClaimCore.IntegrationTests.GrowthQualificationTests

open ClaimCore.TestSupport

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.GrowthMeasurements


let private cardinalityWrites cases keyRing (database: PostgresFixture.Database) =
    task {
        let database =
            { database with
                Fixture =
                    { database.Fixture with
                        Store = GrowthProtection.store keyRing database
                    }
            }

        let! actor = caseWorker database.Fixture

        do! openCases actor cases

        let! _ =
            writeLane
                actor
                {
                    Writes = 1000 - cases
                    Cases = cases
                    Actors = 0
                    Writers = 1
                }
                0

        let! measurement = verifyMeasured database
        let position = measurement.Position
        let elapsed = measurement.ElapsedMilliseconds
        let baseline = measurement.BaselineManagedBytes
        let peakManaged = measurement.PeakManagedBytes
        let peakWorking = measurement.PeakWorkingBytes

        Expect.equal
            position.Sequence
            (CaseFixtures.sequence (1003L))
            "Resource comparisons keep accepted history fixed"

        let! population = GrowthEnvironment.database database
        Expect.equal population.cases (int64 cases) "Compared resource population is exact"

        return
            {|
                cases = cases
                acceptedEntries = position.Sequence
                verificationMilliseconds = elapsed
                managedBytesBeforeVerification = baseline
                managedBytesAfterVerification = GC.GetTotalMemory false
                sampledPeakManagedBytes = peakManaged
                sampledPeakWorkingSetBytes = peakWorking
                database = population
            |}
    }

let private cardinalityProfile cases =
    task {
        let! database = PostgresFixture.create ()

        let! outcome =
            task {
                try
                    let! result =
                        GrowthProtection.withKeyRing (fun keyRing ->
                            cardinalityWrites cases keyRing database)

                    return Ok result
                with error ->
                    return Error error
            }

        match outcome with
        | Ok result ->
            database.Fixture.Dispose()
            return result
        | Error error ->
            ClaimCore.TestSupport.FixtureDisposal.afterFailure error [ database.Fixture.Dispose ]
            return raise error
    }

let private cardinality () =
    task {
        let reports = ResizeArray<_>()

        for cases in [ 1; 100; 1000 ] do
            let! report = cardinalityProfile cases
            reports.Add report

        return
            {|
                environment = GrowthEnvironment.source ()
                fixedCaseEntries = 1000
                fixedAuthorityEntries = 3
                profiles = reports.ToArray()
            |}
    }

let private profiles () =
    let required =
        [
            for writes in [ 1000; 10000; 100000 ] do
                for writers in [ 1; 4; 16 ] -> writes, writers
        ]

    match Environment.GetEnvironmentVariable("CLAIMCORE_GROWTH_PROFILE") |> Option.ofObj with
    | None -> required
    | Some selected ->
        match
            required
            |> List.tryFind (fun (writes, writers) ->
                selected = string writes + ":" + string writers)
        with
        | Some profile -> [ profile ]
        | None -> failtest "CLAIMCORE_GROWTH_PROFILE must name a required writes:writers profile."

let private workloads selected completed =
    async {
        for writes, writers in selected do
            let cases, actors = 1000, 99

            let! database = PostgresFixture.create () |> Async.AwaitTask

            let mutable failure: exn option = None

            try
                try
                    let! report =
                        GrowthProtection.withKeyRing (fun keyRing ->
                            measure
                                keyRing
                                {
                                    Writes = writes
                                    Cases = cases
                                    Actors = actors
                                    Writers = writers
                                }
                                database)
                        |> Async.AwaitTask

                    completed report
                with error ->
                    failure <- Some error
            finally
                match failure with
                | None -> database.Fixture.Dispose()
                | Some error ->
                    ClaimCore.TestSupport.FixtureDisposal.afterFailure
                        error
                        [ database.Fixture.Dispose ]

            Expect.notEqual
                (string database.Container.State)
                "Running"
                "Owned growth container is stopped"
    }

let private qualify () =
    async {
        let directory =
            Path.Combine(
                ClaimCore.TestSupport.RepositoryRoot.find (),
                "artifacts/verification/growth",
                Guid.NewGuid().ToString("N")
            )

        Directory.CreateDirectory directory |> ignore

        let atomic name value =
            let path = Path.Combine(directory, name)
            File.WriteAllBytes(path + ".writing", JsonSerializer.SerializeToUtf8Bytes value)
            File.Move(path + ".writing", path, true)

        let selected = profiles ()
        let mutable count = 0

        let nativeRun =
            Environment.GetEnvironmentVariable("CLAIMCORE_TEST_REPORT_DIRECTORY")
            |> Option.ofObj
            |> Option.map Path.GetFileName

        let state result =
            atomic
                "run.json"
                {|
                    result = result
                    completed = count
                    expected = selected.Length
                    requiredProfiles = 9
                    completeQualification = selected.Length = 9
                    nativeRun = nativeRun
                |}

        state "incomplete"

        try
            do!
                workloads selected (fun report ->
                    atomic (sprintf "workload-%02d.json" count) report
                    count <- count + 1
                    state "incomplete")

            let! resources = cardinality () |> Async.AwaitTask
            atomic "cardinality.json" resources
            state "completed"
        with error ->
            let failure =
                try
                    state "failed"
                    error
                with reportError ->
                    AggregateException(
                        "Qualification and report storage failed.",
                        [| error; reportError |]
                    )
                    :> exn

            return raise failure
    }

[<Tests>]
let tests =
    testSequenced (
        testCaseAsync "synthetic write contention and verifier growth qualification" (qualify ())
    )
