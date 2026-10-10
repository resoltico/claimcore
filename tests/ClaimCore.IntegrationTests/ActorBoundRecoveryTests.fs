module ClaimCore.IntegrationTests.ActorBoundRecoveryTests

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport

let private grant source witness owner role =
    let registry = new ActorGrantRegistry(source, witness)
    let target = actorId (source) owner

    registry.SetGrant(
        owner,
        target,
        {
            Role = role
            Scope = GrantScope.Installation
        },
        true
    )
    |> await
    |> applied

let private requirePrepared (core: IActorClaimsCore) request =
    match core.Prepare(request, CancellationToken.None) |> await with
    | PrepareOutcome.Prepared(details, _) ->
        details.Summary.RequestSha256
        |> Option.defaultWith (fun () -> failtest "Retained preparation lacks its exact digest.")
    | PrepareOutcome.PrepareRejected _ -> failtest "Synthetic preparation was rejected."
    | PrepareOutcome.PrepareFailed _ -> failtest "Synthetic preparation failed."
    | _ -> failtest "Synthetic actor must retain one preparation."

let private revoke source witness owner role =
    let registry = new ActorGrantRegistry(source, witness)
    let id = actorId (source) owner

    registry.SetGrant(
        owner,
        id,
        {
            Role = role
            Scope = GrantScope.Installation
        },
        false
    )
    |> await
    |> applied

let private unavailable =
    function
    | RecoveryQueryOutcome.RecoveryRejected RecoveryRejection.ResourceUnavailable -> ()
    | RecoveryQueryOutcome.RecoveryRejected _ -> failtest "Recovery refusal was not uniform."
    | RecoveryQueryOutcome.RecoveryFailed _ -> failtest "Recovery denial became a fault."
    | RecoveryQueryOutcome.RecoveryCancelled ->
        failtest "Recovery denial was unexpectedly cancelled."
    | _ -> failtest "Recovery must return one non-disclosing refusal."

let private requireDismissed (core: IActorClaimsCore) operationId digest =
    match
        core.Recovery.Dismiss(operationId, digest, true, CancellationToken.None)
        |> await
    with
    | RecoveryDismissOutcome.DismissedPreparation _ -> ()
    | RecoveryDismissOutcome.DismissRefused _ -> failtest "Authorized dismissal was rejected."
    | RecoveryDismissOutcome.DismissFailed _ -> failtest "Authorized dismissal failed."
    | _ -> failtest "Authorized dismissal must record a definite revocation."

let private recoveryScope =
    testCase
        "[CC-AUTH-001] recovery inspection requires a current operation grant before payload read"
        (fun _ ->
            withAuthorityRuntimeDatabase (fun ownerConnection app writer witness ->
                let owner = human "recovery-owner"
                let stranger = human "recovery-stranger"
                provision ownerConnection witness owner |> applied
                use source = RuntimeDataSource.create app
                let registry = new ActorGrantRegistry(source, witness)
                registry.RegisterActor(owner, stranger) |> await |> applied
                grant source witness owner Role.CaseEditor

                use runtime =
                    Runtime.OpenPostgres(
                        app,
                        writer,
                        witnessKey (),
                        suppressionKeyFile (),
                        artifactKeyRingFile (),
                        CancellationToken.None
                    )
                    |> await
                    |> accepted

                let request =
                    openRequest (Guid.NewGuid()) ("RECOVERY-" + Guid.NewGuid().ToString("N"))

                let digest = requirePrepared (runtime.ForActor owner) request

                let inspect actor operationId =
                    (runtime.ForActor actor)
                        .Recovery.Inspect(operationId, None, 10, CancellationToken.None)
                    |> await

                inspect owner request.OperationId |> unavailable
                inspect stranger request.OperationId |> unavailable
                inspect owner (Guid.NewGuid()) |> unavailable

                grant source witness owner Role.RecoveryOperator

                match inspect owner request.OperationId with
                | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found _) -> ()
                | RecoveryQueryOutcome.RecoveryRejected _ ->
                    failtest "Recovery operator inspection was rejected."
                | RecoveryQueryOutcome.RecoveryFailed _ ->
                    failtest "Recovery operator inspection failed."
                | _ -> failtest "Recovery operator must inspect its scoped pending operation."

                requireDismissed (runtime.ForActor owner) request.OperationId digest

                revoke source witness owner Role.RecoveryOperator

                inspect owner request.OperationId |> unavailable))

let private assertUngrantedResolver (core: IActorClaimsCore) operationId digest =
    match core.Recovery.Resolve(operationId, digest, CancellationToken.None) |> await with
    | ResolveOutcome.RefusedBeforeAttempt(None, RecoveryRejection.ResourceUnavailable) -> ()
    | ResolveOutcome.RefusedBeforeAttempt _ ->
        failtest "Ungranted resolver refusal was not uniform."
    | ResolveOutcome.ResolveFailedBeforeAttempt _ ->
        failtest "Ungranted resolver denial became a fault."
    | _ -> failtest "An ungranted actor cannot resolve a retained operation."

