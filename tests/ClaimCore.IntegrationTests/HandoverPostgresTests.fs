module ClaimCore.IntegrationTests.HandoverPostgresTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected read: %A" other

let private work fixture number reference revision responsible summary =
    OperationInput.Work
        {
            OperationId = id fixture number
            Target = CaseSelector.Reference reference
            ExpectedRevision = revision
            Act =
                WorkAct.UpdateWork
                    {
                        Responsible = responsible
                        Summary = summary
                    }
        }

let private handover (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! owner = populated database

        let principal =
            PrincipalKey.human "https://issuer.example/" "outgoing-handler"
            |> Result.defaultWith (string >> invalidOp)

        let! registered =
            Service.execute
                owner
                (OperationInput.People(
                    Inputs.peopleRequest
                        (id fixture 100)
                        principal
                        (PeopleAct.Register Inputs.actorProfile)
                ))
                token

        acceptedPeople registered
        let outgoing = CaseFixtures.actorId (id fixture 100)
        let incoming = CaseFixtures.actorId (id fixture 1)

        let! granted =
            grant
                owner
                (id fixture 101)
                principal
                {
                    Role = Role.CaseHandler
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople granted

        let! second =
            Service.execute
                owner
                (Inputs.caseOperation (
                    request fixture 102 "synthetic-b" 0L (AuthoredCaseAct.Open Inputs.registration)
                ))
                token

        accepted second |> ignore

        for number, reference in [ 110, "synthetic-a"; 111, "synthetic-b" ] do
            let! updated =
                Service.execute
                    owner
                    (work
                        fixture
                        number
                        reference
                        0L
                        (WorkValue.Set(ActorId.format outgoing))
                        WorkValue.Keep)
                    token

            accepted updated |> ignore

        let! closedCase =
            Service.execute
                owner
                (Inputs.caseOperation (
                    request
                        fixture
                        112
                        "synthetic-closed"
                        0L
                        (AuthoredCaseAct.Open Inputs.registration)
                ))
                token

        accepted closedCase |> ignore

        let! closedAssigned =
            Service.execute
                owner
                (work
                    fixture
                    113
                    "synthetic-closed"
                    0L
                    (WorkValue.Set(ActorId.format outgoing))
                    WorkValue.Keep)
                token

        accepted closedAssigned |> ignore

        let! closed =
            Service.execute
                owner
                (Inputs.caseOperation (
                    request fixture 114 "synthetic-closed" 1L (AuthoredCaseAct.Bare BareAct.Close)
                ))
                token

        accepted closed |> ignore

        let! beforeGrants =
            Service.grants
                owner
                {
                    ActorId = ActorId.value outgoing
                    AfterCursor = None
                    Limit = 50
                }
                token

        let offered = (read beforeGrants).Actor.ResponsibleCases |> Option.get
        Expect.equal offered.Count 2L "The person count excludes closed responsibilities"

        let filter =
            { CaseListFilter.activeOpen<string> with
                ResponsibleActor = Some outgoing
            }

        let list () =
            Service.list
                owner
                {
                    Page = { AfterCursor = None; Limit = 50 }
                    Filter = filter
                }
                token

        let! rows = list ()
        let rows = (read rows).Items
        Expect.equal rows.Length 2 "Only outgoing responsibility"

        Expect.isTrue
            (rows
             |> List.forall (fun row -> row.WorkRevision = Revision.first && row.CanAssign))
            "Rows retain their independent work revision and an authorized offer"

        Expect.isTrue
            (rows |> List.forall (fun row -> row.Revision = Revision.first))
            "Work did not change fact revisions"

        let! changed =
            Service.execute
                owner
                (work
                    fixture
                    120
                    "synthetic-a"
                    1L
                    WorkValue.Keep
                    (WorkValue.Set "Concurrent summary"))
                token

        accepted changed |> ignore

        let stale =
            work
                fixture
                121
                "synthetic-a"
                (Revision.value rows.Head.WorkRevision)
                (WorkValue.Set(ActorId.format incoming))
                WorkValue.Keep

        let! preview = Service.preview owner stale token

        Expect.isTrue
            (match preview.Outcome with
             | Outcome.Rejected(Refusal.RevisionConflict info) ->
                 info.ActualRevision = CaseFixtures.revision 2L
             | _ -> false)
            "Review conflicts on the selected row revision"

        let! staleWrite = Service.execute owner stale token

        Expect.isTrue
            (match staleWrite.Outcome with
             | ExecuteResult.Rejected(Refusal.RevisionConflict info) ->
                 info.ActualRevision = CaseFixtures.revision 2L
             | _ -> false)
            "Execution cannot overwrite concurrent work"

        for number, reference, revision in [ 122, "synthetic-a", 2L; 123, "synthetic-b", 1L ] do
            let input =
                work
                    fixture
                    number
                    reference
                    revision
                    (WorkValue.Set(ActorId.format incoming))
                    WorkValue.Keep

            let! review = Service.preview owner input token

            Expect.isTrue
                (match review.Outcome with
                 | Outcome.Succeeded(Previewed.Reviewed(OperationReview.Work value)) ->
                     value.Proposed.Responsible
                     |> Option.exists (fun actor -> actor.ActorId = incoming)
                 | _ -> false)
                "One case is reviewed for reassignment"

            let! written = Service.execute owner input token
            accepted written |> ignore

        let! remaining = list ()

        Expect.isEmpty
            (read remaining).Items
            "All selected cases have been handed over through individual operations"

        let! grants =
            Service.grants
                owner
                {
                    ActorId = ActorId.value outgoing
                    AfterCursor = None
                    Limit = 50
                }
                token

        let count = (read grants).Actor.ResponsibleCases |> Option.get

        Expect.equal
            count.Count
            0L
            "The person screen count follows the same bounded responsibility query"

        Expect.equal count.ResponsibleActor outgoing "The link carries the actual actor filter"
        let! _ = verified database
        return ()
    }

[<Tests>]
let tests =
    testList
        "PostgreSQL responsibility handover"
        [
            testCaseAsync
                "row work revisions preserve reviewed single-case handover and conflicts"
                (run handover)
        ]
