module ClaimCore.IntegrationTests.ContextObservationTests

open System
open System.Globalization
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "%A" other

let private active (reply: ContextReply) =
    match reply.Requester with
    | RequesterState.Active value -> value
    | _ -> failtest "Expected active context"

let private principal subject =
    PrincipalKey.human "https://observation.example/" subject
    |> Result.defaultWith (string >> failtest)

let private basis (observed: ObservedTransactions.Store) =
    {
        WorkDateBasis.ObservedAt = observed.ObservedAt
        BusinessDate = DateOnly.FromDateTime observed.ObservedAt.UtcDateTime
        TimeZoneId = "Etc/UTC"
    }

let private instant (value: DateTimeOffset) =
    value
        .ToUniversalTime()
        .ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture)

[<Tests>]
let tests =
    testCaseAsync
        "active context exposes its database snapshot while limited states stay limited"
        (async {
            let! database = PostgresFixture.create () |> Async.AwaitTask

            try
                let! admin = bootstrap database.Fixture |> Async.AwaitTask

                let activePrincipal, roleless, disabled =
                    principal "active", principal "no-roles", principal "disabled"

                for number, target in [ 100, activePrincipal; 102, roleless; 103, disabled ] do
                    let! reply =
                        Service.execute
                            admin
                            (OperationInput.Authority(
                                authorityRequest
                                    (id number)
                                    target
                                    (AuthorityAct.Register Inputs.actorProfile)
                            ))
                            token
                        |> Async.AwaitTask

                    acceptedAuthority reply

                for number, target in [ 101, activePrincipal; 104, disabled ] do
                    let! reply =
                        grant
                            admin
                            (id number)
                            target
                            {
                                Role = Role.CaseReader
                                Scope = GrantTarget.Installation
                            }
                            true
                        |> Async.AwaitTask

                    acceptedAuthority reply

                let! reply =
                    Service.execute
                        admin
                        (OperationInput.Authority(
                            authorityRequest (id 105) disabled AuthorityAct.Disable
                        ))
                        token
                    |> Async.AwaitTask

                acceptedAuthority reply
                let observed = ObservedTransactions.Store(database.Fixture.Store)

                let fixture =
                    { database.Fixture with
                        Store = observed
                    }

                for _ in 1..2 do
                    let! reply =
                        Service.context (core fixture activePrincipal) () token |> Async.AwaitTask

                    let reply = read reply
                    let current = active reply
                    let expected = basis observed

                    Expect.equal
                        current.Roles
                        [
                            {
                                Role = Role.CaseReader
                                Installation = true
                                CaseCount = 0L
                            }
                        ]
                        "Own summaries retain actual assignments"

                    Expect.equal
                        current.Runtime.Basis
                        expected
                        "Context uses the provider's current snapshot"

                    let wire =
                        ResponseCodecs.contextReply.Write reply
                        |> JsonValue.tryMember "requester"
                        |> Option.get

                    Expect.equal
                        (JsonValue.tryMember "observedAt" wire)
                        (Some(JsonValue.String(instant expected.ObservedAt)))
                        "The active wire shape carries the database instant"

                    Expect.equal
                        (JsonValue.tryMember "businessDate" wire)
                        (Some(
                            JsonValue.String(
                                expected.BusinessDate.ToString(
                                    "yyyy-MM-dd",
                                    CultureInfo.InvariantCulture
                                )
                            )
                        ))
                        "Business date stays flat"

                    Expect.equal
                        (JsonValue.tryMember "basis" wire)
                        None
                        "No additional active-context wrapper is invented"

                for target in [ roleless; disabled; principal "not-registered" ] do
                    let! reply = Service.context (core fixture target) () token |> Async.AwaitTask

                    let wire =
                        ResponseCodecs.contextReply.Write(read reply)
                        |> JsonValue.tryMember "requester"
                        |> Option.get

                    Expect.equal
                        (JsonValue.tryMember "observedAt" wire)
                        None
                        "Limited states do not disclose active observation metadata"

                    Expect.equal
                        (JsonValue.tryMember "productVersion" wire)
                        None
                        "Runtime details stay within the active state"
            finally
                database.Fixture.Dispose()
        })

[<Tests>]
let authorityPageBasis =
    testCaseAsync
        "authority pages carry their database transaction basis without a client clock"
        (async {
            let! database = PostgresFixture.create () |> Async.AwaitTask

            try
                let! _ = bootstrap database.Fixture |> Async.AwaitTask
                let observed = ObservedTransactions.Store(database.Fixture.Store)

                let fixture =
                    { database.Fixture with
                        Store = observed
                    }

                let service = core fixture fixture.Owner
                let! context = Service.context service () token |> Async.AwaitTask
                let owner = (read context |> active).ActorId

                let check actual =
                    Expect.equal actual (basis observed) "Each reply uses its own provider snapshot"

                for _ in 1..2 do
                    let! actors =
                        Service.actors
                            service
                            {
                                Role = None
                                Text = None
                                AfterCursor = None
                                Limit = 50
                            }
                            token
                        |> Async.AwaitTask

                    check (read actors).Basis

                    let! grants =
                        Service.grants
                            service
                            {
                                ActorId = ActorId.value owner
                                AfterCursor = None
                                Limit = 50
                            }
                            token
                        |> Async.AwaitTask

                    check (read grants).Basis

                    let! history =
                        Service.authorityHistory
                            service
                            {
                                TargetActorId = None
                                AfterCursor = None
                                Limit = 50
                            }
                            token
                        |> Async.AwaitTask

                    check (read history).Basis

                    let! requests =
                        Service.accessRequests service { AfterCursor = None; Limit = 50 } token
                        |> Async.AwaitTask

                    check (read requests).Basis

                    let! preview =
                        Service.preview
                            service
                            (OperationInput.Authority(
                                authorityRequest
                                    (id 120)
                                    fixture.Owner
                                    (AuthorityAct.SetProfile
                                        {
                                            DisplayName = "Basis profile"
                                            Contact = None
                                        })
                            ))
                            token
                        |> Async.AwaitTask

                    match preview.Outcome with
                    | Outcome.Succeeded(Previewed.Reviewed(OperationReview.Authority review)) ->
                        check review.Basis
                    | other -> failtestf "%A" other
            finally
                database.Fixture.Dispose()
        })
