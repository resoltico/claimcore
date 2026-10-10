module internal ClaimCore.IntegrationTests.RecoveryAuthorityInterleaveFixture

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport

[<NoEquality; NoComparison>]
type Fixture =
    {
        OwnerCore: IActorClaimsCore
        Owner: PrincipalKey
        Outsider: PrincipalKey
        Input: CommandRequest
        Digest: string
        Captured: ActorCallContext
        ApplicationConnection: string
        Witness: WitnessProtocol
        Gate: IActorGate
        CountAccepted: unit -> int64
        CommitStartWithoutSettlement: unit -> Guid
        Controlled:
            (Result<RecoveryStart, RecoveryStoreFailure>
                    -> Task<Result<RecoveryStart, RecoveryStoreFailure>>)
                -> IClaimsCore
    }

let ct = CancellationToken.None

/// Test-only interleave after actual admission; ordinary clients have no port or hook.
type private InterleavedRecovery
    (
        inner: IRecoveryStore,
        started:
            Result<RecoveryStart, RecoveryStoreFailure>
                -> Task<Result<RecoveryStart, RecoveryStoreFailure>>
    ) =
    interface IRecoveryStore with
        member _.InstallationLineage token = inner.InstallationLineage token
        member _.Retain(value, token) = inner.Retain(value, token)
        member _.Get(id, token) = inner.Get(id, token)
        member _.Inspect(id, cursor, limit, token) = inner.Inspect(id, cursor, limit, token)
        member _.List(view, cursor, limit, token) = inner.List(view, cursor, limit, token)

        member _.Start(id, token) =
            task {
                let! value = inner.Start(id, token)
                return! started value
            }

        member _.Settle(id, outcome, token) = inner.Settle(id, outcome, token)

        member _.ExecuteAdmitted(operation, attempt, clock, decide, token) =
            inner.ExecuteAdmitted(operation, attempt, clock, decide, token)

        member _.Dismiss(id, digest, token) = inner.Dismiss(id, digest, token)

let appliedActor =
    function
    | ActorManagementOutcome.Applied(_, revision, _) -> revision
    | _ -> failtest "Controlled authenticated authority event must be confirmed."

let private initializeActors ownerConnection source witness =
    let owner = human "interleave-owner"
    provision ownerConnection witness owner |> applied
    let registry = new ActorGrantRegistry(source, witness)
    let ownerId = actorId (source) owner

    for role in [ Role.CaseEditor; Role.RecoveryOperator ] do
        registry.SetGrant(
            owner,
            ownerId,
            {
                Role = role
                Scope = GrantScope.Installation
            },
            true
        )
        |> await
        |> applied

    owner

let private preparation (core: IActorClaimsCore) =
    let opened = openRequest (Guid.NewGuid()) "AUTHORITY-INTERLEAVE"

    match core.Execute(opened, ct) |> await with
    | SubmissionOutcome.Completed(_,
                                  _,
                                  DefiniteExecution.Accepted _,
                                  SettlementConfirmation.Confirmed) -> ()
    | _ -> failtest "Controlled case seed must be accepted."

    let input =
        { opened with
            OperationId = Guid.NewGuid()
            ExpectedVersion = 1L
            Command = Command.Close
        }

    let digest =
        match core.Prepare(input, ct) |> await with
        | PrepareOutcome.Prepared(details, _) ->
            details.Summary.RequestSha256
            |> Option.defaultWith (fun () -> failtest "Exact digest is required.")
        | _ -> failtest "Controlled CLOSE must be retained."

    input, digest

let private artifactAuthority =
    { new IRecoveryArtifactAuthority with
        member _.Sign(_, _) =
            failtest "This fixture does not exercise artifact custody."

        member _.Verify(_, _) =
            failtest "This fixture does not exercise artifact custody."
    }

let private boundContext source (witness: WitnessProtocol) owner operationId =
    let gate =
        new PostgresActorGate(source, FixturePrivateFiles.syntheticCommitments witness.Identity)
        :> IActorGate

    let context =
        gate.Operation(owner, EndpointAction.RecoveryResolve, operationId, ct)
        |> await
        |> Option.defaultWith (fun () -> failtest "Retained operation must be admitted.")

    gate, context

let private commitStartWithoutSettlement ownerConnection writer source context operationId () =
    let support = TechnicalWitnessTestSupport.protocol

    use faulty =
        support ownerConnection writer (fun () -> raise (TimeoutException("synthetic")))

    let recovery = TechnicalWitnessTestSupport.recovery source faulty context

    match recovery.Start(operationId, ct) |> await with
    | Error RecoveryStoreFailure.TechnicalMutationUnknown -> ()
    | _ -> failtest "Missing independent START settlement must remain unknown."

    let attemptId = WitnessEventIdentity.startEventId operationId 1L

    Expect.equal
        (TechnicalWitnessTestSupport.rowCount
            ownerConnection
            "request_submission_attempts"
            operationId)
        1L
        "The primary committed one real attempt"

    Expect.equal
        (TechnicalWitnessTestSupport.witnessCount
            writer
            (TechnicalWitnessTestSupport.identity ownerConnection)
            attemptId)
        1
        "The independent witness has only START intent"

    attemptId

let private openRuntime app writer =
    Runtime.OpenPostgres(
        app,
        writer,
        witnessKey (),
        suppressionKeyFile (),
        artifactKeyRingFile (),
        ct
    )
    |> await
    |> accepted

let withPreparation action =
    withAuthorityRuntimeDatabase (fun ownerConnection app writer witness ->
        use source = RuntimeDataSource.create app
        let owner = initializeActors ownerConnection source witness

        use runtime = openRuntime app writer

        let core = runtime.ForActor owner
        let outsider = human "unrelated-interleave-actor"

        core.Management.RegisterActor(Guid.NewGuid(), outsider, ct)
        |> await
        |> appliedActor
        |> ignore

        let input, digest = preparation core
        let gate, context = boundContext source witness owner input.OperationId

        use claims =
            new PostgresStore(source, witness, context, CaseListCursorTestSupport.protection)

        let recovery =
            new PostgresRecoveryStore(source, PreparationLimits.defaults, witness, context)
            :> IRecoveryStore

        let controlled started =
            CoreApi.createActor
                (claims :> IClaimStore)
                (new InterleavedRecovery(recovery, started) :> IRecoveryStore)
                clock
                context
                artifactAuthority

        action
            {
                OwnerCore = core
                Owner = owner
                Outsider = outsider
                Input = input
                Digest = digest
                Captured = context
                ApplicationConnection = app
                Witness = witness
                Gate = gate
                CountAccepted =
                    fun () ->
                        TechnicalWitnessTestSupport.rowCount
                            ownerConnection
                            "case_changes"
                            input.OperationId
                CommitStartWithoutSettlement =
                    commitStartWithoutSettlement
                        ownerConnection
                        writer
                        source
                        context
                        input.OperationId
                Controlled = controlled
            })
