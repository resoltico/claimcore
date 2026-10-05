module ClaimCore.IntegrationTests.ActorGrantLookupTests

open System
open System.Data
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.TestSupport
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport

let private storedCaseId owner reference =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT case_id FROM claimcore.cases WHERE case_reference=@reference",
            connection
        )

    command.Parameters.AddWithValue("reference", reference) |> ignore
    command.ExecuteScalar() :?> Guid

let private equalUnavailable actual =
    Expect.equal
        actual
        AuthorizationDecision.Unavailable
        "Inaccessible and nonexistent identities use one refusal."

let private assertDenied (grants: ActorGrantStore) reader (input: CommandRequest) =
    for reference in [ input.CaseReference; "MISSING-" + Guid.NewGuid().ToString("N") ] do
        grants.AuthorizeCaseReference(
            reader,
            EndpointAction.GetCase,
            reference,
            CancellationToken.None
        )
        |> await
        |> equalUnavailable

    for operationId in [ input.OperationId; Guid.NewGuid() ] do
        grants.AuthorizeAcceptedOperation(
            reader,
            EndpointAction.ObserveOperation,
            operationId,
            CancellationToken.None
        )
        |> await
        |> equalUnavailable

let private assertGranted (grants: ActorGrantStore) reader (input: CommandRequest) =
    match
        grants.AuthorizeCaseReference(
            reader,
            EndpointAction.GetCase,
            input.CaseReference,
            CancellationToken.None
        )
        |> await
    with
    | AuthorizationDecision.Available _ -> ()
    | _ -> failtest "Explicit case read grant should authorize the existing case."

    match
        grants.AuthorizeAcceptedOperation(
            reader,
            EndpointAction.ObserveOperation,
            input.OperationId,
            CancellationToken.None
        )
        |> await
    with
    | AuthorizationDecision.Available _ -> ()
    | _ -> failtest "Exact accepted operation resolves to the granted case."

let private assertCaseWitness (witness: WitnessProtocol) eventId caseId =
    for phase in [ Intent; SettledAuthority ] do
        let ticket =
            (witness.EvidenceStore
                .TryReadEvidence(eventId, phase, CancellationToken.None)
                .GetAwaiter()
                .GetResult())
            |> Option.defaultWith (fun () -> failtest "Case grant witness phase is absent.")
            |> _.Ticket

        Expect.equal ticket.ScopeKind Case "Grant witness scope is CASE."
        Expect.equal ticket.SubjectCaseId (Some caseId) "Settlement retains exact case."

let private acceptedOpen (source: NpgsqlDataSource) (witness: WitnessProtocol) first input =
    let gate =
        new PostgresActorGate(source, FixturePrivateFiles.syntheticCommitments witness.Identity)
        :> IActorGate

    let context =
        gate.Command(first, EndpointAction.ExecuteNewCase, input, CancellationToken.None)
        |> await
        |> Option.defaultWith (fun () -> failtest "Synthetic editor was not admitted.")

    FixtureCommandExecution.executeRequest source witness context clock input
    |> await
    |> accepted
    |> ignore

let private exerciseLookups owner app witness =
    let first = human "lookup-owner"
    let reader = human "lookup-reader"
    provision owner witness first |> applied
    use source = RuntimeDataSource.create app
    let registry = new ActorGrantRegistry(source, witness)
    registry.RegisterActor(first, reader) |> await |> applied
    let grants = new ActorGrantStore(source)

    registry.SetGrant(
        first,
        actorId grants first,
        {
            Role = Role.CaseEditor
            Scope = GrantScope.Installation
        },
        true
    )
    |> await
    |> applied

    let input = openRequest (Guid.NewGuid()) ("AUTH-" + Guid.NewGuid().ToString("N"))

    acceptedOpen source witness first input

    assertDenied grants reader input

    let grant =
        {
            Role = Role.CaseReader
            Scope = GrantScope.Case(storedCaseId owner input.CaseReference)
        }

    let grantOutcome =
        registry.SetGrant(first, actorId grants reader, grant, true) |> await

    grantOutcome |> applied

    let caseId = storedCaseId owner input.CaseReference

    match grantOutcome with
    | AuthorityWriteOutcome.Applied(eventId, _) -> assertCaseWitness witness eventId caseId
    | _ -> failtest "Case grant did not settle."

    assertGranted grants reader input

    let revokeOutcome =
        registry.SetGrant(first, actorId grants reader, grant, false) |> await

    revokeOutcome |> applied

    match revokeOutcome with
    | AuthorityWriteOutcome.Applied(eventId, _) -> assertCaseWitness witness eventId caseId
    | _ -> failtest "Case revocation did not settle."

    assertDenied grants reader input

let private noExistenceLeak =
    testCase
        "[CC-AUTH-001] case and accepted-operation lookups do not reveal inaccessible identities"
        (fun _ -> withAuthorityDatabase exerciseLookups)

let private lockExclusion =
    testCase "[CC-AUTH-001] authority lock excludes a concurrent grant change" (fun _ ->
        withAuthorityDatabase (fun owner app witness ->
            provision owner witness (human "lock-owner") |> applied
            use first = new NpgsqlConnection(app)
            use second = new NpgsqlConnection(app)
            first.Open()
            second.Open()
            use transaction = first.BeginTransaction(IsolationLevel.ReadCommitted)

            ActorGrantRead.lockRevision first transaction true CancellationToken.None
            |> await
            |> ignore

            use conflict =
                new NpgsqlCommand(
                    "SELECT revision FROM claimcore.authority_tip WHERE singleton FOR UPDATE NOWAIT",
                    second
                )

            Expect.throwsT<PostgresException>
                (fun () -> conflict.ExecuteScalar() |> ignore)
                "A concurrent authority update cannot pass the held revision lock."

            transaction.Commit()
            Expect.isNotNull (conflict.ExecuteScalar()) "Lock is released after commit."))

let tests =
    testList "actor/grant resource lookup" [ noExistenceLeak; lockExclusion ]
