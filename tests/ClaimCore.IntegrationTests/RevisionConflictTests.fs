module ClaimCore.IntegrationTests.RevisionConflictTests

open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.ApplicationFixtures

let private token = CancellationToken.None

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "%A" other

let private conflict =
    function
    | ExecuteResult.Rejected(Refusal.RevisionConflict value) -> value
    | other -> failtestf "Expected revision conflict: %A" other

let private page (conflict: RevisionConflictInfo) =
    match conflict.Changes with
    | Some(ConflictChanges.Page page) -> page
    | other -> failtestf "Expected observed change page: %A" other

let private update amount =
    DomainInputs.registrationWith [ BusinessField.ClaimedAmount, amount ] registration
    |> DomainInputs.registrationUpdate
    |> RegistrationUpdateInput.values
    |> Submissions.updates

let private caseWrite fixture owner number expected taskName values =
    Service.executeSubmission
        owner
        (((request fixture number "CONFLICT-1" expected taskName values)))
        token

[<Tests>]
let tests =
    testCaseAsync
        "revision conflicts retain bounded snapshot ranges and current disclosure"
        (run (fun _ fixture ->
            task {
                let! owner = caseWorker fixture

                let! opened =
                    caseWrite
                        (fixture)
                        (owner)
                        (77000)
                        (0L)
                        "case.open"
                        (ClaimCore.TestSupport.Submissions.fields (
                            RegistrationInput.values (registration)
                        ))

                accepted opened |> ignore

                for revision in 1..60 do
                    let! changed =
                        caseWrite
                            fixture
                            owner
                            (77000 + revision)
                            (int64 revision)
                            "case.amendRegistration"
                            (update (string (1000 + revision)))

                    accepted changed |> ignore

                let! stale =
                    caseWrite fixture owner 77100 1L "case.amendRegistration" (update "5000")

                let observed = conflict stale.Outcome

                Expect.equal
                    (Revision.value observed.ActualRevision)
                    61L
                    "The actual revision belongs to the action snapshot"

                let first = page observed

                Expect.equal
                    first.Entries.Length
                    10
                    "Large ranges preserve full details within the response cap"

                Expect.isSome first.NextCursor "The remaining changes have a continuation"

                let revisions (entries: CaseTimelineEntry list) =
                    entries
                    |> List.map (fun entry ->
                        Expect.equal
                            entry.Subject.PartKind
                            ClaimCore.Domain.Changes.PartKind.Case
                            "Case conflict part"

                        Revision.value entry.Subject.Revision)


                Expect.equal
                    (revisions first.Entries)
                    [ 61L .. (-1L) .. 52L ]
                    "The first page is the expected-to-observed case range"

                Expect.isTrue
                    (first.Entries
                     |> List.forall (fun entry ->
                         match entry.Changes with
                         | TimelineChanges.Disclosed _ -> true
                         | _ -> false))
                    "Full-history people receives typed differences"

                let encoded =
                    Codec.encode Api.operationExecute.Output (RepresentationReply.Data stale)

                Expect.isLessThan
                    encoded.Length
                    Limits.MaximumResponseBytes
                    "The actual enriched refusal keeps the reply limit"

                let! later =
                    caseWrite fixture owner 77101 61L "case.amendRegistration" (update "6000")

                accepted later |> ignore

                let all = ResizeArray<CaseTimelineEntry>()
                let mutable nextCursor = first.NextCursor

                while nextCursor.IsSome do
                    let! result =
                        Service.timeline
                            owner
                            {
                                Target = CaseSelector.Reference "CONFLICT-1"
                                Filter = None
                                AfterCursor = nextCursor
                                Limit = 50
                            }
                            token

                    let current = read result

                    Expect.isLessThanOrEqual
                        current.Items.Length
                        10
                        "Continuation respects the full-detail response bound."

                    all.AddRange current.Items
                    nextCursor <- current.NextCursor

                let rest = List.ofSeq all

                Expect.equal
                    (revisions rest)
                    [ 51L .. (-1L) .. 2L ]
                    "Paging covers the exact observed range without future writes."

                let registrar =
                    PrincipalKey.human "https://issuer.example/" "summary-registrar"
                    |> Result.defaultWith (fun _ -> failtest "Principal fixture")

                let! registered =
                    (task {
                        let! submission =
                            ClaimCore.TestSupport.StoreFixtures.personSubmission
                                (fixture.Store)
                                ((id fixture 77102))
                                (registrar)
                                (None)
                                "people.register"
                                (ClaimCore.TestSupport.Submissions.profile (
                                    DomainInputs.actorProfile
                                ))

                        return! Service.executeSubmission (owner) submission (token)
                    })

                accepted registered |> ignore

                let! granted =
                    grant
                        owner
                        (id fixture 77103)
                        registrar
                        {
                            Role = Role.CaseRegistrar
                            Scope = GrantTarget.Installation
                        }
                        true

                acceptedPeople granted
                let reader = { owner with Principal = registrar }

                let! hidden =
                    caseWrite fixture reader 77104 61L "case.amendRegistration" (update "7000")

                let hidden = page (conflict hidden.Outcome)

                Expect.equal
                    hidden.Entries.Length
                    1
                    "Summary history can identify the change without exposing old values"

                Expect.isTrue
                    (hidden.Entries
                     |> List.forall (fun entry -> entry.Changes = TimelineChanges.Withheld))
                    "Revision conflicts keep full-history disclosure separate from write permission"

                let target = CaseSelector.Reference "CONFLICT-1"

                for number, expected, summary in
                    [ 77200, 0L, "First summary"; 77201, 1L, "Second summary" ] do
                    let! changed =
                        Service.executeSubmission
                            owner
                            ((ClaimCore.TestSupport.Submissions.createRaw)
                                "work.update"
                                (id fixture number)
                                (ClaimCore.Application.Contracts.OperationBinding.Work(
                                    (target),
                                    (expected)
                                ))
                                (ClaimCore.TestSupport.Submissions.work (
                                    {
                                        Responsible = WorkValue.Keep
                                        Summary = WorkValue.Set summary
                                    }
                                )))
                            token

                    accepted changed |> ignore

                let! staleWork =
                    Service.executeSubmission
                        owner
                        ((ClaimCore.TestSupport.Submissions.createRaw)
                            "work.update"
                            (id fixture 77202)
                            (ClaimCore.Application.Contracts.OperationBinding.Work((target), (0L)))
                            (ClaimCore.TestSupport.Submissions.work (
                                {
                                    Responsible = WorkValue.Keep
                                    Summary = WorkValue.Set "Stale summary"
                                }
                            )))
                        token

                let work = page (conflict staleWork.Outcome)

                Expect.equal
                    work.Entries.Length
                    2
                    "Work conflicts use their independent revision range"

                Expect.isTrue
                    (work.Entries
                     |> List.forall (fun entry ->
                         entry.Subject.PartKind = ClaimCore.Domain.Changes.PartKind.Work))
                    "Financial changes do not enter work conflicts"

                let! planned =
                    Service.executeSubmission
                        owner
                        ((ClaimCore.TestSupport.Submissions.createRaw)
                            "reminder.create"
                            (id fixture 77300)
                            (ClaimCore.Application.Contracts.OperationBinding.Reminder(
                                (target),
                                None
                            ))
                            (ClaimCore.TestSupport.Submissions.reminderPlan
                                ("Initial action")
                                ("2027-12-31")))
                        token

                accepted planned |> ignore

                let! revised =
                    Service.executeSubmission
                        owner
                        ((ClaimCore.TestSupport.Submissions.createRaw)
                            "reminder.update"
                            (id fixture 77301)
                            (ClaimCore.Application.Contracts.OperationBinding.Reminder(
                                (target),
                                Some((id fixture 77300), (1L))
                            ))
                            (ClaimCore.TestSupport.Submissions.reminderPlan
                                ("Revised action")
                                ("2027-12-31")))
                        token

                accepted revised |> ignore

                let! staleReminder =
                    Service.executeSubmission
                        owner
                        ((ClaimCore.TestSupport.Submissions.createRaw)
                            "reminder.complete"
                            (id fixture 77302)
                            (ClaimCore.Application.Contracts.OperationBinding.Reminder(
                                (target),
                                Some((id fixture 77300), (1L))
                            ))
                            (Map.empty))
                        token

                let reminders = page (conflict staleReminder.Outcome)

                Expect.equal
                    reminders.Entries.Length
                    1
                    "Reminder conflicts isolate their own revisions"

                match reminders.Entries.Head.Changes with
                | TimelineChanges.Disclosed {
                                                Items = [ TimelineChange.Reminder difference ]
                                            } ->
                    Expect.equal
                        (ReminderId.value difference.ReminderId)
                        (id fixture 77300)
                        "The difference belongs to the requested reminder"
                | other -> failtestf "Unexpected reminder conflict changes: %A" other
            }))
