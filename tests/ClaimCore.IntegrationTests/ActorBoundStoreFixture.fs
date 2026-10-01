module internal ClaimCore.IntegrationTests.ActorBoundStoreFixture

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open Expecto
open ClaimCore.Application
open ClaimCore.TestSupport
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.FixtureDatabase

let private principal =
    PrincipalKey.human "https://synthetic.example/realm" "shared-integration-editor"
    |> Result.defaultWith (fun _ -> failtest "Synthetic principal must be valid.")

let private applied =
    function
    | AuthorityWriteOutcome.Applied _ -> ()
    | _ -> failtest "Synthetic witnessed actor provision failed."

let private provision () =
    use owner = new NpgsqlConnection(adminConnection ())
    owner.Open()

    ActorGrantAdministration.provisionInitialOwner owner (witnessProtocol ()) principal
    |> await
    |> applied

    let source = dataSource ()
    let grants = new ActorGrantStore(source)

    let authority =
        (grants :> IActorGrantSource)
            .LoadForScope(principal, ResourceScope.Installation, CancellationToken.None)
        |> await
        |> Option.defaultWith (fun () -> failtest "Synthetic owner is missing.")

    let registry = new ActorGrantRegistry(source, witnessProtocol ())

    for role in [ Role.CaseEditor; Role.RecoveryOperator; Role.RecoveryExporter ] do
        let grant =
            {
                Role = role
                Scope = GrantScope.Installation
            }

        registry.SetGrant(principal, authority.ActorId, grant, true) |> await |> applied

let private ready = lazy (provision ())

let actorPrincipal () =
    ready.Force()
    principal

type internal Store() =
    let source = dataSource ()
    let witness = witnessProtocol ()

    let gate =
        new PostgresActorGate(source, FixturePrivateFiles.syntheticCommitments witness.Identity)
        :> IActorGate

    let invoke
        (context: Task<ActorCallContext option>)
        (call: IClaimStore -> Task<Result<'value, CoreFailure>>)
        =
        task {
            let! bound = context

            match bound with
            | None -> return Error CoreFailure.ResourceUnavailable
            | Some actor ->
                use adapter =
                    new PostgresStore(
                        source,
                        witness,
                        actor,
                        CaseListCursorTestSupport.protection,
                        CaseListCursorTestSupport.clock
                    )

                return! call (adapter :> IClaimStore)
        }

    interface ITestCommandExecutor with
        member _.Execute(operation, capture, decide) =
            task {
                let request = Operation.request operation

                let action =
                    match request.Command with
                    | Command.Open _ -> EndpointAction.ExecuteNewCase
                    | _ -> EndpointAction.ExecuteCommand

                let! context = gate.Command(principal, action, request, CancellationToken.None)

                match context with
                | None -> return Error CoreFailure.ResourceUnavailable
                | Some actor ->
                    return!
                        FixtureCommandExecution.execute
                            source
                            witness
                            actor
                            operation
                            capture
                            decide
            }

    interface IClaimStore with
        member _.Get(reference) =
            invoke
                (gate.Case(principal, EndpointAction.GetCase, reference, CancellationToken.None))
                (fun adapter -> adapter.Get(reference))

        member _.List(request) =
            invoke (gate.List(principal, CancellationToken.None)) (fun adapter ->
                adapter.List(request))

        member _.History(reference, afterVersion) =
            invoke
                (gate.Case(
                    principal,
                    EndpointAction.HistorySummary,
                    reference,
                    CancellationToken.None
                ))
                (fun adapter -> adapter.History(reference, afterVersion))

        member _.Operation(operationId) =
            invoke
                (gate.Operation(
                    principal,
                    EndpointAction.ObserveOperation,
                    operationId,
                    CancellationToken.None
                ))
                (fun adapter -> adapter.Operation(operationId))

        member _.Accepted(operationId, requestSha256) =
            invoke
                (gate.Operation(
                    principal,
                    EndpointAction.ObserveOperation,
                    operationId,
                    CancellationToken.None
                ))
                (fun adapter -> adapter.Accepted(operationId, requestSha256))

    interface IDisposable with
        member _.Dispose() = ()

let create () =
    ready.Force()
    new Store()
