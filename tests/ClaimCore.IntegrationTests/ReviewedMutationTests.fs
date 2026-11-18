module ClaimCore.IntegrationTests.ReviewedMutationTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application
open ClaimCore.Application.Administration
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private acceptedChange service input =
    task {
        let! reply = Service.executeSubmission service input token

        match reply.Outcome with
        | ExecuteResult.Accepted receipt -> return receipt
        | other -> return failtestf "Expected accepted setup change: %A" other
    }

let private peopleRevisions (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! owner = caseWorker fixture

        let principal =
            PrincipalKey.human "https://review.example/" "person"
            |> Result.defaultWith (fun _ -> failtest "principal")

        let people number expected taskName values =
            let declaration =
                ClaimCore.Domain.Tasks.Registry.findDeclaration (TaskId.ofLiteral taskName)
                |> Option.get

            let binding =
                OperationBinding.People(
                    (if declaration.Creates then None else Some(id fixture 10)),
                    expected
                )

            let authored =
                if declaration.Creates then
                    Map.fold
                        (fun result key value -> Map.add key value result)
                        values
                        (Submissions.principal principal)
                else
                    values

            Submissions.createRaw taskName (id fixture number) binding authored

        let profile name =
            {
                ActorProfileInput.DisplayName = name
                DigestEmail = None
                Contact = None
            }

        let grant =
            {
                GrantInput.Role = Role.CaseReader
                Scope = GrantTarget.Installation
            }

        let! _ =
            acceptedChange
                owner
                (people
                    (10)
                    (0L)
                    "people.admit"
                    (ClaimCore.TestSupport.Submissions.profile (profile "Original")
                     |> Map.add
                         (TaskInputId.Target InputTarget.Grants)
                         (ClaimCore.Application.Contracts.SubmissionValue.Rows(
                             ([ grant ]) |> List.map ClaimCore.TestSupport.Submissions.grant
                         ))))

        let state () =
            task {
                let! value =
                    fixture.Store.Read
                        {
                            Work = (fun tx -> AggregateStorage.principal tx principal)
                            CancellationToken = token
                        }

                return
                    value
                    |> Result.defaultWith (fun failure -> failtestf "Read: %A" failure)
                    |> Option.get
            }

        let! initial = state ()

        Expect.equal
            (Revision.value initial.Revision)
            1L
            "Admission's register/profile/grant effects advance once"

        let first =
            people
                (11)
                (1L)
                "people.setProfile"
                (ClaimCore.TestSupport.Submissions.profile ((profile "First owner")))

        let! profileChanged = acceptedChange owner first

        for stale in
            [
                people
                    (12)
                    (1L)
                    "people.setProfile"
                    (ClaimCore.TestSupport.Submissions.profile ((profile "Second owner")))
                people (13) (1L) "people.eraseProfile" (Map.empty)
                people (14) (1L) "people.disable" (Map.empty)
            ] do
            let! reply = Service.executeSubmission owner stale token

            Expect.equal
                reply.Outcome
                (ExecuteResult.Rejected(Refusal.People PeopleRefusal.ActorChanged))
                "Stale reviewed state cannot overwrite or erase newer content"

        let! current = state ()

        Expect.equal
            (current.Actor.Profile
             |> Option.map (fun value -> PartyName.format value.DisplayName))
            (Some "First owner")
            "New private profile survives stale erasure"

        let! _ = acceptedChange owner (people (15) (2L) "people.disable" (Map.empty))

        let! staleEnable =
            Service.executeSubmission owner (people (16) (2L) "people.enable" (Map.empty)) token

        Expect.equal
            staleEnable.Outcome
            (ExecuteResult.Rejected(Refusal.People PeopleRefusal.ActorChanged))
            "Stale enable cannot undo a newer disable"

        let! _ = acceptedChange owner (people (17) (3L) "people.enable" (Map.empty))

        let! _ =
            acceptedChange
                owner
                (people (18) (4L) "people.grant" (ClaimCore.TestSupport.Submissions.grant (grant)))

        let! reaffirmed = state ()

        Expect.equal
            (Revision.value reaffirmed.Revision)
            5L
            "Deliberate reaffirmation advances exactly once"

        let! replay = Service.executeSubmission owner first token

        match replay.Outcome with
        | ExecuteResult.AlreadyAccepted _ -> ()
        | other -> failtestf "Accepted retry precedes stale target checks: %A" other

        let! afterReplay = state ()

        Expect.equal
            afterReplay.Revision
            reaffirmed.Revision
            "Exact retry appends and advances nothing"

        let changed =
            people
                (11)
                (5L)
                "people.setProfile"
                (ClaimCore.TestSupport.Submissions.profile ((profile "First owner")))

        let! conflict = Service.executeSubmission owner changed token

        Expect.equal
            conflict.Outcome
            (ExecuteResult.Rejected Refusal.IdempotencyConflict)
            "Changed reviewed basis changes canonical identity"

        let! _ =
            acceptedChange
                owner
                (people (20) (5L) "people.revoke" (ClaimCore.TestSupport.Submissions.grant (grant)))

        let! staleGrant =
            Service.executeSubmission
                owner
                (people (21) (5L) "people.grant" (ClaimCore.TestSupport.Submissions.grant (grant)))
                token

        Expect.equal
            staleGrant.Outcome
            (ExecuteResult.Rejected(Refusal.People PeopleRefusal.ActorChanged))
            "An old reviewed grant cannot undo a newer revocation"

        let! _ =
            acceptedChange
                owner
                (people (22) (6L) "people.grant" (ClaimCore.TestSupport.Submissions.grant (grant)))

        let! _ = acceptedChange owner (people (19) (7L) "people.eraseProfile" (Map.empty))
        let! _ = verified database
        do! enterMaintenance database
        let! checkpoint = LedgerAdministration.head database.OwnerSource token

        let! rebuilt =
            ProjectionRebuild.rebuild
                database.OwnerSource
                checkpoint
                (MaintenanceHandle.create
                    1L
                    (System.Guid.Parse "10000000-0000-4000-8000-000000000099")
                 |> Option.get)
                token

        Expect.isOk rebuilt "Erased profiles rebuild from retained revision metadata"
        let! _ = verified database

        do!
            rewrite database profileChanged.Position.Sequence (fun retained ->
                match retained.Changes.Head.Value with
                | PartValue.Person { Profile = ProfileValue.Present None } -> ()
                | other -> failtestf "The negative requires unavailable private profile: %A" other

                let binding =
                    match retained.Root.Structure.Binding with
                    | Binding.People(id, _) -> Binding.People(id, CaseFixtures.revision 2L)
                    | _ -> failtest "Person binding required"

                rehashed
                    retained.Root.Header
                    retained.Root.Structure.Task
                    binding
                    retained.Root.Structure.Retained
                    retained.Root.Structure.Effects
                    retained.Root.Structure.Compartments)


        let! detected = Verification.verify database.OwnerSource None token

        match detected with
        | VerificationResult.Diverged failure ->
            Expect.equal
                failure.Finding
                VerificationFinding.StateSequence
                "The common fold rejects the non-reproduced person decision"

            Expect.equal
                failure.Sequence
                (Some profileChanged.Position.Sequence)
                "The seeded changed binding is the first divergent root"
        | other -> failtestf "Seeded person decision was not detected: %A" other

        return ()
    }

