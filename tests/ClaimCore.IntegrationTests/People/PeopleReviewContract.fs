module ClaimCore.TestSupport.PeopleReviewContract

open ClaimCore.TestSupport

open ClaimCore.Wire
open ClaimCore.Presentation
open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.TestSupport.StoreFixtures


let internal peopleReview fixture principal input =
    task {
        let! preview =
            Service.previewSubmission (core fixture principal) input CancellationToken.None

        let! caller = inspectPeople fixture principal CancellationToken.None

        return
            preview,
            caller
            |> Result.defaultWith (fun error -> failtestf "Current caller read failed: %A" error)
    }

let internal ledgerHead fixture =
    task {
        let mutable loaded = None

        let! result: Result<Verdict<unit>, StoreFailure> =
            fixture.Store.Write(
                {
                    WriteOperation.RequiresEncryption = false
                    WriteOperation.Work =
                        (fun tx ->
                            task {
                                loaded <- Some tx.Head

                                return
                                    Verdict.Refuse(
                                        Refusal.Access AccessRefusal.ResourceUnavailable
                                    )
                            })
                    CancellationToken = CancellationToken.None
                }
            )

        Expect.equal
            result
            (Ok(Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable)))
            "The head probe appends nothing."

        return loaded |> Option.get
    }

let internal peopleReviewRefusals fixture =
    task {
        let! actor = bootstrap fixture
        let! before = ledgerHead fixture

        for index, taskName, values, expected in
            [
                10,
                "people.register",
                Submissions.profile DomainInputs.actorProfile,
                Refusal.People PeopleRefusal.ActorAlreadyRegistered
                11, "people.disable", Map.empty, Refusal.People PeopleRefusal.LastOwner
                12,
                "people.revoke",
                Submissions.grant
                    {
                        Role = Role.Owner
                        Scope = GrantTarget.Installation
                    },
                Refusal.People PeopleRefusal.LastOwner
            ] do
            let! input =
                personSubmission fixture.Store (id fixture index) fixture.Owner None taskName values

            let! preview, _ = peopleReview fixture fixture.Owner input

            Expect.equal
                preview.Outcome
                (Outcome.Rejected expected)
                "Fresh people preview enforces the execution refusal."

            let! execution = (Service.executeSubmission actor ((input)) (CancellationToken.None))

            Expect.equal
                execution.Outcome
                (ExecuteResult.Rejected expected)
                "Locked execution preserves the same refusal."

        let! after = ledgerHead fixture
        Expect.equal after before "Refused previews and executions leave the ledger head unchanged."
    }

let internal peopleReadIsNotManage fixture =
    task {
        let! actor = bootstrap fixture

        let auditor =
            PrincipalKey.human "https://issuer.example/" "preview-auditor"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            ((task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (fixture.Store)
                        ((id fixture 2))
                        (auditor)
                        (None)
                        "people.register"
                        (ClaimCore.TestSupport.Submissions.profile (
                            ClaimCore.TestSupport.DomainInputs.actorProfile
                        ))

                return! Service.executeSubmission (actor) submission ((CancellationToken.None))
            }))

        acceptedPeople registered

        let! granted =
            grant
                actor
                (id fixture 3)
                auditor
                {
                    Role = Role.Auditor
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople granted

        let! input =
            personSubmission
                fixture.Store
                (id fixture 4)
                fixture.Owner
                None
                "people.disable"
                Map.empty

        let! preview, view = peopleReview fixture auditor input

        Expect.isTrue
            (Access.can auditor view Capability.ReadAccess Scope.Installation)
            "The rejecting control really has people read."

        Expect.equal
            preview.Outcome
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Read people cannot authorize mutation preview."

        let! execution =
            (Service.executeSubmission (core fixture auditor) ((input)) (CancellationToken.None))

        Expect.equal
            execution.Outcome
            (ExecuteResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Preview and execution conceal the same inaccessible operation."
    }

let internal peopleReviewReplay fixture =
    task {
        let! actor = bootstrap fixture

        let target =
            PrincipalKey.human "https://issuer.example/" "preview-target"
            |> Result.defaultWith (string >> failtest)

        let! input =
            personSubmission
                fixture.Store
                (id fixture 2)
                target
                (Some 0L)
                "people.register"
                (Submissions.profile DomainInputs.actorProfile)

        let! before = ledgerHead fixture
        let! preview, _ = peopleReview fixture fixture.Owner input

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed review) ->
            Expect.equal review.Target (ReviewTarget.Person None) "No accepted actor target exists"

            match review.Changes.Head.Description with
            | PartDescription.Person changes ->
                Expect.equal
                    changes.Enabled
                    (Some { Before = None; After = Some true })
                    "Creation begins absent"

                Expect.isNone changes.Profiles.Before "No prior private profile"

                Expect.equal
                    (changes.Profiles.After |> Option.map ActorProfile.authored)
                    (Some DomainInputs.actorProfile)
                    "Authored profile remains exact"

                Expect.isEmpty changes.Grants "Registration grants no roles"
            | _ -> failtest "Person description required"
        | other -> failtestf "Expected registration proposal: %A" other

        let! after = ledgerHead fixture
        Expect.equal after before "A successful preview appends no entry."

        let! execution = (Service.executeSubmission actor ((input)) (CancellationToken.None))

        let receipt = accepted execution

        Expect.equal
            receipt.Position.Sequence
            (Sequence.successor before.Sequence)
            "Execution accepts one root containing the registration effects."

        let! replay, _ = peopleReview fixture fixture.Owner input

        match replay.Outcome with
        | Outcome.Succeeded(Previewed.AlreadyAccepted actual) ->
            expectReceipt actual receipt "Accepted registration replays before fresh-state refusal."
        | other -> failtestf "Expected preview of retained acceptance: %A" other

        let! changed =
            personSubmission fixture.Store (id fixture 2) target None "people.disable" Map.empty

        let! conflict, _ = peopleReview fixture fixture.Owner changed

        Expect.equal
            conflict.Outcome
            (Outcome.Rejected Refusal.IdempotencyConflict)
            "Changed people content conflicts with its accepted identity."
    }

