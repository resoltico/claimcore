module internal ClaimCore.IntegrationTests.RealDataActivationPlanFixture

open System.Threading
open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Expecto
open Npgsql
open NpgsqlTypes
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.FreshBaselineSupport

let private digest (value: string) =
    SHA256.HashData(Encoding.ASCII.GetBytes value) |> Convert.ToHexStringLower

let private canonicalPlan (profile: ReviewedDeploymentProfile) (tip: Snapshot) =
    let fields = SortedDictionary<string, objnull>(StringComparer.Ordinal)
    let text name value = fields.Add(name, box value)
    let number name value = fields.Add(name, box value)
    let id () = Guid.NewGuid().ToString("D")
    let sampleHash = digest "isolated activation mechanics"
    let tipHash = Convert.ToHexStringLower tip.TipHash
    text "format" "claimcore-real-data-activation-plan-1"
    text "installationId" (tip.Identity.InstallationId.ToString("D"))
    text "lineageId" (tip.Identity.LineageId.ToString("D"))
    number "epoch" tip.Identity.Epoch
    number "writerGeneration" tip.WriterGeneration
    text "policySha256" profile.BackupHealthPolicySha256

    text
        "publicationRootKeySha256"
        (SHA256.HashData(profile.PublicationRootKey) |> Convert.ToHexStringLower)

    text "cycleId" (id ())
    text "leaseId" (id ())
    text "captureNonce" sampleHash
    text "captureReceiptSha256" sampleHash
    number "backupCaptureSequence" tip.TipSequence
    text "backupCaptureHash" tipHash
    text "primaryBaseCopyId" (id ())
    text "witnessBaseCopyId" (id ())
    number "primaryBaseRevision" 2
    number "witnessBaseRevision" 2
    text "primaryBaseCiphertextSha256" (digest "primary base")
    text "witnessBaseCiphertextSha256" (digest "witness base")
    text "primaryBasePhysicalReceiptSha256" (digest "primary physical")
    text "witnessBasePhysicalReceiptSha256" (digest "witness physical")
    text "primaryBaseWalHorizon" "0/1000000"
    text "witnessBaseWalHorizon" "0/1000000"
    number "primaryWalSegmentBytes" 16777216
    number "witnessWalSegmentBytes" 16777216
    text "primarySystemId" "12345678900000000001"
    text "witnessSystemId" "12345678900000000002"
    number "primaryTimeline" 1
    number "witnessTimeline" 1
    text "checkpointObjectSha256" (digest "checkpoint")
    number "checkpointSequence" tip.TipSequence
    text "checkpointHash" tipHash
    text "testRestoreReportSha256" (digest "restore report")
    text "testRestoreFullAuditSha256" (digest "restore audit")
    number "testRestoreWitnessCutoff" tip.TipSequence
    text "testRestoreWitnessCutoffHash" tipHash
    number "minimumArtifactCutoffSequence" tip.TipSequence
    text "minimumPrimaryWalHorizon" "0/1000000"
    text "minimumWitnessWalHorizon" "0/1000000"
    let canonical = Encoding.ASCII.GetBytes(JsonSerializer.Serialize(fields) + "\n")

    InstallationUsePlanCodec.parse canonical
    |> Option.defaultWith (fun () -> failtest "Synthetic activation plan is not canonical.")

let publishSyntheticPlan owner (witness: WitnessProtocol) profile =
    let plan =
        canonicalPlan profile ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()))

    let digestBytes = Convert.FromHexString plan.PlanSha256

    let planId =
        InstallationUseActivationCandidate.planIdFromDigest plan.InstallationId digestBytes

    let activationId =
        InstallationUseActivationCandidate.eventIdFromPlan plan.InstallationId digestBytes

    let intent =
        (witness
            .BeginAuthority(planId, plan.Canonical, None, CancellationToken.None)
            .GetAwaiter()
            .GetResult())

    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "INSERT INTO claimcore.installation_data_use_plans "
            + "(plan_id,activation_id,installation_id,lineage_id,witness_epoch,writer_generation,"
            + "policy_sha256,plan_sha256,canonical_plan,published_at,witness_intent_sequence,"
            + "witness_epoch_at_publication,witness_intent_hash) VALUES "
            + "(@plan,@activation,@installation,@lineage,@epoch,@generation,@policy,@digest,"
            + "@canonical,clock_timestamp(),@sequence,@witnessEpoch,@hash)",
            connection
        )

    Sql.uuid command "plan" planId
    Sql.uuid command "activation" activationId
    Sql.uuid command "installation" plan.InstallationId
    Sql.uuid command "lineage" plan.LineageId
    Sql.integer command "epoch" plan.Epoch
    Sql.integer command "generation" plan.WriterGeneration
    Sql.add command "policy" NpgsqlDbType.Bytea (box (Convert.FromHexString plan.PolicySha256))
    Sql.add command "digest" NpgsqlDbType.Bytea (box digestBytes)
    Sql.add command "canonical" NpgsqlDbType.Bytea (box plan.Canonical)
    Sql.integer command "sequence" intent.Ticket.Sequence
    Sql.integer command "witnessEpoch" intent.Ticket.Epoch
    Sql.add command "hash" NpgsqlDbType.Bytea (box intent.Ticket.EntryHash)
    Expect.equal (command.ExecuteNonQuery()) 1 "One isolated plan projection was staged."
    witness.SettleAuthority(planId, intent) |> await |> ignore
    planId, activationId, plan

let withRealDataBootstrap action =
    withDatabase (fun owner app ->
        let profile =
            {
                PublicationRootKey = Array.create 32 0x5Duy
                BackupHealthPolicySha256 = digest "isolated review policy"
            }

        SchemaBaseline.initializeRealDataWithReviewedProfile
            profile
            owner
            "Etc/UTC"
            syntheticSuppressionCheck
        |> completedAdministration

        withWitnessForScope InstallationUseScope.RealData owner (fun writer capability ->
            use connection = new NpgsqlConnection(owner)
            connection.Open()

            use identityCommand =
                new NpgsqlCommand(
                    "SELECT installation_id,lineage_id,witness_epoch FROM claimcore.installation_lineage",
                    connection
                )

            use reader = identityCommand.ExecuteReader()

            if not (reader.Read()) then
                failtest "Isolated REAL_DATA identity is absent."

            let identity: Identity =
                {
                    InstallationId = reader.GetGuid(0)
                    LineageId = reader.GetGuid(1)
                    Epoch = reader.GetInt64(2)
                }

            reader.Close()
            let store = new Store(writer, identity, capability)

            let keyId, _ = (store.ReadKeyCheck(CancellationToken.None).GetAwaiter().GetResult())

            use custody = new KeyRing(keyId, [ keyId, witnessKey () ]) :> IKeyCustody
            use witness = new WitnessProtocol(store, custody, identity)
            (witness.Admit(CancellationToken.None).GetAwaiter().GetResult())
            action owner app writer witness profile))
