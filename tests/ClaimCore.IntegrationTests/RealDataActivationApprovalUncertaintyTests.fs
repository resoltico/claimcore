module ClaimCore.IntegrationTests.RealDataActivationApprovalUncertaintyTests

open System
open System.Data
open System.Security.Cryptography
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.FixturePrivateFiles
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.RealDataActivationPlanFixture

let private request planId activationId (plan: BackupHealthActivationPlan) (tip: Snapshot) =
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
        ReviewWitnessSequence = tip.TipSequence
        ReviewWitnessHash = Array.copy tip.TipHash
        ExpectedWitnessSequence = tip.TipSequence
        ExpectedWitnessHash = Array.copy tip.TipHash
        ExpiresAt = utcMicrosecond (DateTimeOffset.UtcNow.AddHours(1.))
    }

let private actorContext app (witness: WitnessProtocol) principal =
    let source = RuntimeDataSource.create app

    let gate =
        new PostgresActorGate(source, syntheticCommitments witness.Identity) :> IActorGate

    let context =
        gate.Installation(
            principal,
            EndpointAction.ApproveRealDataActivation,
            CancellationToken.None
        )
        |> await
        |> Option.defaultWith (fun () -> failtest "Authenticated owner context is absent.")

    source, context

let private stagedAttempt owner (witness: WitnessProtocol) context request primaryCommitted =
    use connection = new NpgsqlConnection(owner)
    connection.Open()
    use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)

    let approvedAt =
        Sql.databaseNow connection transaction CancellationToken.None |> await

    let canonical =
        RealDataActivationApprovalCandidate.canonical
            request
            context.Binding.ActorId
            context.Binding.GrantRevision
            approvedAt

    try
        let intent =
            (witness
                .BeginAuthority(request.ApprovalId, canonical, None, CancellationToken.None)
                .GetAwaiter()
                .GetResult())

        if primaryCommitted then
            RealDataActivationApprovalRows.insert
                connection
                transaction
                request
                context.Binding.ActorId
                context.Binding.GrantRevision
                approvedAt
                canonical
                intent
            |> await

            transaction.Commit()

        intent, Array.copy canonical
    finally
        CryptographicOperations.ZeroMemory(canonical)

let private retainedCanonical owner approvalId =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT canonical_action FROM claimcore.installation_data_use_approvals "
            + "WHERE approval_id=@approval",
            connection
        )

    Sql.uuid command "approval" approvalId

    command.ExecuteScalar()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failtest "Retained approval canonical is absent.")
    :?> byte array

let private retryBoundary primaryCommitted owner app _writer (witness: WitnessProtocol) profile =
    let principal =
        human (
            if primaryCommitted then
                "activation-commit-owner"
            else
                "activation-intent-owner"
        )

    provision owner witness principal |> applied
    let planId, activationId, plan = publishSyntheticPlan owner witness profile

    let action =
        request
            planId
            activationId
            plan
            ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()))

    let dataSource, context = actorContext app witness principal
    use source = dataSource

    let unaligned =
        { action with
            ApprovalId = Guid.NewGuid()
            ExpiresAt = action.ExpiresAt.AddTicks(1L)
        }

    let before =
        (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence

    Expect.equal
        (RealDataActivationApproval.approve source witness context unaligned CancellationToken.None
         |> await)
        RealDataActivationApprovalOutcome.ResourceUnavailable
        "Sub-microsecond expiry cannot create unroundtrippable activation evidence."

    Expect.equal
        ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
        before
        "Invalid activation expiry creates no witness authority."

    let intent, originalCanonical =
        stagedAttempt owner witness context action primaryCommitted

    let pendingTip = (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult())

    Expect.equal pendingTip.TipSequence intent.Ticket.Sequence "Only an INTENT exists before retry."

    match
        RealDataActivationApproval.approve source witness context action CancellationToken.None
        |> await
    with
    | RealDataActivationApprovalOutcome.Approved(id, revision) when
        id = action.ApprovalId && revision = context.Binding.GrantRevision
        ->
        ()
    | _ -> failtest "Exact approval retry did not reconcile the original witnessed intent."

    Expect.equal
        (retainedCanonical owner action.ApprovalId)
        originalCanonical
        "Retry retained original DB-clock canonical bytes."

    Expect.equal
        ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
        (intent.Ticket.Sequence + 1L)
        "Retry adds only the missing settlement."

    use auditConnection = RuntimeDatabase.openConnection source
    DataAudit.run auditConnection witness CancellationToken.None |> await |> ignore

let tests =
    testList
        "real-data activation approval uncertainty"
        [
            testCase
                "[CC-BACKUP-001] intent-only owner approval retry preserves exact canonical and DB time"
                (fun _ -> withRealDataBootstrap (retryBoundary false))
            testCase
                "[CC-BACKUP-001] post-primary approval retry reconciles one witness settlement"
                (fun _ -> withRealDataBootstrap (retryBoundary true))
        ]
