module ClaimCore.TestSupport.ScopedPeopleContract

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
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.TestSupport.StoreFixtures

open ClaimCore.TestSupport.StoreContractReads

let internal peopleChanges fixture =
    task {
        let! owner = bootstrap fixture

        let target =
            PrincipalKey.human "https://issuer.example/" "reader"
            |> Result.defaultWith (string >> failtest)

        do! registerReader fixture owner target
        let! read = inspectPeople fixture (target) (CancellationToken.None)

        match read with
        | Ok({ Actor = Some actor } as people) ->
            Expect.equal
                actor.ActorId
                ((CaseFixtures.actorId (id fixture 2)))
                "Registration identity"

            Expect.equal people.Installation [ Role.CaseReader ] "Added grant"
        | _ -> failtest "Missing actor"

        let! removed =
            grant
                owner
                (id fixture 4)
                target
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
                false

        acceptedPeople removed

        let! disabled =
            ((task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (fixture.Store)
                        ((id fixture 5))
                        (target)
                        (None)
                        "people.disable"
                        (Map.empty)

                return! Service.executeSubmission (owner) submission ((CancellationToken.None))
            }))

        acceptedPeople disabled
        let! read = inspectPeople fixture (target) (CancellationToken.None)

        match read with
        | Ok({ Actor = Some actor } as people) ->
            Expect.isFalse actor.Enabled "Disabled"
            Expect.isEmpty people.Installation "Removed"
        | _ -> failtest "Missing actor"
    }

let internal scopedHandler fixture (admin: ServiceContext) reference =
    task {
        let target =
            PrincipalKey.service "https://issuer.example/" "scoped-handler"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            ((task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (fixture.Store)
                        ((id fixture 30))
                        (target)
                        (None)
                        "people.register"
                        (ClaimCore.TestSupport.Submissions.profile (
                            ClaimCore.TestSupport.DomainInputs.actorProfile
                        ))

                return! Service.executeSubmission (admin) submission ((CancellationToken.None))
            }))

        acceptedPeople registered

        let! granted =
            grant
                admin
                (id fixture 31)
                target
                {
                    Role = Role.CaseHandler
                    Scope = ((caseGrant fixture reference))
                }
                true

        acceptedPeople granted
        return target, core fixture target
    }

let internal checkScopedAdvice fixture (actor: ServiceContext) reference =
    task {
        let! result =
            (Service.get
                actor
                ({
                    Target = (caseTarget fixture reference)
                })
                (CancellationToken.None))

        match result with
        | Outcome.Succeeded value ->
            Expect.equal
                value.Advice.History
                HistoryAccess.Full
                "Case-scoped full history is advised"

            let _correction =
                value.Advice.Correction
                |> Option.defaultWith (fun () -> failtest "Correct needs choices")

            Expect.contains
                value.Advice.Acts
                (TaskId.ofLiteral "case.correct")
                "Correction is explicitly offered"

            Expect.equal
                value.Snapshot.Revision
                (CaseFixtures.revision (2L))
                "The snapshot owns the expected revision"

        | _ -> failtest "Expected readable scoped case"
    }

let internal advisedCase fixture reference =
    task {
        let! admin = caseWorker fixture

        let! opened =
            (Service.executeSubmission
                admin
                (((request
                    (fixture)
                    (20)
                    (reference)
                    (0L)
                    "case.open"
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values (registration)
                    )))))
                (CancellationToken.None))

        accepted opened |> ignore

        let decision =
            (ClaimCore.TestSupport.DomainInputs.decisionFrom
                [
                    BusinessField.PaymentDecisionDate, ("2026-08-15")
                    BusinessField.PayableAmount, ("750.0000")
                    BusinessField.PayableCurrency, ("EUR")
                ])

        let! decided =
            (Service.executeSubmission
                admin
                (((request
                    (fixture)
                    (21)
                    (reference)
                    (1L)
                    "case.decide"
                    (ClaimCore.TestSupport.Submissions.fields (DecisionInput.values (decision))))))
                (CancellationToken.None))

        accepted decided |> ignore
        return admin
    }

let internal refusedAdvice fixture (actor: ServiceContext) reference pending =
    task {
        let! refused = (Service.executeSubmission actor ((pending)) (CancellationToken.None))

        Expect.equal
            refused.Outcome
            (ExecuteResult.Rejected(Refusal.Access(AccessRefusal.NotAuthorized Capability.Register)))
            "Revocation is decided again under lock"

        let! current =
            (Service.get
                actor
                ({
                    Target = (caseTarget fixture reference)
                })
                (CancellationToken.None))

        match current with
        | Outcome.Succeeded value ->
            Expect.equal
                value.Snapshot.Revision
                (CaseFixtures.revision (2L))
                "Refusal preserves the projection"

            Expect.equal
                value.Advice.History
                HistoryAccess.Summary
                "Fresh advice reflects revoked full history"

            Expect.isFalse
                (List.contains (TaskId.ofLiteral "case.correct") value.Advice.Acts)
                "No stale act offer"
        | _ -> failtest "Reader remains authorized"

        let! entries = caseTimeline fixture reference None 50

        Expect.equal
            (storedPage entries).Entries.Length
            2
            "Revoked execution appends no case acceptance"
    }