let internal grantReviewTarget fixture =
    task {
        let! actor = caseWorker fixture

        let! opened =
            (Service.executeSubmission
                actor
                (((request
                    (fixture)
                    (10)
                    ("REVIEW-CASE")
                    (0L)
                    "case.open"
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values (registration)
                    )))))
                (CancellationToken.None))

        accepted opened |> ignore

        let target =
            PrincipalKey.human "https://issuer.example/" "grant-preview-target"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            ((task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (fixture.Store)
                        ((id fixture 11))
                        (target)
                        (None)
                        "people.register"
                        (ClaimCore.TestSupport.Submissions.profile (
                            ClaimCore.TestSupport.DomainInputs.actorProfile
                        ))

                return! Service.executeSubmission (actor) submission ((CancellationToken.None))
            }))

        acceptedPeople registered

        let! installed =
            grant
                actor
                (id fixture 12)
                target
                {
                    Role = Role.CaseRegistrar
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople installed

        let! scoped =
            grant
                actor
                (id fixture 13)
                target
                {
                    Role = Role.CaseReader
                    Scope = (GrantTarget.CaseReference "REVIEW-CASE")
                }
                true

        acceptedPeople scoped

        let! input =
            personSubmission
                fixture.Store
                (id fixture 14)
                target
                None
                "people.revoke"
                (Submissions.grant
                    {
                        Role = Role.CaseReader
                        Scope = GrantTarget.CaseReference "review-case"
                    })

        return target, input
    }

let internal assertGrantRevocation fixture (review: OperationReview) =
    match review.Changes.Head.Description with
    | PartDescription.Person changes ->
        Expect.isNone changes.Enabled "A grant change does not invent an enabled transition"

        Expect.equal
            changes.Grants
            [
                {
                    Grant =
                        {
                            Role = Role.CaseReader
                            Scope = Scope.Case(CaseFixtures.caseId (id fixture 10))
                        }
                    Before = true
                    After = false
                }
            ]
            "Exactly the touched assignment is removed"

        Expect.equal
            (changes.Profiles.After |> Option.map ActorProfile.authored)
            (Some DomainInputs.actorProfile)
            "The current profile remains available"
    | _ -> failtest "Person description required"

    let target =
        match review.Target with
        | ReviewTarget.Person actor -> actor |> Option.map _.ActorId
        | _ -> None

    Expect.equal
        target
        (Some(CaseFixtures.actorId (id fixture 11)))
        "Existing actor identity remains exact"


