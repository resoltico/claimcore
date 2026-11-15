module ClaimCore.IntegrationTests.ReviewedMutationTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private acceptedChange service input =
    task {
        let! reply = Service.execute service input token

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

        let people number expected act =
            OperationInput.People
                {
                    OperationId = id fixture number
                    ExpectedRevision = expected
                    Target = PrincipalKey.toInput principal
                    Act = act
                }

        let profile name =
            {
                ActorProfileInput.DisplayName = name
                Contact = None
            }

        let grant =
            {
                AuthoredGrant.Role = Role.CaseReader
                Scope = GrantTarget.Installation
            }

        let! _ =
            acceptedChange
                owner
                (people
                    10
                    0L
                    (PeopleAct.Admit
                        {
                            Profile = profile "Original"
                            Grants = [ grant ]
                        }))

        let state () =
            task {
                let! value =
                    fixture.Store.Read
                        {
                            Work = (fun tx -> tx.PeopleState principal)
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

        let first = people 11 1L (PeopleAct.SetProfile(profile "First owner"))
        let! profileChanged = acceptedChange owner first

        for stale in
            [
                people 12 1L (PeopleAct.SetProfile(profile "Second owner"))
                people 13 1L PeopleAct.EraseProfile
                people 14 1L PeopleAct.Disable
            ] do
            let! reply = Service.execute owner stale token

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

        let! _ = acceptedChange owner (people 15 2L PeopleAct.Disable)
        let! staleEnable = Service.execute owner (people 16 2L PeopleAct.Enable) token

        Expect.equal
            staleEnable.Outcome
            (ExecuteResult.Rejected(Refusal.People PeopleRefusal.ActorChanged))
            "Stale enable cannot undo a newer disable"

        let! _ = acceptedChange owner (people 17 3L PeopleAct.Enable)
        let! _ = acceptedChange owner (people 18 4L (PeopleAct.Grant grant))
        let! reaffirmed = state ()

        Expect.equal
            (Revision.value reaffirmed.Revision)
            5L
            "Deliberate reaffirmation advances exactly once"

        let! replay = Service.execute owner first token

        match replay.Outcome with
        | ExecuteResult.AlreadyAccepted _ -> ()
        | other -> failtestf "Accepted retry precedes stale target checks: %A" other

        let! afterReplay = state ()

        Expect.equal
            afterReplay.Revision
            reaffirmed.Revision
            "Exact retry appends and advances nothing"

        let changed = people 11 5L (PeopleAct.SetProfile(profile "First owner"))
        let! conflict = Service.execute owner changed token

        Expect.equal
            conflict.Outcome
            (ExecuteResult.Rejected Refusal.IdempotencyConflict)
            "Changed reviewed basis changes canonical identity"

        let! _ = acceptedChange owner (people 20 5L (PeopleAct.Revoke grant))
        let! staleGrant = Service.execute owner (people 21 5L (PeopleAct.Grant grant)) token

        Expect.equal
            staleGrant.Outcome
            (ExecuteResult.Rejected(Refusal.People PeopleRefusal.ActorChanged))
            "An old reviewed grant cannot undo a newer revocation"

        let! _ = acceptedChange owner (people 22 6L (PeopleAct.Grant grant))
        let! _ = acceptedChange owner (people 19 7L PeopleAct.EraseProfile)
        let! _ = verified database
        do! enterMaintenance database
        let! checkpoint = LedgerAdministration.head database.OwnerSource token
        let! rebuilt = ProjectionRebuild.rebuild database.OwnerSource checkpoint token
        Expect.isOk rebuilt "Erased profiles rebuild from retained revision metadata"
        let! _ = verified database

        do!
            rewrite database profileChanged.Position.Sequence (fun retained ->
                match retained.Values with
                | RetainedOperationValues.People RetainedProfile.Unavailable -> ()
                | other ->
                    failtestf "The negative control requires unavailable profile content: %A" other

                let command =
                    match retained.Root.Structure.Command with
                    | CommandSkeleton.People scope ->
                        CommandSkeleton.People
                            { scope with
                                ExpectedRevision = CaseFixtures.revision 2L
                            }
                    | _ -> failtest "Expected retained profile-bearing People root"

                rehashed
                    retained.Root.Header
                    command
                    retained.Root.Structure.Effects
                    retained.Root.Structure.Compartments)


        do! finding VerificationFinding.PeopleFold database
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
                (Inputs.caseOperation (
                    request fixture 30 "FAMILY-REVIEW" 0L (CaseAct.Open registration)
                ))

        let review number =
            task {
                let! reply = Service.get owner { Target = target } token

                match reply with
                | Outcome.Succeeded current ->
                    return
                        OperationInput.Case
                            {
                                OperationId = id fixture number
                                Target = target
                                ExpectedRevision = Revision.value current.Snapshot.Revision
                                ExpectedFamilySequence = Some(Sequence.value current.FamilySequence)
                                Act = CaseAct.Bare BareAct.Erase
                            }
                | other -> return failtestf "Review: %A" other
            }

        let invalidated number change =
            task {
                let! erase = review number
                let! _ = acceptedChange owner change
                let! stale = Service.execute owner erase token

                Expect.equal
                    stale.Outcome
                    (ExecuteResult.Rejected(Refusal.Case CaseRefusal.FamilyChanged))
                    "Every accepted child change invalidates the family review"
            }

        do!
            invalidated
                31
                (OperationInput.Note
                    {
                        OperationId = id fixture 32
                        Target = target
                        Act = AuthoredNoteAct.AddNote "Retained note"
                    })

        do!
            invalidated
                33
                (OperationInput.Note
                    {
                        OperationId = id fixture 34
                        Target = target
                        Act = AuthoredNoteAct.RemoveNote(id fixture 32)
                    })

        do!
            invalidated
                35
                (OperationInput.Work
                    {
                        OperationId = id fixture 36
                        Target = target
                        ExpectedRevision = 0L
                        Act =
                            WorkAct.UpdateWork
                                {
                                    Responsible = WorkValue.Keep
                                    Summary = WorkValue.Set "Retained work"
                                }
                    })

        do!
            invalidated
                37
                (OperationInput.Work
                    {
                        OperationId = id fixture 38
                        Target = target
                        ExpectedRevision = 1L
                        Act =
                            WorkAct.UpdateWork
                                {
                                    Responsible = WorkValue.Keep
                                    Summary = WorkValue.Clear
                                }
                    })

        let plan =
            {
                ReminderPlanInput.Action = "Follow up"
                Due = "2026-01-01"
            }

        let reminder number act =
            OperationInput.Reminder
                {
                    OperationId = id fixture number
                    Target = target
                    Act = act
                }

        do! invalidated 39 (reminder 40 (ReminderAct.Create plan))

        do!
            invalidated
                41
                (reminder
                    42
                    (ReminderAct.Update(id fixture 40, 1L, { plan with Action = "Updated" })))

        do! invalidated 43 (reminder 44 (ReminderAct.Complete(id fixture 40, 2L)))
        do! invalidated 45 (reminder 46 (ReminderAct.Reopen(id fixture 40, 3L, "2026-01-02")))
        do! invalidated 47 (reminder 48 (ReminderAct.Cancel(id fixture 40, 4L)))
        do! invalidated 49 (reminder 50 (ReminderAct.Reopen(id fixture 40, 5L, "2026-01-03")))
        do! invalidated 51 (reminder 52 (ReminderAct.CompleteAndPlanNext(id fixture 40, 6L, plan)))
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

        let! accepted = Service.execute owner erase token

        let erased =
            match accepted.Outcome with
            | ExecuteResult.Accepted receipt -> receipt
            | other -> failtestf "Unrelated People activity must not invalidate erasure: %A" other

        let! replayed = Service.execute owner erase token

        match replayed.Outcome with
        | ExecuteResult.AlreadyAccepted _ -> ()
        | other -> failtestf "Erased family exact retry resolves acceptance first: %A" other

        let changedBasis =
            match erase with
            | OperationInput.Case request ->
                OperationInput.Case
                    { request with
                        ExpectedFamilySequence = Some 1L
                    }
            | _ -> failtest "Erasure input"

        let! changed = Service.execute owner changedBasis token

        Expect.equal
            changed.Outcome
            (ExecuteResult.Rejected Refusal.IdempotencyConflict)
            "Changed family basis cannot reuse accepted operation identity"

        let! _ = verified database
        do! enterMaintenance database
        let! checkpoint = LedgerAdministration.head database.OwnerSource token
        let! rebuilt = ProjectionRebuild.rebuild database.OwnerSource checkpoint token
        Expect.isOk rebuilt "Erased family rebuilds without private contents"
        let! _ = verified database

        do!
            rewrite database erased.Position.Sequence (fun retained ->
                let command =
                    match retained.Root.Structure.Command with
                    | CommandSkeleton.Case scope ->
                        CommandSkeleton.Case
                            { scope with
                                ExpectedFamilySequence = Some(CaseFixtures.sequence 1L)
                            }
                    | _ -> failtest "Expected retained case erasure"

                OperationRoot.append
                    retained.Root.Header
                    command
                    retained.Root.Structure.Effects
                    retained.Root.Structure.Compartments
                |> Result.defaultWith (string >> failtest))

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
