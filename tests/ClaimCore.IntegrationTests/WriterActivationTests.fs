module internal ClaimCore.IntegrationTests.WriterActivationTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Database
open ClaimCore.HostSecurity
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.RestoreWriterHandoffContext
open ClaimCore.IntegrationTests.RestoreWriterHandoffPhysicalTests
open ClaimCore.IntegrationTests.RestoreFencedTailPhysicalTests
open ClaimCore.IntegrationTests.WriterActivationSyntheticTail

let private digest (value: string) = Convert.FromHexString value

let evidence (source: FencedTailVerification) : WriterActivationEvidence =
    {
        HandoffId = source.HandoffId
        InstallationId = source.InstallationId
        LineageId = source.LineageId
        Epoch = source.Epoch
        WriterGeneration = source.WriterGeneration
        W1Sequence = source.W1Sequence
        W1Hash = digest source.W1Hash
        PublicationManifestSha256 = digest source.PublicationManifestSha256
        ReportSha256 = digest source.ReportSha256
        FenceSha256 = digest source.FenceReportSha256
        SupplementSha256 = digest source.SupplementSha256
        FinalWalObjectSha256 = digest source.FinalWalObjectSha256
        FinalWalObjectCount = source.FinalWalObjects
        IndependentProbeSha256 = digest source.IndependentProbeSha256
        ProbeEvidenceSha256 = digest source.ProbeEvidenceSha256
        CheckpointSigningKeyId = source.CheckpointSignerKeyId
        CheckpointHolderActorId = source.CheckpointHolderActorId
        SignedReport = Array.copy source.SignedReport
        ReportSignature = Array.copy source.ReportSignature
        SignedFence = Array.copy source.SignedFence
        FenceSignature = Array.copy source.FenceSignature
        SignedSupplement = Array.copy source.SignedSupplement
        SupplementSignature = Array.copy source.SupplementSignature
        ValidUntil = source.ValidUntil
    }

let private qualification (value: WriterActivationEvidence) : WriterActivationQualification =
    {
        HandoffId = value.HandoffId
        InstallationId = value.InstallationId
        LineageId = value.LineageId
        Epoch = value.Epoch
        WriterGeneration = value.WriterGeneration
        W1Sequence = value.W1Sequence
        W1Hash = value.W1Hash
        PublicationManifestSha256 = value.PublicationManifestSha256
        ReportSha256 = value.ReportSha256
        FenceSha256 = value.FenceSha256
        SupplementSha256 = value.SupplementSha256
        FinalWalObjectSha256 = value.FinalWalObjectSha256
        FinalWalObjectCount = value.FinalWalObjectCount
        IndependentProbeSha256 = value.IndependentProbeSha256
        ProbeEvidenceSha256 = value.ProbeEvidenceSha256
        CheckpointSigningKeyId = value.CheckpointSigningKeyId
        CheckpointHolderActorId = value.CheckpointHolderActorId
        ValidUntil = value.ValidUntil
    }

let testVerifier result =
    { new IWriterActivationEvidenceVerifier with
        member _.Verify(_, _) = Task.FromResult result
    }

let openRestored (context: SettledW1Context) =
    let name = "CLAIMCORE_WRITER_CAPABILITY_FILE"
    let prior = Environment.GetEnvironmentVariable(name)

    let replacement =
        Environment.GetEnvironmentVariable("CLAIMCORE_NEW_WRITER_CAPABILITY_FILE")

    if String.IsNullOrWhiteSpace replacement then
        failtest "Restored writer capability file is unavailable."

    try
        Environment.SetEnvironmentVariable(name, replacement)

        Runtime.OpenPostgres(
            context.Access.App,
            context.Access.WitnessWriter,
            witnessKey (),
            suppressionKeyFile (),
            artifactKeyRingFile (),
            CancellationToken.None
        )
        |> await
    finally
        Environment.SetEnvironmentVariable(name, prior)

let private requireClosed context =
    match openRestored context with
    | Error _ -> ()
    | Ok runtime ->
        use unexpected = runtime
        failtest "A W1-settled but unactivated writer must remain quarantined."

let requireAuditedActivation
    (source: NpgsqlDataSource)
    (context: SettledW1Context)
    (suppression: ISuppressionCommitments)
    expectedCutoff
    =
    use connection = RuntimeDatabase.openConnection source

    let summary =
        DataAudit.runWithSuppression
            connection
            context.Witness
            (Some suppression)
            CancellationToken.None
        |> await

    Expect.equal summary.WriterActivations 1L "Full audit counts the exact W2 event."
    Expect.equal summary.WitnessCutoff expectedCutoff "Full audit reaches W2 settlement."
    Expect.equal summary.PendingIntents 0L "W2 leaves no unsettled witness intent."