let internal peopleGrantReview fixture =
    task {
        let! target, input = grantReviewTarget fixture
        let! before = ledgerHead fixture
        let! preview, _ = peopleReview fixture fixture.Owner input

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed review) -> assertGrantRevocation fixture review
        | _ -> failtest "Expected a case-grant revocation proposal."

        let! after = ledgerHead fixture
        Expect.equal after before "Grant preview writes neither ledger nor projections."

        let! read =
            fixture.Store.Read
                {
                    CancellationToken = CancellationToken.None
                    Work = fun tx -> PersonAcquisition.principal tx target
                }

        let person =
            read
            |> Result.defaultWith (fun _ -> failtest "Stored target read failed")
            |> Option.get

        Expect.equal
            (person.Grants |> Map.keys |> Set.ofSeq)
            (set
                [
                    {
                        Role = Role.CaseRegistrar
                        Scope = Scope.Installation
                    }
                    {
                        Role = Role.CaseReader
                        Scope = Scope.Case(CaseFixtures.caseId (id fixture 10))
                    }
                ])
            "Preview changes neither the touched nor inherited grants"
    }

let internal peopleReviewRechecks fixture =
    task {
        let! actor = bootstrap fixture

        let other =
            PrincipalKey.human "https://issuer.example/" "other-preview-owner"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            ((task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (fixture.Store)
                        ((id fixture 2))
                        (other)
                        (None)
                        "people.register"
                        (ClaimCore.TestSupport.Submissions.profile (
                            ClaimCore.TestSupport.DomainInputs.actorProfile
                        ))

                return! Service.executeSubmission (actor) submission ((CancellationToken.None))
            }))

        acceptedPeople registered

        let! granted =
            grant
                actor
                (id fixture 3)
                other
                {
                    Role = Role.Owner
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople granted

        let! input =
            personSubmission
                fixture.Store
                (id fixture 4)
                fixture.Owner
                None
                "people.disable"
                Map.empty

        let! preview, view = peopleReview fixture fixture.Owner input

        Expect.isTrue
            (Access.can fixture.Owner view Capability.ManageAccess Scope.Installation)
            "The preview's requester can manage access"

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed _) -> ()
        | _ -> failtest "Disabling one of two owners is initially permitted."

        let! revoked =
            grant
                actor
                (id fixture 5)
                other
                {
                    Role = Role.Owner
                    Scope = GrantTarget.Installation
                }
                false

        acceptedPeople revoked
        let! before = ledgerHead fixture

        let! execution = (Service.executeSubmission actor ((input)) (CancellationToken.None))

        Expect.equal
            execution.Outcome
            (ExecuteResult.Rejected(Refusal.People PeopleRefusal.LastOwner))
            "Execution decides again against its locked people state."

        let! after = ledgerHead fixture
        Expect.equal after before "Stale preview cannot authorize a write."
    }

let internal meaningfulScopeAdmission fixture =
    task {
        let! actor = caseWorker fixture

        let! opened =
            Service.executeSubmission
                actor
                (((request
                    (fixture)
                    (20)
                    ("SCOPE-ADMISSION")
                    (0L)
                    "case.open"
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values (registration)
                    )))))
                CancellationToken.None

        accepted opened |> ignore

        let target =
            PrincipalKey.human "https://issuer.example/" "scope-target"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            (task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (fixture.Store)
                        ((id fixture 21))
                        (target)
                        (None)
                        "people.register"
                        (ClaimCore.TestSupport.Submissions.profile (
                            ClaimCore.TestSupport.DomainInputs.actorProfile
                        ))

                return! Service.executeSubmission (actor) submission (CancellationToken.None)
            })

        acceptedPeople registered
        let! before = ledgerHead fixture

        let! refused =
            grant
                actor
                (id fixture 22)
                target
                {
                    Role = Role.Owner
                    Scope = GrantTarget.CaseReference "SCOPE-ADMISSION"
                }
                true

        Expect.equal
            refused.Outcome
            (ExecuteResult.Rejected(Refusal.People PeopleRefusal.RoleScopeNotApplicable))
            "A meaningless grant refuses through the service"

        let! after = ledgerHead fixture
        Expect.equal after before "Refusal appends no ledger entry"

        let! installed =
            grant
                actor
                (id fixture 23)
                target
                {
                    Role = Role.Owner
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople installed

        let! disabled =
            (task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (fixture.Store)
                        ((id fixture 24))
                        (target)
                        (None)
                        "people.disable"
                        (Map.empty)

                return! Service.executeSubmission (actor) submission (CancellationToken.None)
            })

        acceptedPeople disabled

        for operation, role in [ 25, Role.CaseHandler; 26, Role.CaseReader ] do
            let! result =
                grant
                    actor
                    (id fixture operation)
                    target
                    {
                        Role = role
                        Scope = GrantTarget.CaseReference "SCOPE-ADMISSION"
                    }
                    true

            acceptedPeople result
    }
