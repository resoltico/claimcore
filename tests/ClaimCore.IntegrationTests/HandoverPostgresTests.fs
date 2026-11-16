module ClaimCore.IntegrationTests.HandoverPostgresTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected read: %A" other

let private work fixture number reference revision responsible summary =
    (ClaimCore.TestSupport.Submissions.createRaw)
        "work.update"
        (id fixture number)
        (ClaimCore.Application.Contracts.OperationBinding.Work(
            (CaseSelector.Reference reference),
            (revision)
        ))
        (ClaimCore.TestSupport.Submissions.work (
            {
                Responsible = responsible
                Summary = summary
            }
        ))

let private handover (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! owner = populated database

        let principal =
            PrincipalKey.human "https://issuer.example/" "outgoing-handler"
            |> Result.defaultWith (string >> invalidOp)

        let! registered =
            (task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (fixture.Store)
                        ((id fixture 100))
                        (principal)
                        (None)
                        "people.register"
                        (ClaimCore.TestSupport.Submissions.profile (Inputs.actorProfile))

                return! Service.executeSubmission (owner) submission (token)
            })

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
            Service.executeSubmission
                owner
                (((request
                    (fixture)
                    (102)
                    ("synthetic-b")
                    (0L)
                    "case.open"
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values (Inputs.registration)
                    )))))
                token

        accepted second |> ignore

        for number, reference in [ 110, "synthetic-a"; 111, "synthetic-b" ] do
            let! updated =
                Service.executeSubmission
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
            Service.executeSubmission
                owner
                (((request
                    (fixture)
                    (112)
                    ("synthetic-closed")
                    (0L)
                    "case.open"
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values (Inputs.registration)
                    )))))
                token

        accepted closedCase |> ignore

        let! closedAssigned =
            Service.executeSubmission
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
            Service.executeSubmission
                owner
                (((request (fixture) (114) ("synthetic-closed") (1L) "case.close" (Map.empty))))
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
            Service.executeSubmission
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

        let! preview = Service.previewSubmission owner stale token

        Expect.isTrue
            (match preview.Outcome with
             | Outcome.Rejected(Refusal.RevisionConflict info) ->
                 info.ActualRevision = CaseFixtures.revision 2L
             | _ -> false)
            "Review conflicts on the selected row revision"

        let! staleWrite = Service.executeSubmission owner stale token

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

            let! review = Service.previewSubmission owner input token

            Expect.isTrue
                (match review.Outcome with
                 | Outcome.Succeeded(Previewed.Reviewed review) ->
                     review.Changes
                     |> List.exists (fun part ->
                         match part.Description with
                         | ClaimCore.Domain.Changes.PartDescription.Work description ->
                             description.Responsible |> Option.bind _.After = Some incoming
                         | _ -> false)
                 | _ -> false)
                "One case is reviewed for reassignment"

            let! written = Service.executeSubmission owner input token
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
