module ClaimCore.TestSupport.ReminderConcurrencyContract

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.RecordFormat
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected current read: %A" other

let private caseAct fixture actor number reference revision taskName values =
    Service.executeSubmission
        actor
        (request fixture number reference revision taskName values)
        token

let private reminder fixture actor number target taskName selected values =
    Service.executeSubmission
        actor
        (Submissions.createRaw
            taskName
            (id fixture number)
            (OperationBinding.Reminder(target, selected))
            values)
        token

let private acceptedCount (replies: ExecuteReply array) =
    replies
    |> Array.filter (fun reply ->
        match reply.Outcome with
        | ExecuteResult.Accepted _ -> true
        | _ -> false)
    |> Array.length

let private storeRead (fixture: Fixture) work =
    task {
        let! result =
            fixture.Store.Read
                {
                    Work = work
                    CancellationToken = token
                }

        return
            match result with
            | Ok value -> value
            | Error fault -> failtestf "Store observation failed: %A" fault
    }

let private find fixture number =
    storeRead fixture (fun tx ->
        ReminderReads.findReminder tx (ReminderId.create (id fixture number) |> Option.get))

let private opened fixture actor number reference =
    task {
        let! result =
            caseAct
                fixture
                actor
                number
                reference
                0L
                "case.open"
                (Submissions.fields (RegistrationInput.values registration))

        return accepted result |> caseReceipt |> _.CaseId
    }

let private seed fixture actor number target text =
    task {
        let! result =
            reminder
                (fixture)
                (actor)
                (number)
                (target)
                "reminder.create"
                (None)
                (ClaimCore.TestSupport.Submissions.reminderPlan (text) ("2026-11-10"))

        accepted result |> ignore
    }

let private snapshot (row: StoredReminder option) = (Option.get row).Snapshot |> Option.get

let internal independentRevisions fixture =
    task {
        let! actor = caseWorker fixture
        let! parent = opened fixture actor 100 "REMINDER-CONCURRENCY"
        let target = CaseSelector.Id parent
        do! seed fixture actor 101 target "Original first action"
        do! seed fixture actor 102 target "Original second action"

        let patch =
            (RegistrationUpdateInput.replace
                [ BusinessField.PolicyNumber, Some "Financial preview remains valid" ]
                (DomainInputs.registrationUpdate registration)
             |> Result.defaultWith failtest)

        let financial =
            ((request
                (fixture)
                (103)
                ("REMINDER-CONCURRENCY")
                (1L)
                "case.amendRegistration"
                (ClaimCore.TestSupport.Submissions.updates (RegistrationUpdateInput.values (patch)))))

        let! preview = Service.previewSubmission actor financial token

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed _) -> ()
        | other -> failtestf "Financial preview control: %A" other

        let! changed =
            Task.WhenAll(
                [|
                    reminder
                        (fixture)
                        (actor)
                        (104)
                        (target)
                        "reminder.update"
                        (Some((id fixture 101), (1L)))
                        (ClaimCore.TestSupport.Submissions.reminderPlan
                            ("First independent edit")
                            ("2026-11-11"))
                    reminder
                        (fixture)
                        (actor)
                        (105)
                        (target)
                        "reminder.update"
                        (Some((id fixture 102), (1L)))
                        (ClaimCore.TestSupport.Submissions.reminderPlan
                            ("Second independent edit")
                            ("2026-11-12"))
                |]
            )

        Expect.equal
            (acceptedCount changed)
            2
            "Different reminders use their own expected revisions"

        let! view = Service.get actor { Target = target } token

        Expect.equal
            (Revision.value (read view).Snapshot.Revision)
            1L
            "No financial invalidation from reminders"

        Expect.equal
            (Revision.value (read view).Work.Revision)
            0L
            "No work root revision from reminders"

        let! financialResult = Service.executeSubmission actor financial token
        accepted financialResult |> ignore

        let! raced =
            Task.WhenAll(
                [|
                    reminder
                        (fixture)
                        (actor)
                        (106)
                        (target)
                        "reminder.update"
                        (Some((id fixture 101), (2L)))
                        (ClaimCore.TestSupport.Submissions.reminderPlan
                            ("Concurrent edit A")
                            ("2026-11-13"))
                    reminder
                        (fixture)
                        (actor)
                        (107)
                        (target)
                        "reminder.update"
                        (Some((id fixture 101), (2L)))
                        (ClaimCore.TestSupport.Submissions.reminderPlan
                            ("Concurrent edit B")
                            ("2026-11-14"))
                |]
            )

        Expect.equal (acceptedCount raced) 1 "Exactly one same-revision edit accepts"

        let refusals =
            raced
            |> Array.choose (fun reply ->
                match reply.Outcome with
                | ExecuteResult.Rejected refusal -> Some refusal
                | _ -> None)

        Expect.equal refusals.Length 1 "Exactly one conflict remains"

        match refusals[0] with
        | Refusal.RevisionConflict conflict ->
            Expect.equal
                (Revision.value conflict.ActualRevision)
                3L
                "Loser observes the winning revision"

            match conflict.Changes with
            | Some(ConflictChanges.Page page) ->
                Expect.equal page.Entries.Length 1 "The winner is explained without a rebase"
            | other -> failtestf "Missing authorized conflict explanation: %A" other
        | other -> failtestf "Unexpected losing outcome: %A" other

        let! a = find fixture 101
        let! b = find fixture 102
        Expect.equal (Revision.value (Reminder.revision (snapshot a))) 3L "One winning edit only"

        Expect.equal
            (ReminderAction.format (Reminder.action (snapshot b)))
            "Second independent edit"
            "Other reminder remains independent"

        let! assigned =
            Task.WhenAll(
                [|
                    Service.executeSubmission
                        actor
                        ((ClaimCore.TestSupport.Submissions.createRaw)
                            "work.update"
                            (id fixture 108)
                            (ClaimCore.Application.Contracts.OperationBinding.Work((target), (0L)))
                            (ClaimCore.TestSupport.Submissions.work (
                                {
                                    Responsible =
                                        WorkValue.Set(
                                            ActorId.format (CaseFixtures.actorId (id fixture 1))
                                        )
                                    Summary = WorkValue.Keep
                                }
                            )))
                        token
                    reminder
                        (fixture)
                        (actor)
                        (109)
                        (target)
                        "reminder.update"
                        (Some((id fixture 102), (2L)))
                        (ClaimCore.TestSupport.Submissions.reminderPlan
                            ("Edit alongside assignment")
                            ("2026-11-15"))
                |]
            )

        Expect.equal
            (acceptedCount assigned)
            2
            "Assignment and an independent reminder edit can both commit"

        let! assignedView = Service.get actor { Target = target } token
        let assignedView = read assignedView
        Expect.isTrue assignedView.Mine "Current case responsibility governs Mine after the race"

        Expect.equal
            (Revision.value assignedView.Work.Revision)
            1L
            "Only the assignment advanced work"

        Expect.equal
            (Revision.value assignedView.Snapshot.Revision)
            2L
            "Neither work family changes financial facts"

    }

