module ClaimCore.IntegrationTests.CaseStageTests

open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.ApplicationFixtures

let private token = CancellationToken.None

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected case read: %A" other

[<Tests>]
let tests =
    testCaseAsync
        "case stages use retained metadata and latest case fact"
        (run (fun database fixture ->
            task {
                let! actor = caseWorker fixture

                let! stewardship =
                    grant
                        actor
                        (id fixture 78006)
                        fixture.Owner
                        {
                            Role = Role.DataSteward
                            Scope = GrantTarget.Installation
                        }
                        true

                acceptedAuthority stewardship

                let write n revision act =
                    Service.execute
                        actor
                        (Inputs.caseOperation (request fixture n "STAGES-1" revision act))
                        token

                let! result = write 78000 0L (AuthoredCaseAct.Open registration)
                let receipt = accepted result |> caseReceipt

                let get () =
                    Service.get
                        actor
                        {
                            Target = CaseSelector.Id receipt.CaseId
                        }
                        token

                let! initial = get ()
                let initial = read initial

                let opening =
                    match initial.Stages with
                    | CaseStageDisplay.Tracker(CaseStageKind.Opened, steps) ->
                        Expect.equal
                            (steps |> List.map _.Completed)
                            [ true; false; false; false ]
                            "Only opening is completed"

                        steps[0].Date.Value
                    | other -> failtestf "Expected opened tracker: %A" other

                use! connection = database.OwnerSource.OpenConnectionAsync()

                let! date =
                    ClaimCore.Postgres.DatabaseSession.rows
                        connection
                        "SELECT to_char(business_date,'YYYY-MM-DD') FROM claimcore.ledger WHERE subject_kind='CASE' AND subject_id=$1 AND case_revision=1"
                        [| box (CaseId.value receipt.CaseId) |]
                        token

                Expect.equal
                    (CalendarDate.format opening)
                    (ClaimCore.Postgres.DatabaseSession.value<string> 0 date.Head)
                    "Opening date is actual retained ledger metadata"

                let! closed = write 78001 1L (AuthoredCaseAct.Bare BareAct.Close)
                accepted closed |> ignore
                let! current = get ()

                match (read current).Stages with
                | CaseStageDisplay.Tracker(CaseStageKind.Closed, steps) ->
                    Expect.equal
                        (steps |> List.map _.Completed)
                        [ true; false; false; true ]
                        "Absent financial groups stay absent"

                    Expect.equal
                        steps[0].Date
                        (Some opening)
                        "Closing preserves original opening date"

                    Expect.isSome steps[3].Date "Close has a retained date"
                | other -> failtestf "Expected closed tracker: %A" other

                let! voided = write 78002 2L (AuthoredCaseAct.Void "Duplicate report")
                accepted voided |> ignore

                let! reinstated =
                    write 78003 3L (AuthoredCaseAct.Reinstate "Confirmed original report")

                accepted reinstated |> ignore
                let! current = get ()

                Expect.equal
                    (read current).Stages
                    CaseStageDisplay.Reinstated
                    "Latest fact act is reinstatement"

                let! changed =
                    Service.execute
                        actor
                        (OperationInput.Work
                            {
                                OperationId = id fixture 78004
                                Target = CaseSelector.Id receipt.CaseId
                                ExpectedRevision = 0L
                                Act =
                                    WorkAct.UpdateWork
                                        {
                                            Responsible = WorkValue.Keep
                                            Summary = WorkValue.Set "Investigate coverage"
                                        }
                            })
                        token

                accepted changed |> ignore
                let! current = get ()

                Expect.equal
                    (read current).Stages
                    CaseStageDisplay.Reinstated
                    "Work changes do not replace latest case-fact act"

                let! reopened = write 78005 4L (AuthoredCaseAct.Bare BareAct.Reopen)
                accepted reopened |> ignore
                let! current = get ()

                match (read current).Stages with
                | CaseStageDisplay.Tracker(CaseStageKind.Opened, steps) ->
                    Expect.equal
                        steps[3].Date
                        None
                        "Reopened tracker does not reuse an earlier close date"
                | other -> failtestf "Expected restored tracker: %A" other
            }))
