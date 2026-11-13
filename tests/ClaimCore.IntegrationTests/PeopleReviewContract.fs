module ClaimCore.TestSupport.PeopleReviewContract

open ClaimCore.TestSupport

open ClaimCore.Wire
open ClaimCore.Presentation
open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures

open ClaimCore.TestSupport.StoreContractReads

let internal peopleReview fixture principal input =
    task {
        let! preview =
            (Service.preview
                (core fixture principal)
                (OperationInput.People input)
                (CancellationToken.None))

        let request =
            People.parse input
            |> Result.defaultWith (fun _ -> failtest "Synthetic people input must admit.")

        let! read =
            inspectTarget
                fixture
                (principal)
                {
                    Needed = People.factsNeeded request.Act
                    OperationId = (OperationId.value (request.OperationId))
                    Target = request.Target
                    CaseTargets = PeopleRequests.targetCases request
                }
                (CancellationToken.None)

        let view =
            read |> Result.defaultWith (fun _ -> failtest "People snapshot must load.")

        return preview, view
    }

let internal ledgerHead fixture =
    task {
        let mutable loaded = None

        let! result: Result<Verdict<unit>, StoreFailure> =
            fixture.Store.Write(
                {
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

        for index, act, expected in
            [
                10,
                (PeopleAct.Register ClaimCore.TestSupport.Inputs.actorProfile),
                (Refusal.People PeopleRefusal.ActorAlreadyRegistered)
                11, PeopleAct.Disable, (Refusal.People PeopleRefusal.LastOwner)
                12,
                PeopleAct.Revoke
                    {
                        AuthoredGrant.Role = Role.Owner
                        Scope = GrantTarget.Installation
                    },
                (Refusal.People PeopleRefusal.LastOwner)
            ] do
            let input = peopleRequest (id fixture index) fixture.Owner act
            let! preview, _ = peopleReview fixture fixture.Owner input

            Expect.equal
                preview.Outcome
                (Outcome.Rejected expected)
                "Fresh people preview enforces the execution refusal."

            let! execution =
                (Service.execute actor (OperationInput.People input) (CancellationToken.None))

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
            (Service.execute
                actor
                (OperationInput.People(
                    peopleRequest
                        (id fixture 2)
                        auditor
                        (PeopleAct.Register ClaimCore.TestSupport.Inputs.actorProfile)
                ))
                (CancellationToken.None))

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

        let input = peopleRequest (id fixture 4) fixture.Owner (PeopleAct.Disable)

        let! preview, view = peopleReview fixture auditor input

        Expect.isTrue
            (Access.can auditor view.Actor Capability.ReadAccess Scope.Installation)
            "The rejecting control really has people read."

        Expect.equal
            preview.Outcome
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Read people cannot authorize mutation preview."

        let! execution =
            (Service.execute
                (core fixture auditor)
                (OperationInput.People input)
                (CancellationToken.None))

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

        let input =
            peopleRequest
                (id fixture 2)
                target
                (PeopleAct.Register ClaimCore.TestSupport.Inputs.actorProfile)

        let! before = ledgerHead fixture
        let! preview, _ = peopleReview fixture fixture.Owner input

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed(OperationReview.People review)) ->
            Expect.isNone review.Target "The unregistered principal has no current actor."

            Expect.equal
                review.Before
                {
                    Enabled = false
                    Profile = None
                    Grants = []
                    Access = []
                }
                "Unregistered before state."

            Expect.equal
                review.After
                {
                    Enabled = true
                    Profile =
                        Some(
                            ActorProfile.parse Inputs.actorProfile
                            |> Result.defaultWith (string >> failtest)
                        )
                    Grants = []
                    Access = []
                }
                "Registration proposes an enabled actor without grants."
        | _ -> failtest "Expected an people proposal."

        let! after = ledgerHead fixture
        Expect.equal after before "A successful preview appends no entry."

        let! execution =
            (Service.execute actor (OperationInput.People input) (CancellationToken.None))

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

        let changed = peopleRequest (id fixture 2) target (PeopleAct.Disable)
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
            (Service.execute
                actor
                (ClaimCore.TestSupport.Inputs.caseOperation (
                    request fixture 10 "REVIEW-CASE" 0L (AuthoredCaseAct.Open registration)
                ))
                (CancellationToken.None))

        accepted opened |> ignore

        let target =
            PrincipalKey.human "https://issuer.example/" "grant-preview-target"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            (Service.execute
                actor
                (OperationInput.People(
                    peopleRequest
                        (id fixture 11)
                        target
                        (PeopleAct.Register ClaimCore.TestSupport.Inputs.actorProfile)
                ))
                (CancellationToken.None))

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

        let input =
            peopleRequest
                (id fixture 14)
                target
                (PeopleAct.Revoke
                    {
                        AuthoredGrant.Role = Role.CaseReader
                        Scope = GrantTarget.CaseReference "review-case"
                    })

        return target, input
    }

let internal assertGrantRevocation fixture (review: PeopleReview) =
    Expect.isTrue review.Before.Enabled "Grant changes preserve actor enabled state."

    Expect.equal
        review.Before.Grants
        [
            {
                Role = Role.CaseReader
                Scope =
                    GrantScope.Case(
                        CaseFixtures.caseId (id fixture 10),
                        CaseFixtures.reference "REVIEW-CASE"
                    )
            }
        ]
        "Only the touched grant is shown, with the authoritative reference spelling."

    Expect.equal
        review.After
        {
            Enabled = true
            Profile =
                Some(
                    ActorProfile.parse Inputs.actorProfile
                    |> Result.defaultWith (string >> failtest)
                )
            Grants = []
            Access =
                [ Capability.ReadCase; Capability.ReadHistory; Capability.Register ]
                |> List.map (fun capability ->
                    {
                        Capability = capability
                        DirectRoles = []
                        InstallationRoles = [ Role.CaseRegistrar ]
                        Effective = true
                    })
        }
        "The revoked grant is absent, while inherited registrar capabilities remain."

    Expect.equal
        (review.Target |> Option.map _.Actor.ActorId)
        (Some((CaseFixtures.actorId (id fixture 11))))
        "Review identifies the existing target actor."

let internal peopleGrantReview fixture =
    task {
        let! target, input = grantReviewTarget fixture
        let! before = ledgerHead fixture
        let! preview, _ = peopleReview fixture fixture.Owner input

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed(OperationReview.People review)) ->
            assertGrantRevocation fixture review
        | _ -> failtest "Expected a case-grant revocation proposal."

        let! after = ledgerHead fixture
        Expect.equal after before "Grant preview writes neither ledger nor projections."

        let! targetRead =
            inspectTarget
                fixture
                (fixture.Owner)
                {
                    Needed = []
                    OperationId = id fixture 14
                    Target = target
                    CaseTargets = [ GrantTarget.CaseReference "REVIEW-CASE" ]
                }
                (CancellationToken.None)

        let targetRead =
            targetRead
            |> Result.defaultWith (fun _ -> failtest "Scoped people must remain readable.")

        Expect.equal
            targetRead.Target.Installation
            [ Role.CaseRegistrar ]
            "Unrelated installation grants are untouched."

        Expect.equal
            (targetRead.Target.Cases
             |> Map.values
             |> Seq.collect (fun roles -> roles)
             |> Seq.toList)
            [ Role.CaseReader ]
            "The proposed revocation has not changed the stored case grant."
    }

