module internal ClaimCore.IntegrationTests.WriterActivationSyntheticTail

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Expecto
open Npgsql
open NSec.Cryptography
open ClaimCore.Database
open ClaimCore.IntegrationTests.RestoreWriterHandoffContext

let private hash (value: byte array) =
    SHA256.HashData(value) |> Convert.ToHexStringLower

let private checkpointHolder (context: SettledW1Context) =
    use connection = new NpgsqlConnection(context.Access.Owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT holder_actor_id FROM claimcore.managed_copy_signers "
            + "WHERE signing_key_id=@key",
            connection
        )

    command.Parameters.AddWithValue("key", context.Input.Index.CheckpointSignerKeyId)
    |> ignore

    command.ExecuteScalar() :?> Guid

/// A test-assembly-only W2 proof; this does not qualify a physical final WAL tail.
let syntheticTail (context: SettledW1Context) : FencedTailVerification =
    let supplement =
        Encoding.ASCII.GetBytes("claimcore-synthetic-w2-tail-mechanics-1\n")

    let signature = SignatureAlgorithm.Ed25519.Sign(context.CheckpointKey, supplement)
    use fence = JsonDocument.Parse(context.Fence)

    let probe =
        fence.RootElement.GetProperty("independentProbeSha256").GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Signed fence probe is missing.")

    {
        Scope = "synthetic-only"
        InstallationId = context.Witness.Identity.InstallationId
        LineageId = context.Witness.Identity.LineageId
        Epoch = context.Witness.Identity.Epoch
        WriterGeneration = context.Facts.WriterGeneration + 1L
        HandoffId = context.HandoffId
        W1Sequence = context.W1Sequence
        W1Hash = Convert.ToHexStringLower(context.W1Hash)
        PublicationManifestSha256 = context.Input.Publication.ManifestSha256
        ReportSha256 = context.Report.Evidence.ReportSha256
        FenceReportSha256 = hash context.Fence
        SupplementSha256 = hash supplement
        FinalWalObjectSha256 = hash (Encoding.ASCII.GetBytes("synthetic-final-object-set"))
        FinalWalObjects = 2
        IndependentProbeSha256 = probe
        ProbeEvidenceSha256 = probe
        CheckpointSignerKeyId = context.Input.Index.CheckpointSignerKeyId
        CheckpointHolderActorId = checkpointHolder context
        SignedReport = context.Report.Evidence.Report
        ReportSignature = context.Report.ReportSignature
        SignedFence = context.Fence
        FenceSignature = context.FenceSignature
        SignedSupplement = supplement
        SupplementSignature = signature
        ValidUntil = context.ValidUntil
    }
