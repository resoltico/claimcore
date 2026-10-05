module ClaimCore.IntegrationTests.DatabaseWitnessPruneProcessTests

open System
open System.Security.Cryptography
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Database
open ClaimCore.HostSecurity
open ClaimCore.Witness
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture
open ClaimCore.IntegrationTests.ManagedCopyInventoryDocuments
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.DatabaseWitnessPruneProcessInputs

let private ct = CancellationToken.None

let private approve (runtime: Runtime) steward request =
    let id = Guid.NewGuid()

    match
        (runtime.ForActor steward)
            .Tombstones.ApproveWitnessPrune(request, id, request.ValidUntil.AddMinutes(-1.0), ct)
        |> await
    with
    | TombstoneWriteOutcome.Applied(value, _) when value = id -> ()
    | _ -> failtest "Synthetic steward approval failed."

let private stagePartial
    directory
    owner
    writer
    (witness: WitnessProtocol)
    (commitments: ISuppressionCommitments)
    registryPath
    inspectionPath
    copyKeyPath
    files
    (request: TombstonePruneProposal)
    =
    withEnvironment registryPath inspectionPath copyKeyPath (fun () ->
        use provider =
            DatabaseManagedCopyInventory.TryLoadFromPrivateConfiguration()
            |> Option.defaultWith (fun () -> failtest "Signed inventory was unavailable")

        match
            CaseTombstonePruneOwner.execute
                owner
                writer
                witness
                commitments
                (provider :> IManagedCopyErasureClearance)
                request
                ct
            |> await
        with
        | OwnerWitnessPruneOutcome.Unconfirmed id when id = request.EventId -> ()
        | _ -> failtest "Synthetic partial state was not uncertain.")

    let changed =
        { request with
            TargetCount = request.TargetCount + 1L
        }

    let path =
        privateBytes directory "changed.proposal" (CaseTombstoneCandidate.proposal changed)

    rejectedProposal files path

    Expect.isNone
        ((witness.EvidenceStore
            .TryReadEvidence(request.EventId, SettledAuthority, CancellationToken.None)
            .GetAwaiter()
            .GetResult()))
        "Changed proposal did not settle the original intent"

let private livePurgedReview
    owner
    ownerConnection
    (witness: WitnessProtocol)
    runtime
    proposer
    first
    second
    =
    let _, input, livePurge =
        CaseErasurePurgeTests.proposal runtime proposer first second

    let caseId = CaseErasurePurgeTests.caseId owner input.CaseReference
    let draft = CaseLifecycleCandidate.draft caseId livePurge
    let commitments = FixturePrivateFiles.syntheticCommitments witness.Identity

    match
        CaseErasurePurge.execute
            owner
            ownerConnection
            witness
            commitments
            (CaseErasurePurgeTests.syntheticInventory caseId)
            draft
            ct
        |> await
    with
    | OwnerPurgeOutcome.Purged _ -> ()
    | _ -> failtest "Synthetic prerequisite live purge failed."

    let reviewed =
        match (runtime.ForActor first).Tombstones.Review(caseId, ct) |> await with
        | TombstoneReviewOutcome.Available value -> value
        | _ -> failtest "Opaque steward review failed."

    commitments, reviewed


let private beforeCommand
    stage
    directory
    owner
    writer
    witness
    commitments
    registryPath
    inspectionPath
    copyKeyPath
    files
    proposalPath
    request
    =
    if stage then
        stagePartial
            directory
            owner
            writer
            witness
            commitments
            registryPath
            inspectionPath
            copyKeyPath
            files
            request
    else
        invalidFilesRefused directory files proposalPath
        wrongCapabilityRefused directory owner witness files proposalPath request

let private signedInventoryPaths directory algorithm registryKey inspectorKey registry inspection =
    let registryPath =
        privateBytes directory "registry.json" (envelope algorithm registryKey registry)

    let inspectionPath =
        privateBytes directory "inspection.json" (envelope algorithm inspectorKey inspection)

    registryPath, inspectionPath

let private executeSigned
    stage
    owner
    writer
    (witness: WitnessProtocol)
    (runtime: Runtime)
    first
    second
    commitments
    reviewed
    algorithm
    registryKey
    inspectorKey
    registry
    inspection
    =
    let directory = privateRoot ()

    try
        let registryPath, inspectionPath =
            signedInventoryPaths directory algorithm registryKey inspectorKey registry inspection

        let copyKey = RandomNumberGenerator.GetBytes(32)

        try
            let copyKeyPath = privateBytes directory "copy-commitment.key" copyKey
            let request = proposal reviewed

            let proposalPath =
                privateBytes directory "prune.proposal" (CaseTombstoneCandidate.proposal request)

            approve runtime first request
            approve runtime second request

            let files =
                inputFiles directory owner writer witness registryPath inspectionPath copyKeyPath

            preflight owner witness commitments reviewed registryPath inspectionPath copyKeyPath

            beforeCommand
                stage
                directory
                owner
                writer
                witness
                commitments
                registryPath
                inspectionPath
                copyKeyPath
                files
                proposalPath
                request

            completeAndRetry files proposalPath owner witness commitments
        finally
            CryptographicOperations.ZeroMemory(copyKey)
    finally
        IO.Directory.Delete(directory, true)

let private run
    stage
    owner
    _
    (witness: WitnessProtocol)
    (runtime: Runtime)
    proposer
    first
    second
    _
    writer
    =
    let custodianOne = human "witness-prune-registry-custodian"
    let custodianTwo = human "witness-prune-inspector-custodian"
    use ownerConnection = new NpgsqlConnection(owner)
    ownerConnection.Open()

    let registryKey, inspectorKey, algorithm, registryKeyId, inspectorKeyId =
        registerInventorySigners runtime proposer custodianOne custodianTwo witness ownerConnection

    use registryKey = registryKey
    use inspectorKey = inspectorKey

    let commitments, reviewed =
        livePurgedReview owner ownerConnection witness runtime proposer first second

    let tip = (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult())

    Expect.equal tip.TipSequence reviewed.CutoffSequence "Inventory binds the reviewed cutoff"
    let now = DateTimeOffset.UtcNow
    let registry = registryBodyForEntries tip registryKeyId [] [] now

    let inspection =
        inspectionBodyForObservations tip inspectorKeyId (digest registry) [] now

    executeSigned
        stage
        owner
        writer
        witness
        runtime
        first
        second
        commitments
        reviewed
        algorithm
        registryKey
        inspectorKey
        registry
        inspection

let tests =
    testList
        "owner witness prune process"
        [
            testCase
                "[CC-ERASE-001] owner process prunes from private proposal and signed inventory with exact retry"
                (fun _ -> CaseLifecycleStoreFixture.setup (run false))
            testCase
                "[CC-ERASE-001] owner process retries committed intent and refuses changed proposal"
                (fun _ -> CaseLifecycleStoreFixture.setup (run true))
        ]
