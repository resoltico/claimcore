module ClaimCore.IntegrationTests.RevocationIntentIdentityTests

open System
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.TechnicalWitnessTestSupport

let private enableRecovery source witness principal =
    let grants = new ActorGrantStore(source)
    let registry = new ActorGrantRegistry(source, witness)

    registry.SetGrant(
        principal,
        ActorGrantTestSupport.actorId grants principal,
        {
            Role = Role.RecoveryOperator
            Scope = GrantScope.Installation
        },
        true
    )
    |> await
    |> ActorGrantTestSupport.applied

let private admit source witness principal request =
    let preparer =
        actorContext source witness principal EndpointAction.PrepareNewCase request

    let material = draft request preparer

    (recovery source witness preparer).Retain(material, cancellation)
    |> await
    |> accepted
    |> ignore

    let submitter =
        actorContext source witness principal EndpointAction.ExecuteNewCase request

    let attempt =
        match
            (recovery source witness submitter).Start(request.OperationId, cancellation)
            |> await
        with
        | Ok(RecoveryStart.Started(id, _)) -> id
        | _ -> failtest "Attempt must be admitted before simulating the crash."

    material, submitter, attempt

let private orphan
    witness
    operation
    (material: RecoveryPreparationDraft)
    (submitter: ActorCallContext)
    =
    let request = Operation.request operation

    let claim =
        Claim.decide (clock.Capture().EffectiveBusinessDate) request None |> accepted

    let evidence: ExecutionAttribution =
        {
            Command =
                {
                    Actor = submitter.Binding
                    CaseId = material.CaseId
                }
            PreparerActorId = material.PreparerActorId
            ImporterActorId = None
            Phase = AttemptActorPhase.NormalSubmit
        }

    WitnessAcceptedProtocol.beginAccepted
        witness
        operation
        (clock.Capture())
        material.CaseId
        evidence
        claim

let private dismiss
    source
    (witness: WitnessProtocol)
    principal
    (request: CommandRequest)
    (material: RecoveryPreparationDraft)
    =
    let gate =
        new PostgresActorGate(source, FixturePrivateFiles.syntheticCommitments witness.Identity)
        :> IActorGate

    let context =
        gate.Operation(principal, EndpointAction.RecoveryDismiss, request.OperationId, cancellation)
        |> await
        |> Option.defaultWith (fun () -> failtest "Revocation must be authorised.")

    match
        (recovery source witness context)
            .Dismiss(request.OperationId, material.RequestSha256, cancellation)
        |> await
    with
    | Ok(RecoveryDismissal.Dismissed _) -> ()
    | _ -> failtest "A separate revocation event must close future authority."

let private remainsUnsettled owner attempt =
    use connection = new Npgsql.NpgsqlConnection(owner)
    connection.Open()

    use query =
        new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM claimcore.request_submission_settlements WHERE attempt_id=@attempt",
            connection
        )

    Sql.uuid query "attempt" attempt
    Expect.equal (query.ExecuteScalar() :?> int64) 0L "The crash remains historically unresolved"

let tests =
    testCase
        "[CC-REC-001] [CC-WIT-001] revocation fences future authority without rewriting an orphan acceptance"
        (fun () ->
            setup (fun owner source _ witness principal ->
                enableRecovery source witness principal
                let request = newRequest ()
                let material, submitter, attempt = admit source witness principal request
                let operation = Operation.prepare request |> accepted
                let original = orphan witness operation material submitter
                dismiss source witness principal request material

                Expect.equal
                    (rowCount owner "case_changes" request.OperationId)
                    0L
                    "No invented primary acceptance"

                let retained =
                    witness.EvidenceStore.TryReadEvidence(request.OperationId, Intent)
                    |> Option.defaultWith (fun () ->
                        failtest "Original acceptance intent survives.")

                Expect.equal
                    retained.Ticket.Sequence
                    original.Ticket.Sequence
                    "Original intent remains unchanged"

                let decide date current = Claim.decide date request current

                match
                    (recovery source witness submitter)
                        .ExecuteAdmitted(operation, attempt, clock.Capture, decide, cancellation)
                    |> await
                with
                | Ok(AdmittedExecution.RevokedBeforeExecution SettlementConfirmation.Unconfirmed) ->
                    ()
                | _ ->
                    failtest
                        "Future execution is fenced without rewriting past attempt knowledge."

                remainsUnsettled owner attempt))
