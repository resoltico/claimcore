module ClaimCore.IntegrationTests.RecoveryListAuthorityTests

open System
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures

let private setRole source witness owner target role active =
    let registry = new ActorGrantRegistry(source, witness)
    let id = actorId (source) target

    registry.SetGrant(
        owner,
        id,
        {
            Role = role
            Scope = GrantScope.Installation
        },
        active
    )
    |> await
    |> applied

let private requirePrepared (core: IActorClaimsCore) request =
    match core.Prepare(request, CancellationToken.None) |> await with
    | PrepareOutcome.Prepared _ -> ()
    | _ -> failtest "Synthetic case editor must retain exact pending work."

let private requireUnavailable outcome =
    match outcome with
    | RecoveryQueryOutcome.RecoveryRejected RecoveryRejection.ResourceUnavailable -> ()
    | _ -> failtest "Recovery cursor authority must fail without disclosure."

let private verifyCursorFences
    source
    witness
    owner
    other
    (runtime: Runtime)
    (core: IActorClaimsCore)
    token
    =
    (runtime.ForActor other)
        .Recovery.List(RecoveryListView.Pending, Some token, 1, CancellationToken.None)
    |> await
    |> requireUnavailable

    let expired =
        RecoveryCursorCodec.decode token
        |> Result.defaultWith failtest
        |> fun cursor ->
            { cursor with
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1.0)
            }
        |> RecoveryCursorCodec.encode

    core.Recovery.List(RecoveryListView.Pending, Some expired, 1, CancellationToken.None)
    |> await
    |> requireUnavailable

    setRole source witness owner owner Role.RecoveryOperator false

    core.Recovery.List(RecoveryListView.Pending, Some token, 1, CancellationToken.None)
    |> await
    |> requireUnavailable

let private cursorAuthority () =
    withAuthorityRuntimeDatabase (fun ownerConnection app writer witness ->
        let owner = human "list-owner"
        let other = human "list-other"
        provision ownerConnection witness owner |> applied
        use source = RuntimeDataSource.create app
        let registry = new ActorGrantRegistry(source, witness)
        registry.RegisterActor(owner, other) |> await |> applied
        setRole source witness owner owner Role.CaseEditor true
        setRole source witness owner owner Role.RecoveryOperator true
        setRole source witness owner other Role.RecoveryOperator true

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

        let core = runtime.ForActor owner

        for _ in 1..2 do
            requirePrepared
                core
                (openRequest (Guid.NewGuid()) ("LIST-" + Guid.NewGuid().ToString("N")))

        let token =
            match
                core.Recovery.List(RecoveryListView.Pending, None, 1, CancellationToken.None)
                |> await
            with
            | RecoveryQueryOutcome.RecoverySucceeded page when page.Items.Length = 1 ->
                page.NextCursor
                |> Option.defaultWith (fun () -> failtest "Next cursor is required.")
            | _ -> failtest "Installation recovery operator must see one bounded pending page."

        verifyCursorFences source witness owner other runtime core token)

let tests =
    testList
        "actor-bound recovery list"
        [
            testCase
                "[CC-AUTH-001] recovery cursors bind principal, grant and expiry"
                cursorAuthority
        ]
