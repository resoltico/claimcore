module ClaimCore.IntegrationTests.RealDataActivationApprovalTests

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.RealDataActivationPlanFixture

let private owners app (witness: WitnessProtocol) first second =
    use source = RuntimeDataSource.create app
    let store = source
    let registry = new ActorGrantRegistry(source, witness)
    registry.RegisterActor(first, second) |> await |> applied

    registry.SetGrant(
        first,
        actorId store second,
        {
            Role = Role.Owner
            Scope = GrantScope.Installation
        },
        true
    )
    |> await
    |> applied

let private reviewed (runtime: Runtime) principal planId =
    match
        (runtime.ForActor principal).ReviewRealDataActivation(planId, CancellationToken.None)
        |> await
    with
    | RealDataActivationPlanReviewOutcome.Reviewed value -> value
    | _ -> failtest "Synthetic human owner could not review the exact witnessed plan."

let private request (value: RealDataActivationPlanReview) (tip: Snapshot) =
    {
        ApprovalId = Guid.NewGuid()
        PlanId = value.PlanId
        ActivationId = value.ActivationId
        InstallationId = value.InstallationId
        LineageId = value.LineageId
        Epoch = value.Epoch
        WriterGeneration = value.WriterGeneration
        ActivationPlanSha256 = Array.copy value.PlanSha256
        PolicySha256 = Array.copy value.PolicySha256
        ReviewWitnessSequence = tip.TipSequence
        ReviewWitnessHash = Array.copy tip.TipHash
        ExpectedWitnessSequence = tip.TipSequence
        ExpectedWitnessHash = Array.copy tip.TipHash
        ExpiresAt = value.ApprovalExpiresNoLaterThan.AddMinutes(-1.)
    }

let private requireApproved approvalId =
    function
    | RealDataActivationApprovalOutcome.Approved(id, revision) when id = approvalId && revision > 0L ->
        revision
    | _ -> failtest "Human owner activation approval was not witnessed."

let private reviewDenials (runtime: Runtime) outsider nonhuman planId =
    let coreOutsider = runtime.ForActor outsider
    let coreService = runtime.ForActor nonhuman
    let absent = Guid.NewGuid()

    let denied =
        function
        | RealDataActivationPlanReviewOutcome.ResourceUnavailable -> ()
        | _ -> failtest "Absent and inaccessible plans must share one refusal."

    coreOutsider.ReviewRealDataActivation(planId, CancellationToken.None)
    |> await
    |> denied

    coreOutsider.ReviewRealDataActivation(absent, CancellationToken.None)
    |> await
    |> denied

    coreService.ReviewRealDataActivation(planId, CancellationToken.None)
    |> await
    |> denied

let private approve (core: IActorClaimsCore) (input: RealDataActivationApprovalRequest) =
    core.ApproveRealDataActivation(input, CancellationToken.None) |> await

let private unavailableApproval =
    function
    | RealDataActivationApprovalOutcome.ResourceUnavailable -> ()
    | _ -> failtest "Activation approval must refuse without disclosing authority."