let internal peopleReviewRechecks fixture =
    task {
        let! actor = bootstrap fixture

        let other =
            PrincipalKey.human "https://issuer.example/" "other-preview-owner"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            (Service.execute
                actor
                (OperationInput.People(
                    peopleRequest
                        (id fixture 2)
                        other
                        (PeopleAct.Register ClaimCore.TestSupport.Inputs.actorProfile)
                ))
                (CancellationToken.None))

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

        let input = peopleRequest (id fixture 4) fixture.Owner (PeopleAct.Disable)

        let! preview, view = peopleReview fixture fixture.Owner input
        Expect.equal view.EnabledHumanOwners (Some 2) "The preview sees two enabled owners."

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed(OperationReview.People _)) -> ()
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

        let! execution =
            (Service.execute actor (OperationInput.People input) (CancellationToken.None))

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
            Service.execute
                actor
                (Inputs.caseOperation (
                    request fixture 20 "SCOPE-ADMISSION" 0L (AuthoredCaseAct.Open registration)
                ))
                CancellationToken.None

        accepted opened |> ignore

        let target =
            PrincipalKey.human "https://issuer.example/" "scope-target"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            Service.execute
                actor
                (OperationInput.People(
                    peopleRequest
                        (id fixture 21)
                        target
                        (PeopleAct.Register ClaimCore.TestSupport.Inputs.actorProfile)
                ))
                CancellationToken.None

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
            Service.execute
                actor
                (OperationInput.People(
                    peopleRequest (id fixture 24) target PeopleAct.Disable
                ))
                CancellationToken.None

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
