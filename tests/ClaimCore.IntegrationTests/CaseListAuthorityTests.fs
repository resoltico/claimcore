module ClaimCore.IntegrationTests.CaseListAuthorityTests

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport

let private openRuntime app witnessWriter =
    Runtime.OpenPostgres(
        app,
        witnessWriter,
        witnessKey (),
        suppressionKeyFile (),
        artifactKeyRingFile (),
        CancellationToken.None
    )
    |> await
    |> accepted

let private grant
    (registry: ActorGrantRegistry)
    (source: ActorGrantStore)
    (owner: PrincipalKey)
    (target: PrincipalKey)
    (role: Role)
    (scope: GrantScope)
    active
    =
    registry.SetGrant(owner, actorId source target, { Role = role; Scope = scope }, active)
    |> await
    |> applied

let private openCase (core: IActorClaimsCore) reference =
    let input = openRequest (Guid.NewGuid()) reference

    match core.Execute(input, CancellationToken.None) |> await with
    | SubmissionOutcome.Completed(_,
                                  _,
                                  DefiniteExecution.Accepted _,
                                  SettlementConfirmation.Confirmed) -> ()
    | _ -> failtest "Synthetic case opening must settle before paging."

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

let private page (core: IActorClaimsCore) cursor limit =
    match
        core.List({ AfterCursor = cursor; Limit = limit }, CancellationToken.None)
        |> await
    with
    | QueryOutcome.Succeeded value -> value
    | _ -> failtest "Expected an authorized case-list page."

let private invalidCursor (core: IActorClaimsCore) cursor limit =
    match
        core.List(
            {
                AfterCursor = Some cursor
                Limit = limit
            },
            CancellationToken.None
        )
        |> await
    with
    | QueryOutcome.Rejected Rejection.InvalidCaseListCursor -> ()
    | _ -> failtest "One typed cursor refusal covers invalid or stale authority."

let private scopedListContinuation () =
    withAuthorityRuntimeDatabase (fun owner app witnessWriter witness ->
        let steward = human "cursor-steward"
        let reader = human "cursor-reader"
        let other = human "cursor-other"
        provision owner witness steward |> applied
        use source = RuntimeDataSource.create app
        let actors = new ActorGrantStore(source)
        let registry = new ActorGrantRegistry(source, witness)
        registry.RegisterActor(steward, reader) |> await |> applied
        registry.RegisterActor(steward, other) |> await |> applied
        grant registry actors steward steward Role.CaseEditor GrantScope.Installation true
        use runtime = openRuntime app witnessWriter
        let references = [ "CURSOR-001"; "CURSOR-002"; "CURSOR-003" ]
        references |> List.iter (openCase (runtime.ForActor steward))
        let firstGrant = GrantScope.Case(storedCaseId owner references[0])
        let lastGrant = GrantScope.Case(storedCaseId owner references[2])
        grant registry actors steward reader Role.CaseReader firstGrant true
        grant registry actors steward reader Role.CaseReader lastGrant true
        grant registry actors steward other Role.CaseReader GrantScope.Installation true
        let scoped = runtime.ForActor reader
        let first = page scoped None 1

        Expect.equal
            (first.Items |> List.map _.CaseReference)
            [ references[0] ]
            "First visible key"

        let cursor =
            first.NextCursor
            |> Option.defaultWith (fun () -> failtest "Continuation exists.")

        Expect.isFalse
            (cursor.Contains(references[0], StringComparison.Ordinal))
            "No reference plaintext"

        invalidCursor (runtime.ForActor other) cursor 1
        invalidCursor scoped cursor 2
        invalidCursor scoped (cursor + "=") 1
        let second = page scoped (Some cursor) 1

        Expect.equal
            (second.Items |> List.map _.CaseReference)
            [ references[2] ]
            "Hidden middle row did not consume the window"

        Expect.isNone second.NextCursor "Only two scoped rows exist"
        grant registry actors steward reader Role.CaseReader lastGrant false
        invalidCursor scoped cursor 1)

let tests =
    testCase
        "[CC-AUTH-001] case-list cursor binds principal grant query and visible page"
        scopedListContinuation