let internal closeAndCompound fixture =
    task {
        let! actor = caseWorker fixture
        let! parent = opened fixture actor 100 "REMINDER-CLOSE-RACE"
        let target = CaseSelector.Id parent
        do! seed fixture actor 101 target "Original outstanding action"

        let! replies =
            Task.WhenAll(
                [|
                    caseAct
                        fixture
                        actor
                        102
                        "REMINDER-CLOSE-RACE"
                        1L
                        "case.close"
                        CaseTransitionInputs.closed
                    reminder
                        (fixture)
                        (actor)
                        (103)
                        (target)
                        "reminder.completeAndPlanNext"
                        (Some((id fixture 101), (1L)))
                        (ClaimCore.TestSupport.Submissions.reminderPlan
                            ("Replacement outstanding action")
                            ("2026-11-11"))
                |]
            )

        accepted replies[0] |> ignore
        let! old = find fixture 101
        let! next = find fixture 103

        match replies[1].Outcome with
        | ExecuteResult.Accepted _ ->
            Expect.equal
                (Reminder.state (snapshot old))
                ReminderState.Completed
                "Compound won before close"

            Expect.equal
                (Reminder.state (snapshot next))
                ReminderState.Pending
                "Both effects retained inactive"
        | ExecuteResult.Rejected(Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Closed)) ->
            Expect.equal
                (Reminder.state (snapshot old))
                ReminderState.Pending
                "Close won before compound"

            Expect.isNone next "Refusal creates no replacement"
        | other -> failtestf "Invalid close/compound result: %A" other

        let! view = Service.get actor { Target = target } token

        Expect.equal
            (read view).Work.Due
            CaseDue.Inactive
            "Close never silently completes remaining work"

        Expect.equal (Revision.value (read view).Snapshot.Revision) 2L "Exactly one financial close"
    }

