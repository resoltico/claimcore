module ClaimCore.IntegrationTests.RecoveryArtifactExportTests

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.RecoveryArtifactExportTestSupport

let private retained source operationId =
    use connection = RuntimeDatabase.openConnection source

    PreparationData.readHeader connection None operationId
    |> await
    |> Option.defaultWith (fun () -> failtest "Synthetic preparation must be retained.")

let private exportContext source (witness: WitnessProtocol) owner operationId =
    let gate =
        new PostgresActorGate(source, FixturePrivateFiles.syntheticCommitments witness.Identity)
        :> IActorGate

    gate.Operation(owner, EndpointAction.RecoveryExport, operationId, CancellationToken.None)
    |> await
    |> Option.defaultWith (fun () -> failtest "Recovery exporter grant must admit the operation.")

let private decode (scenario: Scenario) bytes =
    RecoveryEnvelopeV3.decode
        65536
        (fun candidate ->
            if candidate = scenario.Key.Id then
                Some(scenario.Encryption, scenario.Mac)
            else
                None)
        scenario.Witness.Identity.InstallationId
        scenario.Witness.Identity.Epoch
        scenario.Now
        scenario.Key.Lifetime
        bytes

let private tryIssue (scenario: Scenario) =
    RecoveryArtifactExportIssue.issue
        scenario.Source
        scenario.Witness
        scenario.Context
        scenario.Retained
        scenario.Key
        (fun () -> scenario.Now)
        (RecoveryEnvelopeV3.encode 65536 scenario.Encryption scenario.Mac)
        (fun bytes ->
            match decode scenario bytes with
            | Ok artifact ->
                artifact.OperationId = scenario.Retained.OperationId
                && artifact.CanonicalRequest = scenario.Retained.CanonicalRequest
            | Error _ -> false)
        CancellationToken.None
    |> await

let private issue scenario =
    tryIssue scenario
    |> Result.defaultWith (fun _ -> failtest "Synthetic export failed.")

let private checkOpaque
    (scenario: Scenario)
    id
    (artifact: RecoveryArtifactV3)
    (row: RecoveryArtifactExportRow)
    =
    Expect.equal artifact.ExportId id "Envelope binds stable witnessed export ID"
    Expect.equal artifact.ExporterActorId scenario.Context.Binding.ActorId "Envelope binds exporter"

    Expect.sequenceEqual
        artifact.CanonicalRequest
        scenario.Retained.CanonicalRequest
        "Exact request"

    let evidence =
        {
            ExportId = id
            Artifact = artifact
            ExporterActorId = scenario.Context.Binding.ActorId
            ExporterGrantRevision = scenario.Context.Binding.GrantRevision
            ArtifactBytes = row.ArtifactBytes
            KeyIssuanceOrdinal = 1
            KeyMaximumExports = scenario.Key.MaximumExports
        }

    Expect.isTrue
        (RecoveryArtifactExportCandidate.verifyStored evidence row.CanonicalAction)
        "Opaque candidate matches encrypted artifact metadata"

    let changed = Array.copy row.CanonicalAction
    changed[0] <- changed[0] ^^^ 1uy

    Expect.isFalse
        (RecoveryArtifactExportCandidate.verifyStored evidence changed)
        "Changed candidate"

let private checkIssued (scenario: Scenario) id bytes =
    let artifact =
        decode scenario bytes
        |> Result.defaultWith (fun _ -> failtest "Issued artifact must authenticate.")

    use connection = RuntimeDatabase.openConnection scenario.Source

    let row =
        RecoveryArtifactExportRead.find connection None id CancellationToken.None
        |> await
        |> Option.defaultWith (fun () -> failtest "Witnessed export row must exist.")

    checkOpaque scenario id artifact row
    checkAudit scenario id row

let private run (scenario: Scenario) =
    let first = issue scenario
    let replay = issue scenario
    Expect.sequenceEqual replay first "Same exporter and grant replay exact encrypted bytes"

    let id =
        RecoveryArtifactExportCandidate.exportId
            scenario.Witness.Identity.InstallationId
            scenario.Witness.Identity.Epoch
            scenario.Retained.OperationId
            scenario.Context.Binding.ActorId
            scenario.Context.Binding.GrantRevision

    Expect.equal
        (inventoryCount scenario.OwnerConnection id)
        (1L, 1L)
        "One export and one managed copy"

    checkIssued scenario id first