let private requirePairEvidence
    (source: NpgsqlDataSource)
    (context: SettledW1Context)
    activationId
    sequence
    hash
    =
    use connection = RuntimeDatabase.openConnection source

    WriterActivationRead.verify
        connection
        context.Witness
        context.HandoffId
        (context.Facts.WriterGeneration + 1L)
        context.W1Sequence
        context.W1Hash
        activationId
        sequence
        hash

    use writer =
        new ClaimCore.Witness.Store(
            context.Access.WitnessWriter,
            context.Witness.Identity,
            context.NewCapability
        )

    writer.Admit()

let private verifyActivationRetry
    (context: SettledW1Context)
    (value: WriterActivationEvidence)
    (proof: WriterActivationQualification)
    expireBeforeRetry
    activate
    ticket
    =
    let id, sequence, hash = ticket

    if expireBeforeRetry then
        use owner = new NpgsqlConnection(context.Access.Owner)
        owner.Open()
        DatabaseObservation.afterInstant owner value.ValidUntil

    let retryVerifier =
        if expireBeforeRetry then
            testVerifier None
        else
            testVerifier (Some proof)

    match activate retryVerifier with
    | WriterActivationOutcome.Activated(replayedId, replayedSequence, replayedHash) ->
        Expect.equal
            (replayedId, replayedSequence, replayedHash)
            (id, sequence, hash)
            "Exact owner retry reuses the original W2 ticket."
    | _ -> failtest "An exact W2 retry must reconcile without a second activation."

    Expect.equal
        (context.Witness.Snapshot().TipSequence)
        sequence
        "Exact W2 retry cannot append another authority event."

let private activationFlow expireBeforeRetry (context: SettledW1Context) source =
    let value =
        let verified = evidence source

        if expireBeforeRetry then
            { verified with
                ValidUntil = utcMicrosecond (DateTimeOffset.UtcNow.AddSeconds(30.0))
            }
        else
            verified

    let proof = qualification value
    use owner = new NpgsqlConnection(context.Access.Owner)
    owner.Open()
    use dataSource = RuntimeDataSource.create context.Access.App
    let suppression = FixturePrivateFiles.syntheticCommitments context.Witness.Identity

    let activate verifier =
        WriterHandoffActivation.activate
            owner
            dataSource
            context.Access.WitnessOwner
            context.Witness
            verifier
            (Some suppression)
            value
            CancellationToken.None
        |> await

    requireClosed context
    let before = context.Witness.Snapshot().TipSequence

    match activate (testVerifier None) with
    | WriterActivationOutcome.Refused -> ()
    | _ -> failtest "Without independent qualification W2 must refuse before mutation."

    Expect.equal
        (context.Witness.Snapshot().TipSequence)
        before
        "Refused activation appends no witness event."

    let ticket =
        match activate (testVerifier (Some proof)) with
        | WriterActivationOutcome.Activated(id, sequence, hash) -> id, sequence, hash
        | _ -> failtest "Synthetic injected proof must activate the exact W1 pair."

    let id, sequence, hash = ticket

    Expect.isFalse
        (context.Witness.Snapshot().ActivationPending)
        "W2 settles restored writer authority."

    requireAuditedActivation dataSource context suppression sequence
    requirePairEvidence dataSource context id sequence hash

    match openRestored context with
    | Error fault ->
        let category =
            match fault with
            | RuntimeOpenFault.RuntimeConfigurationInvalid -> "CONFIGURATION_INVALID"
            | RuntimeOpenFault.RuntimeSchemaMismatch -> "SCHEMA_MISMATCH"
            | RuntimeOpenFault.RuntimeStoreUnavailable -> "STORE_UNAVAILABLE"
            | RuntimeOpenFault.RuntimeStoreIntegrityError -> "STORE_INTEGRITY_ERROR"
            | RuntimeOpenFault.RuntimeCancelled -> "CANCELLED"

        failtestf "Matched W2 runtime refused with safe category %s." category
    | Ok runtime ->
        use admitted = runtime
        ()

    verifyActivationRetry context value proof expireBeforeRetry activate ticket

let tests =
    testList
        "restored writer activation"
        [
            testCase
                "[CC-BACKUP-001] W1 settlement alone leaves restored case work quarantined"
                (fun _ -> withAuthorityRuntimeDatabase (withSettledPhysicalPair requireClosed))
            testCase
                "[CC-BACKUP-001] test-only verifier exercises W2 activation and exact retry"
                (fun _ ->
                    withAuthorityRuntimeDatabase (
                        withSettledPhysicalPair (fun context ->
                            activationFlow true context (syntheticTail context))
                    ))
            testCase
                "[CC-BACKUP-001] W1 stays quarantined until exact witnessed W2 activation"
                (fun _ ->
                    withAuthorityRuntimeDatabase (withVerifiedFencedTail (activationFlow false)))
        ]