let internal erasureRaces fixture =
    task {
        let! actor = caseWorker fixture

        let! stewardship =
            grant
                actor
                (id fixture 19)
                fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople stewardship

        for number, reference, compound in
            [ 100, "ERASE-CREATE-RACE", false; 200, "ERASE-COMPOUND-RACE", true ] do
            let! parent = opened fixture actor number reference
            let target = CaseSelector.Id parent

            if compound then
                do! seed fixture actor (number + 1) target "Erasable source action"

            let taskName, selected, values =
                if compound then
                    "reminder.completeAndPlanNext",
                    Some(id fixture (number + 1), 1L),
                    Submissions.reminderPlan "Erasable replacement action" "2026-11-11"
                else
                    "reminder.create",
                    None,
                    Submissions.reminderPlan "Erasable new action" "2026-11-11"

            let reviewed =
                request (fixture) ((number + 3)) (reference) (1L) "case.erase" (Map.empty)

            let! replies =
                Task.WhenAll(
                    [|
                        Service.executeSubmission actor ((reviewed)) token
                        reminder fixture actor (number + 2) target taskName selected values
                    |]
                )

            Expect.equal
                (acceptedCount replies)
                1
                "Only one side of the frozen family-review race can accept"

            match replies[0].Outcome, replies[1].Outcome with
            | ExecuteResult.Accepted _,
              ExecuteResult.Rejected(Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Erased)) -> ()
            | ExecuteResult.Rejected(Refusal.Case CaseRefusal.FamilyChanged),
              ExecuteResult.Accepted _ ->
                let! retained = find fixture (number + 2)

                Expect.isSome
                    (Option.get retained).Snapshot
                    "Stale erasure leaves the newly accepted private child intact"

                let! refusedRoot =
                    storeRead fixture (fun tx ->
                        LedgerReads.findOperation
                            tx
                            (OperationId.create reviewed.OperationId |> Option.get))

                Expect.isNone refusedRoot "The stale erasure appended nothing"
                // A separate explicit review/new operation, never an automatic retry of the stale bytes.
                let fresh =
                    request (fixture) ((number + 4)) (reference) (1L) "case.erase" (Map.empty)

                Expect.notEqual
                    fresh.Envelope.Binding
                    reviewed.Envelope.Binding
                    "Fresh erasure observes the accepted child"

                let! erased = Service.executeSubmission actor ((fresh)) token
                accepted erased |> ignore
            | other -> failtestf "Invalid erase/reminder ordering: %A" other

            let! child = find fixture (number + 2)

            match replies[1].Outcome with
            | ExecuteResult.Accepted _ ->
                let child = Option.get child
                Expect.isNone child.Snapshot "Accepted-before-erasure child is a reserved tombstone"
                Expect.isEmpty child.SnapshotBytes "No child content survives accepted erasure"

                let! entry =
                    storeRead fixture (fun tx ->
                        LedgerReads.findOperation
                            tx
                            (OperationId.create (id fixture (number + 2)) |> Option.get))

                Expect.isTrue
                    ((Option.get entry).Root.Structure.Compartments
                     |> List.forall (Compartment.content >> Option.isNone))
                    "Related private contents and nonce are cleared together"
            | ExecuteResult.Rejected(Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Erased)) ->
                Expect.isNone child "Erase-before-action creates no child"
            | other -> failtestf "Invalid erase/reminder outcome: %A" other

            if compound then
                let! old = find fixture (number + 1)
                Expect.isNone (Option.get old).Snapshot "Source content is erased in either order"
                Expect.isEmpty (Option.get old).SnapshotBytes "No original action/date bytes"

            let! view = Service.get actor { Target = target } token

            Expect.equal
                (read view).Snapshot.State
                CaseState.Erased
                "No reminder can resurrect its parent"
    }

let internal revocationAndCompound fixture =
    task {
        let! actor = caseWorker fixture

        let person =
            PrincipalKey.human "https://issuer.example/" "concurrent-reminder-handler"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            (task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (fixture.Store)
                        ((id fixture 10))
                        (person)
                        (None)
                        "people.register"
                        (ClaimCore.TestSupport.Submissions.profile (
                            ClaimCore.TestSupport.DomainInputs.actorProfile
                        ))

                return! Service.executeSubmission (actor) submission (token)
            })

        acceptedPeople registered
        let! parent = opened fixture actor 100 "REVOKE-COMPOUND-RACE"
        let target = CaseSelector.Id parent

        for number, role in [ 11, Role.CaseHandler; 12, Role.CaseReader ] do
            let! granted =
                grant
                    actor
                    (id fixture number)
                    person
                    {
                        Role = role
                        Scope = GrantTarget.CaseId parent
                    }
                    true

            acceptedPeople granted

        do! seed fixture actor 101 target "Permission-sensitive action"

        let request =
            ClaimCore.TestSupport.Submissions.createRaw
                "reminder.completeAndPlanNext"
                (id fixture 103)
                (ClaimCore.Application.Contracts.OperationBinding.Reminder(
                    (target),
                    Some((id fixture 101), (1L))
                ))
                (ClaimCore.TestSupport.Submissions.reminderPlan
                    ("Replacement after authorised completion")
                    ("2026-11-11"))

        let handler = core fixture person

        let! preview = Service.previewSubmission handler ((request)) token

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed _) -> ()
        | other -> failtestf "Authorised preview control: %A" other

        let! replies =
            Task.WhenAll(
                [|
                    grant
                        actor
                        (id fixture 102)
                        person
                        {
                            Role = Role.CaseHandler
                            Scope = GrantTarget.CaseId parent
                        }
                        false
                    Service.executeSubmission handler ((request)) token
                |]
            )

        acceptedPeople replies[0]
        let! old = find fixture 101
        let! next = find fixture 103

        match replies[1].Outcome with
        | ExecuteResult.Accepted _ ->
            Expect.equal
                (Reminder.state (snapshot old))
                ReminderState.Completed
                "Actual action people won before revoke"

            Expect.equal
                (Reminder.state (snapshot next))
                ReminderState.Pending
                "Both effects accepted"
        | ExecuteResult.Rejected(Refusal.Access(AccessRefusal.NotAuthorized Capability.ManageCaseWork)) ->
            Expect.equal
                (Reminder.state (snapshot old))
                ReminderState.Pending
                "Revocation enforced at action state"

            Expect.isNone next "No partial compound effect"
        | other -> failtestf "Invalid revoke/compound outcome: %A" other

        let! after =
            Service.executeSubmission
                handler
                ((ClaimCore.TestSupport.Submissions.withOperationId (id fixture 104) (request)))
                token

        Expect.equal
            after.Outcome
            (ExecuteResult.Rejected(
                Refusal.Access(AccessRefusal.NotAuthorized Capability.ManageCaseWork)
            ))
            "Late preview cannot restore capability"
    }