let private openSyntheticRuntime app writer =
    let opening =
        Runtime.OpenPostgres(
            app,
            writer,
            witnessKey (),
            suppressionKeyFile (),
            artifactKeyRingFile (),
            CancellationToken.None
        )
        |> await

    match opening with
    | Ok value -> value
    | Error RuntimeOpenFault.RuntimeConfigurationInvalid ->
        failtest "Synthetic recovery runtime configuration was invalid."
    | Error RuntimeOpenFault.RuntimeSchemaMismatch ->
        failtest "Synthetic recovery runtime schema mismatched."
    | Error RuntimeOpenFault.RuntimeStoreUnavailable ->
        failtest "Synthetic recovery runtime store was unavailable."
    | Error RuntimeOpenFault.RuntimeStoreIntegrityError ->
        failtest "Synthetic recovery runtime integrity was refused."
    | Error RuntimeOpenFault.RuntimeCancelled ->
        failtest "Synthetic recovery runtime opening was cancelled."

let private withScenario action =
    withAuthorityRuntimeDatabase (fun ownerConnection app writer witness ->
        let owner = human "artifact-export-owner"
        provision ownerConnection witness owner |> applied
        use source = RuntimeDataSource.create app
        grant source witness owner Role.CaseEditor
        grant source witness owner Role.RecoveryExporter
        use _artifactKeys = RecoveryArtifactKeyCustody.load (artifactKeyRingFile ())

        use runtime = openSyntheticRuntime app writer

        let request =
            openRequest (Guid.NewGuid()) ("ARTIFACT-" + Guid.NewGuid().ToString("N"))

        acceptedCase (runtime.ForActor owner) request
        let retained = retained source request.OperationId
        let context = exportContext source witness owner request.OperationId
        Expect.equal context.CaseId (Some retained.CaseId) "Export scope matches case"
        let now = DateTimeOffset.UtcNow

        action
            {
                OwnerConnection = ownerConnection
                Source = source
                Witness = witness
                Retained = retained
                Context = context
                Now = now
                Encryption = Array.init 32 (fun index -> byte (index + 1))
                Mac = Array.init 32 (fun index -> byte (index + 41))
                Key =
                    {
                        Id = Guid.NewGuid()
                        IssueFrom = now.AddDays(-1.)
                        IssueUntil = now.AddDays(1.)
                        Lifetime = TimeSpan.FromHours(1.)
                        MaximumExports = 4
                    }
            })

let private exactReplay () = withScenario run

let private concurrentPrune () =
    withScenario (fun scenario ->
        let operationId = scenario.Retained.OperationId
        runtimeCannotPrune scenario.Source operationId
        ageAccepted scenario.OwnerConnection operationId

        let issuing = Task.Run(fun () -> tryIssue scenario)

        let pruning =
            Task.Run(fun () ->
                PreparationPruning.prune
                    scenario.OwnerConnection
                    { PreparationPruneOptions.defaults with
                        SettledRetentionDays = 1
                    }
                |> completedAdministration)

        Task.WaitAll [| issuing :> Task; pruning :> Task |]

        let exportId =
            RecoveryArtifactExportCandidate.exportId
                scenario.Witness.Identity.InstallationId
                scenario.Witness.Identity.Epoch
                operationId
                scenario.Context.Binding.ActorId
                scenario.Context.Binding.GrantRevision

        match issuing.Result with
        | Ok _ ->
            Expect.equal
                (preparationCount scenario.OwnerConnection operationId)
                1L
                "Pruning cannot remove an unexpired issued preparation"

            Expect.equal (inventoryCount scenario.OwnerConnection exportId) (1L, 1L) "One copy"
        | Error CoreFault.RecoveryStoreUnavailable ->
            Expect.equal
                (preparationCount scenario.OwnerConnection operationId)
                0L
                "Pruning won before export admission"

            Expect.equal (inventoryCount scenario.OwnerConnection exportId) (0L, 0L) "No copy"
        | Error _ -> failtest "Concurrent export must have a definite winner.")

let tests =
    testList
        "witnessed recovery artifact export"
        [
            testCase
                "[CC-REC-001] export retries reuse settled ciphertext and managed copy"
                exactReplay
            testCase
                "[CC-REC-001] owner pruning and export serialize on one operation lock"
                concurrentPrune
        ]
