module ClaimCore.IntegrationTests.AuthorityContinuationTests

open ClaimCore.TestSupport
open ClaimCore.Protocol

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.ApplicationFixtures
open ClaimCore.IntegrationTests.PaginationScenarios


let private authorityLimits (_: PostgresFixture.Database) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture

        do! registerActors actor

        let! first =
            (Service.actors actor ({ AfterCursor = None; Limit = 1 }) (CancellationToken.None))

        let first = succeeded first

        Expect.equal
            (first.Items |> List.map (fun actor -> ActorId.value actor.ActorId))
            [ id 1 ]
            "Stable actor-ID order"

        let! second =
            (Service.actors
                actor
                ({
                    AfterCursor = first.NextCursor
                    Limit = 2
                })
                (CancellationToken.None))

        let second = succeeded second

        Expect.equal
            (second.Items |> List.map (fun actor -> ActorId.value actor.ActorId))
            [ id 10; id 11 ]
            "Changed page size"

        let! third =
            (Service.actors
                actor
                ({
                    AfterCursor = second.NextCursor
                    Limit = 1
                })
                (CancellationToken.None))

        match third with
        | Outcome.Succeeded page ->
            Expect.equal
                (page.Items |> List.map (fun actor -> ActorId.value actor.ActorId))
                [ id 12 ]
                "No omission"

            Expect.isNone page.NextCursor "End of review"
        | _ -> failtest "Missing final review page"

        let! wrongEndpoint =
            (Service.list
                actor
                ((ClaimCore.TestSupport.Inputs.caseListPage (
                    {
                        AfterCursor = first.NextCursor
                        Limit = 1
                    }
                )))
                (CancellationToken.None))

        Expect.equal
            wrongEndpoint
            (Outcome.Rejected Refusal.InvalidCursor)
            "Authority continuation is endpoint-bound even when its position is valid ASCII"
    }

let private grantCursorRejections (_: PostgresFixture.Database) (fixture: Fixture) =
    task {
        let! admin = caseWorker fixture
        let! view = inspectAuthority fixture (owner) (CancellationToken.None)
        let view = view |> Result.defaultWith (fun _ -> failtest "Caller authority")
        let authority = view.Actor |> Option.get

        for position in invalidGrantPositions do
            let cursor =
                let json =
                    $"{{\"actor\":\"{authority.ActorId:D}\",\"after\":{position},\"kind\":\"GRANTS\",\"v\":1}}"

                Convert
                    .ToBase64String(System.Text.Encoding.UTF8.GetBytes json)
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_')

            let! refused =
                (Service.grants
                    admin
                    ({
                        ActorId = ActorId.value authority.ActorId
                        AfterCursor = Some cursor
                        Limit = 50
                    })
                    (CancellationToken.None))

            Expect.equal
                refused
                (Outcome.Rejected Refusal.InvalidCursor)
                "A plain envelope does not admit an invalid grant position"

        let! invalidLimit =
            (Service.grants
                admin
                ({
                    ActorId = ActorId.value authority.ActorId
                    AfterCursor = None
                    Limit = 51
                })
                (CancellationToken.None))

        Expect.equal
            invalidLimit
            (Outcome.Rejected(Refusal.invalidInput (InputTarget.Limit, Violation.PageLimitRange 50)))
            "Public grant page is bounded"
    }

[<Tests>]
let tests =
    testList
        "authorized continuation pages"
        [
            testCaseAsync
                "plain grant positions retain primitive and semantic admission"
                (run grantCursorRejections)
            testCaseAsync
                "authority continuation pages actors once and cannot cross endpoints"
                (run authorityLimits)
        ]