let internal revokedAdvice fixture =
    task {
        let reference = "SCOPED-ADVICE"
        let! admin = advisedCase fixture reference
        let! target, actor = scopedHandler fixture admin reference
        do! checkScopedAdvice fixture actor reference

        let correction =
            Submissions.updates (
                RegistrationUpdateInput.values (
                    DomainInputs.registrationUpdate (
                        DomainInputs.registrationWith
                            [ BusinessField.ClaimedAmount, "1001.0000" ]
                            registration
                    )
                )
            )
            |> Map.add
                (Submissions.input (InputTarget.Group RecordGroup.Registration))
                (SubmissionValue.Text "UPDATE")
            |> Map.add
                (Submissions.input (InputTarget.Group RecordGroup.Decision))
                (SubmissionValue.Text "KEEP")

        let pending = request fixture 35 reference 2L "case.correct" correction

        let! preview = (Service.previewSubmission actor ((pending)) (CancellationToken.None))

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed _) -> ()
        | _ -> failtest "Expected authoritative correction review"

        let! removed =
            grant
                admin
                (id fixture 32)
                target
                {
                    Role = Role.CaseHandler
                    Scope = ((caseGrant fixture reference))
                }
                false

        acceptedPeople removed

        let! reader =
            grant
                admin
                (id fixture 33)
                target
                {
                    Role = Role.CaseReader
                    Scope = ((caseGrant fixture reference))
                }
                true

        acceptedPeople reader
        do! refusedAdvice fixture actor reference pending
    }

let internal scopedWorker fixture =
    task {
        let! admin = caseWorker fixture

        let principal =
            PrincipalKey.human "https://issuer.example/" "many-scopes"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            ((task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (fixture.Store)
                        ((id fixture 20))
                        (principal)
                        (None)
                        "people.register"
                        (ClaimCore.TestSupport.Submissions.profile (
                            ClaimCore.TestSupport.DomainInputs.actorProfile
                        ))

                return! Service.executeSubmission (admin) submission ((CancellationToken.None))
            }))

        acceptedPeople registered

        let! installed =
            grant
                admin
                (id fixture 21)
                principal
                {
                    Role = Role.CaseRegistrar
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople installed

        for number in 1..12 do
            let reference = $"SCOPE-{number}"

            let! opened =
                (Service.executeSubmission
                    admin
                    (((request
                        (fixture)
                        ((100 + number))
                        (reference)
                        (0L)
                        "case.open"
                        (ClaimCore.TestSupport.Submissions.fields (
                            RegistrationInput.values (registration)
                        )))))
                    (CancellationToken.None))

            accepted opened |> ignore

            let! granted =
                grant
                    admin
                    (id fixture (200 + number))
                    principal
                    {
                        Role = Role.CaseReader
                        Scope = ((caseGrant fixture reference))
                    }
                    true

            acceptedPeople granted

        return principal
    }

