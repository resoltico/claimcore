module ClaimCore.IntegrationTests.CaseStageTests

open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
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

                acceptedPeople stewardship

                let write n revision taskName values =
                    Service.executeSubmission
                        actor
                        (((request fixture n "STAGES-1" revision taskName values)))
                        token

                let! result =
                    write
                        (78000)
                        (0L)
                        "case.open"
                        (ClaimCore.TestSupport.Submissions.fields (
                            RegistrationInput.values (registration)
                        ))

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
                    ClaimCore.Application.Storage.DatabaseSession.rows
                        connection
                        "SELECT to_char(l.business_date,'YYYY-MM-DD') FROM claimcore.ledger l JOIN claimcore.ledger_effects e ON e.operation_sequence=l.sequence WHERE e.part_kind='CASE' AND e.part_id=$1 AND e.revision=1"
                        [| box (CaseId.value receipt.CaseId) |]
                        token

                Expect.equal
                    (CalendarDate.format opening)
                    (ClaimCore.Application.Storage.DatabaseSession.value<string> 0 date.Head)
                    "Opening date is actual retained ledger metadata"

                let close outcome =
                    [ Submissions.field BusinessField.Outcome, SubmissionValue.Text outcome ]
                    |> Map.ofList

                let reason =
                    Map.ofList
                        [
                            Submissions.input InputTarget.Reason,
                            SubmissionValue.Text "Additional documents received"
                        ]

                let! closed = write 78001 1L "case.close" (close "WITHDRAWN")
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

                    Expect.equal
                        steps[3].Outcome
                        (Some ClosureOutcome.Withdrawn)
                        "Closed stage shows the asserted outcome"
                | other -> failtestf "Expected closed tracker: %A" other

                let! voided =
                    write
                        (78002)
                        (2L)
                        "case.void"
                        (Map.ofList
                            [
                                TaskInputId.Target InputTarget.Reason,
                                ClaimCore.Application.Contracts.SubmissionValue.Text(
                                    "Duplicate report"
                                )
                            ])

                accepted voided |> ignore

                let! reinstated =
                    write
                        (78003)
                        (3L)
                        "case.reinstate"
                        (Map.ofList
                            [
                                TaskInputId.Target InputTarget.Reason,
                                ClaimCore.Application.Contracts.SubmissionValue.Text(
                                    "Confirmed original report"
                                )
                            ])

                accepted reinstated |> ignore
                let! current = get ()

                Expect.equal
                    (read current).Disposition
                    (Some(
                        CaseDisposition.Reinstated(
                            Reason.parse "Confirmed original report"
                            |> Result.defaultWith (string >> failtest)
                        )
                    ))
                    "Latest fact act is reinstatement"

                let! changed =
                    Service.executeSubmission
                        actor
                        ((ClaimCore.TestSupport.Submissions.createRaw)
                            "work.update"
                            (id fixture 78004)
                            (ClaimCore.Application.Contracts.OperationBinding.Work(
                                (CaseSelector.Id receipt.CaseId),
                                (0L)
                            ))
                            (ClaimCore.TestSupport.Submissions.work (
                                {
                                    Responsible = WorkValue.Keep
                                    Summary = WorkValue.Set "Investigate coverage"
                                }
                            )))
                        token

                accepted changed |> ignore
                let! current = get ()

                Expect.equal
                    (read current).Disposition
                    (Some(
                        CaseDisposition.Reinstated(
                            Reason.parse "Confirmed original report"
                            |> Result.defaultWith (string >> failtest)
                        )
                    ))
                    "Work changes do not replace latest case-fact act"

                let! reopened = write 78005 4L "case.reopen" reason
                accepted reopened |> ignore
                let! current = get ()

                match (read current).Stages with
                | CaseStageDisplay.Tracker(CaseStageKind.Opened, steps) ->
                    Expect.equal
                        steps[3].Date
                        None
                        "Reopened tracker does not reuse an earlier close date"
                | other -> failtestf "Expected restored tracker: %A" other

                let! decision =
                    write
                        78007
                        5L
                        "case.decide"
                        (Map.ofList
                            [
                                Submissions.field BusinessField.PaymentDecisionDate,
                                SubmissionValue.Text "2026-08-15"
                                Submissions.field BusinessField.PayableAmount,
                                SubmissionValue.Text "100"
                                Submissions.field BusinessField.PayableCurrency,
                                SubmissionValue.Text "EUR"
                                Submissions.field BusinessField.DecisionBasis,
                                SubmissionValue.Text "Deductible EUR 100 applied"
                            ])

                let decision = accepted decision |> caseReceipt

                match decision.Snapshot with
                | SnapshotDisclosure.Disclosed live ->
                    let basis =
                        LiveCase.toState live.State
                        |> CaseState.fields
                        |> Option.bind _.Decision
                        |> Option.bind Decision.basis
                        |> Option.map NoteText.format

                    Expect.equal
                        basis
                        (Some "Deductible EUR 100 applied")
                        "Receipt discloses the decision basis"
                | _ -> failtest "Expected disclosed receipt"

                let! closedAgain = write 78008 6L "case.close" (close "DECLINED")
                accepted closedAgain |> ignore
                let! current = get ()
                let current = read current

                match current.Stages with
                | CaseStageDisplay.Tracker(CaseStageKind.Closed, steps) ->
                    Expect.equal
                        steps[3].Outcome
                        (Some ClosureOutcome.Declined)
                        "Latest close determines current outcome"
                | other -> failtestf "Expected tracker after the second close: %A" other

                let! closeHistory =
                    DatabaseSession.rows
                        connection
                        "SELECT count(*) FROM claimcore.ledger l JOIN claimcore.ledger_effects e ON l.sequence=e.operation_sequence WHERE l.task=$1 AND e.part_id=$2"
                        [| box "case.close"; box (CaseId.value receipt.CaseId) |]
                        token

                Expect.equal
                    (DatabaseSession.value<int64> 0 closeHistory.Head)
                    2L
                    "Both close operations survive"
            }))