let private assertAuthorizedResolver (core: IActorClaimsCore) operationId digest =
    match core.Recovery.Resolve(operationId, digest, CancellationToken.None) |> await with
    | ResolveOutcome.ResolveCompleted(_,
                                      _,
                                      DefiniteExecution.Accepted _,
                                      SettlementConfirmation.Confirmed) -> ()
    | ResolveOutcome.RefusedBeforeAttempt _ -> failtest "Authorized resolver was rejected."
    | ResolveOutcome.ResolveFailedBeforeAttempt _ -> failtest "Authorized resolver failed."
    | _ -> failtest "Authorized resolver must accept one definite synthetic operation."

let private resolveScope =
    testCase
        "[CC-AUTH-001] operation-bound recovery resolve rechecks grant before acceptance"
        (fun _ ->
            withAuthorityRuntimeDatabase (fun ownerConnection app writer witness ->
                let owner = human "recovery-resolver"
                let stranger = human "recovery-nongrantee"
                provision ownerConnection witness owner |> applied
                use source = RuntimeDataSource.create app
                let registry = new ActorGrantRegistry(source, witness)
                registry.RegisterActor(owner, stranger) |> await |> applied
                grant source witness owner Role.CaseEditor
                grant source witness owner Role.RecoveryOperator

                use runtime =
                    Runtime.OpenPostgres(
                        app,
                        writer,
                        witnessKey (),
                        suppressionKeyFile (),
                        artifactKeyRingFile (),
                        CancellationToken.None
                    )
                    |> await
                    |> accepted

                let request =
                    openRequest (Guid.NewGuid()) ("RESOLVE-" + Guid.NewGuid().ToString("N"))

                let digest = requirePrepared (runtime.ForActor owner) request

                assertUngrantedResolver (runtime.ForActor stranger) request.OperationId digest
                assertAuthorizedResolver (runtime.ForActor owner) request.OperationId digest))

let private requireAccepted (core: IActorClaimsCore) request =
    match core.Execute(request, CancellationToken.None) |> await with
    | SubmissionOutcome.Completed(_,
                                  _,
                                  DefiniteExecution.Accepted _,
                                  SettlementConfirmation.Confirmed) -> ()
    | _ -> failtest "Synthetic accepted case required for lifecycle gate check."

let private injectSyntheticPrivacyPhase ownerConnection reference =
    // Synthetic-only state injection isolates the gate; witnessed lifecycle is tested separately.
    use connection = new NpgsqlConnection(ownerConnection)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "UPDATE claimcore.cases SET privacy_phase='ERASURE_REQUESTED' WHERE case_reference=@reference",
            connection
        )

    command.Parameters.AddWithValue("reference", reference) |> ignore
    Expect.equal (command.ExecuteNonQuery()) 1 "One synthetic case changes state."

let private syntheticPrivacyFence =
    testCase "[CC-AUTH-001] nonactive case state closes claimant reads and exact replay" (fun _ ->
        withAuthorityRuntimeDatabase (fun ownerConnection app writer witness ->
            let principal = human "privacy-fence"
            provision ownerConnection witness principal |> applied
            use source = RuntimeDataSource.create app
            grant source witness principal Role.CaseEditor
            grant source witness principal Role.RecoveryOperator

            use runtime =
                Runtime.OpenPostgres(
                    app,
                    writer,
                    witnessKey (),
                    suppressionKeyFile (),
                    artifactKeyRingFile (),
                    CancellationToken.None
                )
                |> await
                |> accepted

            let core = runtime.ForActor principal

            let request =
                openRequest (Guid.NewGuid()) ("PRIVACY-" + Guid.NewGuid().ToString("N"))

            requireAccepted core request

            injectSyntheticPrivacyPhase ownerConnection request.CaseReference

            match core.Get(request.CaseReference, CancellationToken.None) |> await with
            | QueryOutcome.Rejected Rejection.ResourceUnavailable -> ()
            | _ -> failtest "Nonactive case must not disclose its projection."

            match core.ObserveOperation(request.OperationId, CancellationToken.None) |> await with
            | QueryOutcome.Rejected Rejection.ResourceUnavailable -> ()
            | _ -> failtest "Nonactive case must not disclose accepted history."

            core.Recovery.Inspect(request.OperationId, None, 10, CancellationToken.None)
            |> await
            |> unavailable

            match core.Execute(request, CancellationToken.None) |> await with
            | SubmissionOutcome.RejectedBeforeAttempt(None, Rejection.ResourceUnavailable) -> ()
            | _ -> failtest "A hidden accepted effect cannot be replayed while case is nonactive."

            match
                core.List({ AfterCursor = None; Limit = 10 }, CancellationToken.None) |> await
            with
            | QueryOutcome.Succeeded page ->
                Expect.isEmpty page.Items "Nonactive case is absent before list window."
            | _ -> failtest "Authorized list must remain available with an empty page."))

let tests =
    testList "actor-bound recovery" [ recoveryScope; resolveScope; syntheticPrivacyFence ]
