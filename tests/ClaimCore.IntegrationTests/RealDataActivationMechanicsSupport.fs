module internal ClaimCore.IntegrationTests.RealDataActivationMechanicsSupport

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport

let witnessOwnerFor (writer: string) =
    let database = NpgsqlConnectionStringBuilder(writer).Database
    let builder = NpgsqlConnectionStringBuilder(witnessOwnerConnection ())
    builder.Database <- database
    builder.ConnectionString

let private approved =
    function
    | RealDataActivationApprovalOutcome.Approved _ -> ()
    | _ -> failtest "A synthetic human owner approval was not witnessed."

let owners app (witness: WitnessProtocol) first second =
    use source = RuntimeDataSource.create app
    let registry = new ActorGrantRegistry(source, witness)
    let actors = new ActorGrantStore(source)
    registry.RegisterActor(first, second) |> await |> applied

    registry.SetGrant(
        first,
        actorId actors second,
        {
            Role = Role.Owner
            Scope = GrantScope.Installation
        },
        true
    )
    |> await
    |> applied

let private approval
    planId
    activationId
    (plan: BackupHealthActivationPlan)
    (anchor: Snapshot)
    (prior: Snapshot)
    =
    {
        ApprovalId = Guid.NewGuid()
        PlanId = planId
        ActivationId = activationId
        InstallationId = plan.InstallationId
        LineageId = plan.LineageId
        Epoch = plan.Epoch
        WriterGeneration = plan.WriterGeneration
        ActivationPlanSha256 = Convert.FromHexString plan.PlanSha256
        PolicySha256 = Convert.FromHexString plan.PolicySha256
        ReviewWitnessSequence = anchor.TipSequence
        ReviewWitnessHash = Array.copy anchor.TipHash
        ExpectedWitnessSequence = prior.TipSequence
        ExpectedWitnessHash = Array.copy prior.TipHash
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1.)
    }

let approvePair app writer (witness: WitnessProtocol) first second planId activationId plan =
    use runtime = openRuntime app writer
    let anchor = witness.Snapshot()
    let one = approval planId activationId plan anchor anchor

    runtime.ForActor(first).ApproveRealDataActivation(one, CancellationToken.None)
    |> await
    |> approved

    let prior = witness.Snapshot()
    let two = approval planId activationId plan anchor prior

    runtime.ForActor(second).ApproveRealDataActivation(two, CancellationToken.None)
    |> await
    |> approved

    one.ApprovalId, two.ApprovalId

let proof (plan: BackupHealthActivationPlan) (tip: Snapshot) =
    let canonical = Encoding.ASCII.GetBytes("synthetic activation mechanics only")
    let now = DateTimeOffset.UtcNow

    {
        InstallationId = plan.InstallationId
        LineageId = plan.LineageId
        Epoch = plan.Epoch
        WriterGeneration = plan.WriterGeneration
        PolicySha256 = plan.PolicySha256
        PolicyCanonical = Encoding.ASCII.GetBytes("synthetic reviewed policy")
        CertificateSha256 = SHA256.HashData(canonical) |> Convert.ToHexStringLower
        WitnessTipSequence = tip.TipSequence
        WitnessTipHash = Array.copy tip.TipHash
        KnownCopyInventorySha256 = SHA256.HashData(Encoding.ASCII.GetBytes("synthetic inventory"))
        CheckedAtDatabase = now.AddSeconds(-1.)
        ValidUntil = now.AddSeconds(45.)
        SignerKeyId = Guid.NewGuid()
        SignerHolderActorId = Guid.NewGuid()
        Canonical = canonical
        Signature = Array.create 64 0x31uy
    }