let private familyReview (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! owner = caseWorker fixture

        let! permission =
            grant
                owner
                (id fixture 29)
                fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        match permission.Outcome with
        | ExecuteResult.Accepted _ -> ()
        | other -> failtestf "Steward setup: %A" other

        let target = CaseSelector.Reference "FAMILY-REVIEW"

        let! _ =
            acceptedChange
                owner
                (((request
                    (fixture)
                    (30)
                    ("FAMILY-REVIEW")
                    (0L)
                    "case.open"
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values (registration)
                    )))))

        let review number =
            task {
                let! reply = Service.get owner { Target = target } token

                match reply with
                | Outcome.Succeeded current ->
                    return
                        (ClaimCore.TestSupport.Submissions.createRaw
                            "case.erase"
                            (id fixture number)
                            (ClaimCore.Application.Contracts.OperationBinding.Case(
                                (target),
                                (Revision.value current.Snapshot.Revision),
                                None,
                                (Some(Sequence.value current.FamilySequence))
                            ))
                            (Map.empty))
                | other -> return failtestf "Review: %A" other
            }

        let invalidated number change =
            task {
                let! erase = review number
                let! _ = acceptedChange owner change
                let! stale = Service.executeSubmission owner erase token

                Expect.equal
                    stale.Outcome
                    (ExecuteResult.Rejected(Refusal.Case CaseRefusal.FamilyChanged))
                    "Every accepted child change invalidates the family review"
            }

        do!
            invalidated
                31
                ((ClaimCore.TestSupport.Submissions.createRaw)
                    "note.add"
                    (id fixture 32)
                    (ClaimCore.Application.Contracts.OperationBinding.Note((target), None))
                    (Map.ofList
                        [
                            TaskInputId.Target InputTarget.NoteText,
                            ClaimCore.Application.Contracts.SubmissionValue.Text("Retained note")
                        ]))

        do!
            invalidated
                33
                ((ClaimCore.TestSupport.Submissions.createRaw)
                    "note.remove"
                    (id fixture 34)
                    (ClaimCore.Application.Contracts.OperationBinding.Note(
                        (target),
                        Some((id fixture 32))
                    ))
                    (Map.empty))

        do!
            invalidated
                35
                ((ClaimCore.TestSupport.Submissions.createRaw)
                    "work.update"
                    (id fixture 36)
                    (ClaimCore.Application.Contracts.OperationBinding.Work((target), (0L)))
                    (ClaimCore.TestSupport.Submissions.work (
                        {
                            Responsible = WorkValue.Keep
                            Summary = WorkValue.Set "Retained work"
                        }
                    )))

        do!
            invalidated
                37
                ((ClaimCore.TestSupport.Submissions.createRaw)
                    "work.update"
                    (id fixture 38)
                    (ClaimCore.Application.Contracts.OperationBinding.Work((target), (1L)))
                    (ClaimCore.TestSupport.Submissions.work (
                        {
                            Responsible = WorkValue.Keep
                            Summary = WorkValue.Clear
                        }
                    )))

        let plan = Submissions.reminderPlan "Follow up" "2026-01-01"

        let reminder number taskName selected values =
            Submissions.createRaw
                taskName
                (id fixture number)
                (OperationBinding.Reminder((target), selected))
                values

        do! invalidated 39 (reminder (40) "reminder.create" (None) (plan))

        do!
            invalidated
                41
                (reminder
                    (42)
                    "reminder.update"
                    (Some((id fixture 40), (1L)))
                    (Submissions.reminderPlan "Updated" "2026-01-01"))

        do!
            invalidated
                43
                (reminder (44) "reminder.complete" (Some((id fixture 40), (2L))) (Map.empty))

        do!
            invalidated
                45
                (reminder
                    (46)
                    "reminder.reopen"
                    (Some((id fixture 40), (3L)))
                    (Map.ofList
                        [
                            TaskInputId.Target InputTarget.ReminderDueDate,
                            ClaimCore.Application.Contracts.SubmissionValue.Text("2026-01-02")
                        ]))

        do!
            invalidated
                47
                (reminder (48) "reminder.cancel" (Some((id fixture 40), (4L))) (Map.empty))

        do!
            invalidated
                49
                (reminder
                    (50)
                    "reminder.reopen"
                    (Some((id fixture 40), (5L)))
                    (Map.ofList
                        [
                            TaskInputId.Target InputTarget.ReminderDueDate,
                            ClaimCore.Application.Contracts.SubmissionValue.Text("2026-01-03")
                        ]))

        do!
            invalidated
                51
                (reminder (52) "reminder.completeAndPlanNext" (Some((id fixture 40), (6L))) (plan))

        let! erase = review 53

        let! _ =
            grant
                owner
                (id fixture 54)
                fixture.Owner
                {
                    Role = Role.Auditor
                    Scope = GrantTarget.Installation
                }
                true

        let! accepted = Service.executeSubmission owner erase token

        let erased =
            match accepted.Outcome with
            | ExecuteResult.Accepted receipt -> receipt
            | other -> failtestf "Unrelated People activity must not invalidate erasure: %A" other

        let! replayed = Service.executeSubmission owner erase token

        match replayed.Outcome with
        | ExecuteResult.AlreadyAccepted _ -> ()
        | other -> failtestf "Erased family exact retry resolves acceptance first: %A" other

        let changedBinding =
            match erase.Envelope.Binding with
            | OperationBinding.Case(target, revision, payment, _) ->
                OperationBinding.Case(target, revision, payment, Some 1L)
            | _ -> failtest "Erasure binding required"

        let changedBasis =
            Submissions.createRaw
                (TaskId.token erase.Task)
                erase.OperationId
                changedBinding
                erase.Envelope.Inputs

        let! changed = Service.executeSubmission owner changedBasis token

        Expect.equal
            changed.Outcome
            (ExecuteResult.Rejected Refusal.IdempotencyConflict)
            "Changed family basis cannot reuse accepted operation identity"

        let! _ = verified database
        do! enterMaintenance database
        let! checkpoint = LedgerAdministration.head database.OwnerSource token

        let! rebuilt =
            ProjectionRebuild.rebuild
                database.OwnerSource
                checkpoint
                (MaintenanceHandle.create
                    1L
                    (System.Guid.Parse "10000000-0000-4000-8000-000000000099")
                 |> Option.get)
                token

        Expect.isOk rebuilt "Erased family rebuilds without private contents"
        let! _ = verified database

        do!
            rewrite database erased.Position.Sequence (fun retained ->
                let binding =
                    match retained.Root.Structure.Binding with
                    | Binding.CaseErase(target, revision, _) ->
                        Binding.CaseErase(target, revision, CaseFixtures.sequence 1L)
                    | _ -> failtest "Erasure binding required"

                rehashed
                    retained.Root.Header
                    retained.Root.Structure.Task
                    binding
                    retained.Root.Structure.Retained
                    retained.Root.Structure.Effects
                    retained.Root.Structure.Compartments)


        do! finding VerificationFinding.StateSequence database
        return ()
    }

[<Tests>]
let tests =
    testList
        "reviewed mutation preconditions"
        [
            testCaseAsync
                "People revisions bind profiles access no-ops retries and erased replay"
                (run peopleRevisions)
            testCaseAsync
                "erasure review covers notes work and complete reminder lifecycle without global-head coupling"
                (run familyReview)
        ]