let internal notOffered fixture =
    task {
        let! actor = caseWorker fixture
        let! parent = opened fixture actor 100 "REMINDER-STATE"
        let target = CaseSelector.Id parent
        do! seed fixture actor 101 target "Inspect current state"

        let change number taskName selected values =
            reminder fixture actor number target taskName selected values

        let expect refusal (reply: ExecuteReply) =
            Expect.equal
                reply.Outcome
                (ExecuteResult.Rejected refusal)
                "Current business state is reported only after authorization"

        let! pending =
            change
                (102)
                "reminder.reopen"
                (Some((id fixture 101), (1L)))
                (Map.ofList
                    [
                        TaskInputId.Target InputTarget.ReminderDueDate,
                        ClaimCore.Application.Contracts.SubmissionValue.Text("2026-11-11")
                    ])

        expect (Refusal.Reminder(ReminderRuleRefusal.NotOffered ReminderState.Pending)) pending

        let! completed =
            change (103) "reminder.complete" (Some((id fixture 101), (1L))) (Map.empty)

        accepted completed |> ignore

        let! repeated =
            change (104) "reminder.complete" (Some((id fixture 101), (2L))) (Map.empty)

        expect (Refusal.Reminder(ReminderRuleRefusal.NotOffered ReminderState.Completed)) repeated

        let! reopened =
            change
                (105)
                "reminder.reopen"
                (Some((id fixture 101), (2L)))
                (Map.ofList
                    [
                        TaskInputId.Target InputTarget.ReminderDueDate,
                        ClaimCore.Application.Contracts.SubmissionValue.Text("2026-11-11")
                    ])

        accepted reopened |> ignore

        let! cancelled =
            change (106) "reminder.cancel" (Some((id fixture 101), (3L))) (Map.empty)

        accepted cancelled |> ignore

        let update = Submissions.reminderPlan "Updated action" "2026-11-12"

        let! unavailable = change 107 "reminder.update" (Some(id fixture 101, 4L)) update

        expect
            (Refusal.Reminder(ReminderRuleRefusal.NotOffered ReminderState.Cancelled))
            unavailable

        let! closed =
            caseAct fixture actor 108 "REMINDER-STATE" 1L "case.close" CaseTransitionInputs.closed

        accepted closed |> ignore

        let! parentClosed =
            change
                (109)
                "reminder.reopen"
                (Some((id fixture 101), (4L)))
                (Map.ofList
                    [
                        TaskInputId.Target InputTarget.ReminderDueDate,
                        ClaimCore.Application.Contracts.SubmissionValue.Text("2026-11-11")
                    ])

        expect (Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Closed)) parentClosed

        let revoke number role =
            grant
                actor
                (id fixture number)
                fixture.Owner
                {
                    Role = role
                    Scope = GrantTarget.Installation
                }
                false

        let! revoked = revoke 110 Role.CaseHandler
        acceptedPeople revoked
        let! noCapability = change 111 "reminder.update" (Some(id fixture 101, 4L)) update
        expect (Refusal.Access(AccessRefusal.NotAuthorized Capability.ManageCaseWork)) noCapability
        let! revoked = revoke 112 Role.PaymentRecorder
        acceptedPeople revoked
        let! unreadable = change 113 "reminder.update" (Some(id fixture 101, 4L)) update
        expect (Refusal.Access AccessRefusal.ResourceUnavailable) unreadable
    }