let internal scopedPeople fixture =
    task {
        let inspectGlobal principal =
            fixture.Store.Read
                {
                    Work =
                        fun tx ->
                            task {
                                let! facts = AccessReads.globalReadFacts tx principal in
                                return facts.People
                            }
                    CancellationToken = CancellationToken.None
                }

        let! principal = scopedWorker fixture

        let! selected = existingCaseId fixture "SCOPE-1"
        let! acceptedScope = existingCaseId fixture "SCOPE-2"

        let readScopes ids =
            fixture.Store.Read
                {
                    Work = fun tx -> AccessReads.accessCases tx (principal, ids)
                    CancellationToken = CancellationToken.None
                }

        let! read = readScopes [ selected ]
        let view = read |> Result.defaultWith (fun _ -> failtest "Scoped case read")
        Expect.equal view.Installation [ Role.CaseRegistrar ] "Installation grants remain available"
        Expect.equal view.Cases.Count 1 "Unrelated case grants are not read"
        let! identity = readScopes [ selected; acceptedScope ]
        let identity = identity |> Result.defaultWith (fun _ -> failtest "Identity scopes")
        Expect.equal identity.Cases.Count 2 "Requested and accepted scopes can be read together"
        let! observed = readScopes [ acceptedScope ]
        let observed = observed |> Result.defaultWith (fun _ -> failtest "Observe scopes")
        Expect.equal observed.Cases.Count 1 "Observation reads only the selected accepted scope"
        let! context = inspectPeople fixture (principal) (CancellationToken.None)
        let context = context |> Result.defaultWith (fun _ -> failtest "Context read")
        Expect.isEmpty context.Cases "Context never enumerates assigned grants"
        Expect.isTrue context.HasScopedCases "EXISTS reports assigned visibility"

        let admin = core fixture fixture.Owner

        let unknown =
            PrincipalKey.human "https://issuer.example/" "unknown-global-reader"
            |> Result.defaultWith (string >> failtest)

        let! missing = inspectGlobal unknown

        Expect.equal
            (storedPage missing)
            AccessView.empty
            "A missing principal remains a genuinely absent actor"

        let roleless =
            PrincipalKey.human "https://issuer.example/" "roleless-global-reader"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            (task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (fixture.Store)
                        ((id fixture 430))
                        (roleless)
                        (None)
                        "people.register"
                        (ClaimCore.TestSupport.Submissions.profile (
                            ClaimCore.TestSupport.DomainInputs.actorProfile
                        ))

                return! Service.executeSubmission (admin) submission (CancellationToken.None)
            })

        acceptedPeople registered
        let! noGrants = inspectGlobal roleless
        let noGrants = storedPage noGrants
        Expect.isSome noGrants.Actor "A zero-grant actor is retained by the left join"

        Expect.equal
            (noGrants.Installation, noGrants.Cases, noGrants.HasScopedCases)
            ([], Map.empty, false)
            "A zero-grant actor has no invented visibility"

        for operation, taskName, enabled in
            [ 420, "people.disable", false; 421, "people.enable", true ] do
            let! submission =
                personSubmission
                    fixture.Store
                    (id fixture operation)
                    principal
                    None
                    taskName
                    Map.empty

            let! changed = Service.executeSubmission admin submission CancellationToken.None

            acceptedPeople changed
            let! observed = inspectGlobal principal
            let observed = storedPage observed

            Expect.equal
                (observed.Actor |> Option.map _.Enabled)
                (Some enabled)
                "Current actor availability remains independently reported"

            Expect.isTrue
                observed.HasScopedCases
                "Scoped presence is a fact even while the actor is disabled"

            Expect.isEmpty observed.Cases "Global people does not enumerate scoped grants"

        let service =
            PrincipalKey.service "https://issuer.example/" "service-global-reader"
            |> Result.defaultWith (string >> failtest)

        let! registeredService =
            (task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (fixture.Store)
                        ((id fixture 440))
                        (service)
                        (None)
                        "people.register"
                        (ClaimCore.TestSupport.Submissions.profile (
                            ClaimCore.TestSupport.DomainInputs.actorProfile
                        ))

                return! Service.executeSubmission (admin) submission (CancellationToken.None)
            })

        acceptedPeople registeredService

        let! grantedService =
            grant
                admin
                (id fixture 441)
                service
                {
                    Role = Role.CaseReader
                    Scope = (caseGrant fixture "SCOPE-1")
                }
                true

        acceptedPeople grantedService
        let! serviceContext = inspectGlobal service
        let serviceContext = storedPage serviceContext
        Expect.isTrue serviceContext.HasScopedCases "A service's valid scoped read remains visible"
        Expect.isEmpty serviceContext.Installation "A scoped read is not an installation grant"

        Expect.isEmpty
            serviceContext.Cases
            "Service global context does not enumerate case identities"

        for operation, role in [ 442, Role.CaseHandler; 443, Role.PaymentRecorder ] do
            let! granted =
                grant
                    admin
                    (id fixture operation)
                    service
                    {
                        Role = role
                        Scope = GrantTarget.Installation
                    }
                    true

            acceptedPeople granted

        let! installedService = inspectGlobal service
        let installedService = storedPage installedService

        Expect.equal
            (List.sort installedService.Installation)
            (List.sort [ Role.CaseHandler; Role.PaymentRecorder ])
            "Global facts retain every installation grant without enumerating scoped grants"

        Expect.isEmpty installedService.Cases "Installation joins do not load case grants"

        Expect.isTrue
            installedService.HasScopedCases
            "Scoped presence remains independent of installation grants"

        let! revoked =
            grant
                admin
                (id fixture 444)
                service
                {
                    Role = Role.CaseReader
                    Scope = (caseGrant fixture "SCOPE-1")
                }
                false

        acceptedPeople revoked
        let! installationOnly = inspectGlobal service
        let installationOnly = storedPage installationOnly

        Expect.equal
            (List.sort installationOnly.Installation)
            (List.sort installedService.Installation)
            "Removing the last scoped grant preserves both installation grants"

        Expect.isFalse
            installationOnly.HasScopedCases
            "Installation visibility does not invent scoped presence"

        Expect.isEmpty installationOnly.Cases "A global view never enumerates case grants"

        let pending =
            request
                (fixture)
                (102)
                ("SCOPE-1")
                (1L)
                "case.amendRegistration"
                (ClaimCore.TestSupport.Submissions.updates (
                    RegistrationUpdateInput.values ((DomainInputs.registrationUpdate registration))
                ))

        let! conflict =
            (Service.executeSubmission
                (Service.create
                    BuildIdentity.current.Version
                    fixture.InstallationId
                    fixture.Store
                    fixture.Calendar
                    principal)
                ((pending))
                (CancellationToken.None))

        Expect.equal
            conflict.Outcome
            (ExecuteResult.Rejected Refusal.IdempotencyConflict)
            "Both scopes preserve content exclusion"
    }