let private approvalDenials (witness: WitnessProtocol) coreOutsider coreService firstApproval =
    let before =
        (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence

    approve coreOutsider firstApproval |> unavailableApproval

    approve coreService firstApproval |> unavailableApproval

    Expect.equal
        ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
        before
        "Denied actors append no approval."

let private approvePair
    (witness: WitnessProtocol)
    coreOne
    coreTwo
    (firstApproval: RealDataActivationApprovalRequest)
    =
    let before =
        (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence

    let firstRevision =
        approve coreOne firstApproval |> requireApproved firstApproval.ApprovalId

    let settledOne = (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult())

    Expect.equal
        settledOne.TipSequence
        (before + 2L)
        "First owner approval has exact two-ticket settlement."

    let duplicate =
        { firstApproval with
            ApprovalId = Guid.NewGuid()
            ExpectedWitnessSequence = settledOne.TipSequence
            ExpectedWitnessHash = Array.copy settledOne.TipHash
        }

    approve coreOne duplicate |> unavailableApproval

    Expect.equal
        ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
        settledOne.TipSequence
        "Duplicate owner denial appends nothing."

    let secondApproval =
        { duplicate with
            ApprovalId = Guid.NewGuid()
        }

    approve coreTwo secondApproval
    |> requireApproved secondApproval.ApprovalId
    |> ignore

    let settledTwo = (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult())

    Expect.equal
        settledTwo.TipSequence
        (before + 4L)
        "Two human approvals are consecutive witnessed pairs."

    firstRevision, settledTwo.TipSequence

let private exactRetry
    (witness: WitnessProtocol)
    coreOne
    firstApproval
    firstRevision
    finalSequence
    =
    match approve coreOne firstApproval with
    | RealDataActivationApprovalOutcome.Approved(id, revision) when
        id = firstApproval.ApprovalId && revision = firstRevision
        ->
        ()
    | _ -> failtest "Exact first approval retry was not definite."

    Expect.equal
        ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
        finalSequence
        "Exact retry does not append."

    let changed =
        { firstApproval with
            PolicySha256 = Array.create 32 0xFFuy
        }

    approve coreOne changed |> unavailableApproval

let private twoHumanOwners owner app writer (witness: WitnessProtocol) profile =
    let first = human "activation-owner-one"
    let second = human "activation-owner-two"
    let outsider = human "activation-outsider"
    let nonhuman = service "activation-service"
    provision owner witness first |> applied
    owners app witness first second
    let planId, _, plan = publishSyntheticPlan owner witness profile
    use runtime = openRuntime app writer
    let coreOne = runtime.ForActor first
    let coreTwo = runtime.ForActor second
    let value = reviewed runtime first planId
    Expect.equal value.CanonicalPlan plan.Canonical "Review returns exact published plan bytes."

    Expect.equal
        value.PlanSha256
        (Convert.FromHexString plan.PlanSha256)
        "Review binds plan digest."

    reviewDenials runtime outsider nonhuman planId

    let anchor = (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult())

    let firstApproval = request value anchor
    approvalDenials witness (runtime.ForActor outsider) (runtime.ForActor nonhuman) firstApproval
    let firstRevision, finalSequence = approvePair witness coreOne coreTwo firstApproval
    exactRetry witness coreOne firstApproval firstRevision finalSequence

    use source = RuntimeDataSource.create app
    let store = source
    let registry = new ActorGrantRegistry(source, witness)

    registry.SetGrant(
        first,
        actorId store second,
        {
            Role = Role.Owner
            Scope = GrantScope.Installation
        },
        false
    )
    |> await
    |> applied

    approve
        coreTwo
        { firstApproval with
            ApprovalId = Guid.NewGuid()
        }
    |> unavailableApproval

    use audit = RuntimeDatabase.openConnection source
    DataAudit.run audit witness CancellationToken.None |> await |> ignore

let private staleTipAndPlanTamper owner app writer (witness: WitnessProtocol) profile =
    let principal = human "activation-stale-owner"
    let other = human "activation-tip-advance"
    provision owner witness principal |> applied
    let planId, _, _ = publishSyntheticPlan owner witness profile
    use runtime = openRuntime app writer
    let review = reviewed runtime principal planId

    let requestAtReview =
        request review ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()))

    use source = RuntimeDataSource.create app
    let registry = new ActorGrantRegistry(source, witness)
    registry.RegisterActor(principal, other) |> await |> applied

    let advancedTip =
        (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence

    approve (runtime.ForActor principal) requestAtReview |> unavailableApproval

    Expect.equal
        ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
        advancedTip
        "Stale review tip appends no approval."

    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use tamper =
        new NpgsqlCommand(
            "UPDATE claimcore.installation_data_use_plans "
            + "SET canonical_plan=canonical_plan || decode('00','hex') WHERE plan_id=@plan",
            connection
        )

    Sql.uuid tamper "plan" planId
    Expect.equal (tamper.ExecuteNonQuery()) 1 "One isolated plan byte string was altered."

    match
        (runtime.ForActor principal).ReviewRealDataActivation(planId, CancellationToken.None)
        |> await
    with
    | RealDataActivationPlanReviewOutcome.ResourceUnavailable -> ()
    | _ -> failtest "Changed published plan bytes must not be disclosed as an approved review."

let tests =
    testList
        "real-data activation owner approvals"
        [
            testCase
                "[CC-BACKUP-001] isolated witnessed plan review and two distinct human-owner approvals"
                (fun _ -> withRealDataBootstrap twoHumanOwners)
            testCase
                "[CC-BACKUP-001] stale activation review tip and changed published plan bytes refuse"
                (fun _ -> withRealDataBootstrap staleTipAndPlanTamper)
        ]
