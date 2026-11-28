module ClaimCore.IntegrationTests.DiagnosticEvidence

open ClaimCore.Wire
open ClaimCore.Presentation

open ClaimCore.TestSupport

open ClaimCore.Json

open System
open System.Threading
open System.Text.Json
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.ApplicationFixtures


open ClaimCore.IntegrationTests.DiagnosticWitnesses

let internal scalarEvidence () =
    task {
        let! _, fixture = fixture ()

        use _owned =
            { new System.IDisposable with
                member _.Dispose() = fixture.Dispose()
            }

        let! actor = caseWorker fixture
        let results = ResizeArray<_>()

        for index, request in scalarRequests fixture () |> List.indexed do
            // Distinct witnesses reserve distinct operation identities, including known business refusals.
            let pending =
                if request.OperationId = Guid.Empty then
                    request
                else
                    ClaimCore.TestSupport.Submissions.withOperationId
                        (id fixture (100 + index))
                        (request)

            let! outcome = execute actor pending
            results.Add(refused outcome)

        // Malformed existing-case acts now require the authoritative readable snapshot and capability.
        let! scalarCase = execute actor (openRequest fixture 480 "REACH-1")
        accepted scalarCase |> ignore

        let! scalarSteward =
            grant
                actor
                (id fixture 481)
                owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople scalarSteward

        for index, request in List.indexed (progressRequests fixture ()) do
            let! outcome =
                execute actor (Submissions.withOperationId (id fixture (482 + index)) request)

            results.Add(refused outcome)

        let! noErasureReview =
            execute
                actor
                (Submissions.createRaw
                    "case.erase"
                    (id fixture 489)
                    (OperationBinding.Case(caseTarget fixture "REACH-1", 1L, None, None))
                    Map.empty)

        results.Add(refused noErasureReview)

        let! workOpening = execute actor (openRequest fixture 490 "REACH-WORK")
        accepted workOpening |> ignore

        for request in workScalarRequests fixture () do
            let! outcome = Service.executeSubmission actor request ct
            results.Add(refused outcome)

        let target = caseTarget fixture "REACH-WORK"

        let work operation responsible summary =
            (ClaimCore.TestSupport.Submissions.createRaw)
                "work.update"
                (id fixture operation)
                (ClaimCore.Application.Contracts.OperationBinding.Work((target), (0L)))
                (ClaimCore.TestSupport.Submissions.work (
                    {
                        Responsible = responsible
                        Summary = summary
                    }
                ))

        let reminder operation taskName selected values =
            Submissions.createRaw
                taskName
                (id fixture operation)
                (OperationBinding.Reminder((target), selected))
                values

        for request in
            [
                work 509 WorkValue.Keep WorkValue.Clear
                work
                    510
                    (WorkValue.Set(ActorId.format (CaseFixtures.actorId (id fixture 999))))
                    WorkValue.Keep
            ] do
            let! outcome = Service.executeSubmission actor request ct
            results.Add(refused outcome)

        let plan = Submissions.reminderPlan "Request report" "2027-01-01"

        let! planned =
            Service.executeSubmission actor (reminder (520) "reminder.create" (None) plan) ct

        accepted planned |> ignore

        for request in
            [
                reminder
                    (521)
                    "reminder.reopen"
                    (Some((id fixture 520), (1L)))
                    (Map.ofList
                        [
                            TaskInputId.Target InputTarget.ReminderDueDate,
                            ClaimCore.Application.Contracts.SubmissionValue.Text("2027-01-01")
                        ])
                reminder (522) "reminder.update" (Some((id fixture 520), (1L))) plan
            ] do
            let! outcome = Service.executeSubmission actor request ct
            results.Add(refused outcome)

        let! completed =
            Service.executeSubmission
                actor
                (reminder (523) "reminder.complete" (Some((id fixture 520), (1L))) (Map.empty))
                ct

        accepted completed |> ignore

        let! repeated =
            Service.executeSubmission
                actor
                (reminder (524) "reminder.complete" (Some((id fixture 520), (2L))) (Map.empty))
                ct

        results.Add(refused repeated)

        let! closed =
            execute
                actor
                (request
                    (fixture)
                    (525)
                    ("REACH-WORK")
                    (1L)
                    "case.close"
                    (CaseTransitionInputs.closed))

        accepted closed |> ignore

        let! inactive =
            Service.executeSubmission actor (reminder (526) "reminder.create" (None) plan) ct

        results.Add(refused inactive)

        let! stewardship =
            grant
                actor
                (id fixture 527)
                owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople stewardship

        let! voided =
            execute
                actor
                (request
                    (fixture)
                    (528)
                    ("REACH-WORK")
                    (2L)
                    "case.void"
                    (Map.ofList
                        [
                            TaskInputId.Target InputTarget.Reason,
                            ClaimCore.Application.Contracts.SubmissionValue.Text(
                                "Synthetic diagnostic state"
                            )
                        ]))

        accepted voided |> ignore

        let! blocked =
            Service.executeSubmission actor (reminder (529) "reminder.create" (None) plan) ct

        results.Add(refused blocked)
        return List.ofSeq results
    }


let internal stateScenarios () =
    let decided = "case.decide", Submissions.fields (DecisionInput.values decision)
    let closed = "case.close", CaseTransitionInputs.closed

    let voided =
        "case.void",
        Map.ofList
            [
                Submissions.input InputTarget.Reason, SubmissionValue.Text "Synthetic reason"
            ]

    let payment =
        "case.recordPayment",
        Submissions.fields (
            PaymentInput.values (
                DomainInputs.paymentFrom [ BusinessField.PaymentDate, "2026-08-20" ]
            )
        )

    let correct registrationChoice decisionChoice fields =
        "case.correct",
        fields
        |> Map.add
            (Submissions.input (InputTarget.Group RecordGroup.Registration))
            (SubmissionValue.Text registrationChoice)
        |> Map.add
            (Submissions.input (InputTarget.Group RecordGroup.Decision))
            (SubmissionValue.Text decisionChoice)

    let registration =
        Submissions.updates (
            RegistrationUpdateInput.values (DomainInputs.registrationUpdate registration)
        )

    let decisionUpdate value =
        Submissions.updates (DecisionUpdateInput.values (DomainInputs.decisionUpdate value))

    [
        [], ("case.reopen", CaseTransitionInputs.reopened)
        [ closed ], closed
        [ closed ], decided
        [ decided ], ("case.amendRegistration", registration)
        [ decided ], correct "UPDATE" "KEEP" registration
        [ closed ], correct "KEEP" "CLEAR" Map.empty
        [ voided ], payment
        [],
        ("case.removePayment",
         Map.ofList
             [
                 Submissions.input InputTarget.PaymentId,
                 SubmissionValue.Text "20000000-0000-7000-8000-000000000003"
             ])
        [], ("case.withdrawDecision", Map.empty)
        [ voided ], closed
        [ voided ], voided
        [],
        ("case.reinstate",
         Map.ofList
             [
                 Submissions.input InputTarget.Reason, SubmissionValue.Text "Synthetic reason"
             ])
        [ "case.erase", Map.empty ], closed
        [ decided; payment ], correct "KEEP" "UPDATE" (decisionUpdate decision)
    ]

let internal stateEvidence () =
    task {
        let! _, fixture = fixture ()

        use _owned =
            { new System.IDisposable with
                member _.Dispose() = fixture.Dispose()
            }

        let! actor = caseWorker fixture

        let! granted =
            grant
                actor
                (id fixture 3)
                owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople granted
        let evidence = ResizeArray<_>()

        // Each scenario changes one case. Separate case references and operation numbers keep
        // those histories independent; the actor and installed database are common inputs.
        for scenario, (setup, (taskName, values)) in stateScenarios () |> List.indexed do
            let reference = $"REACH-STATE-{scenario}"
            let firstOperation = 100 + scenario * 64
            let! opening = execute actor (openRequest fixture firstOperation reference)
            accepted opening |> ignore

            for index, (setupTask, fields) in setup |> List.indexed do
                let! outcome =
                    execute
                        actor
                        (request
                            fixture
                            (firstOperation + index + 1)
                            reference
                            (int64 (index + 1))
                            setupTask
                            fields)

                accepted outcome |> ignore

            let! outcome =
                execute
                    actor
                    (request
                        fixture
                        (firstOperation + setup.Length + 1)
                        reference
                        (int64 (setup.Length + 1))
                        taskName
                        values)

            evidence.Add(refused outcome)

        return List.ofSeq evidence
    }

let internal queryEvidence fixture actor =
    task {
        let! limit =
            (Service.list
                (actor: ServiceContext)
                ((ClaimCore.TestSupport.Inputs.caseListPage ({ AfterCursor = None; Limit = 0 })))
                (ct))

        let! cursor =
            (Service.list
                actor
                ((ClaimCore.TestSupport.Inputs.caseListPage (
                    { AfterCursor = Some "bad"; Limit = 1 }
                )))
                (ct))

        for number, reference in [ 1700, "TRAVERSAL-A"; 1701, "TRAVERSAL-B" ] do
            let! opened = execute actor (openRequest fixture number reference)
            accepted opened |> ignore

        let traversal: CaseListRequest =
            {
                Page = { AfterCursor = None; Limit = 1 }
                Filter = CaseListFilter.create [] [] [] (Some "CC-") CaseListOrder.Reference
            }

        let! first = Service.list actor traversal ct

        let continuation =
            match first with
            | Outcome.Succeeded page when page.NextCursor.IsSome -> page.NextCursor
            | other -> failtestf "Expected an authorized continuation: %A" other

        let! additional = execute actor (openRequest fixture 1702 "TRAVERSAL-C")
        accepted additional |> ignore

        let! stale =
            Service.list
                actor
                { traversal with
                    Page =
                        { traversal.Page with
                            AfterCursor = continuation
                        }
                }
                ct

        Expect.equal
            stale
            (Outcome.Rejected Refusal.TraversalChanged)
            "An accepted case changes the authorized traversal"

        let! missing =
            (Service.get
                actor
                ({
                    Target = caseTarget fixture "MISSING"
                })
                (ct))

        let range =
            { CaseListFilter.empty<string> with
                Dates =
                    Map.ofList
                        [
                            CaseDateFilter.ReceivedFrom, CalendarDate.ofDate (DateOnly(2026, 2, 2))
                            CaseDateFilter.ReceivedThrough,
                            CalendarDate.ofDate (DateOnly(2026, 2, 1))
                        ]
            }

        let! reversed =
            Service.list
                actor
                {
                    Filter = range
                    Page = { AfterCursor = None; Limit = 1 }
                }
                ct

        Expect.equal
            reversed
            (Outcome.Rejected(Refusal.CaseDateRange CaseDateFilter.ReceivedThrough))
            "A Cases read reports the upper field on a reversed range"

        let evidence = ResizeArray<_>()

        for raw in [ ""; "x\u0000y"; " x"; "x\u0001y"; "x\u200by"; String('x', 101) ] do
            let request =
                {
                    Page = { AfterCursor = None; Limit = 1 }
                    Filter = CaseListFilter.create [] [] [] (Some raw) CaseListOrder.Reference
                }

            let! reply = Service.list actor request ct
            evidence.Add(queryRefused reply)

        return
            [
                queryRefused limit
                queryRefused cursor
                queryRefused stale
                queryRefused missing
                queryRefused reversed
            ]
            @ List.ofSeq evidence
    }

let internal peopleEvidence fixture actor =
    task {
        let service =
            PrincipalKey.service "https://issuer.example/" "synthetic"
            |> Result.defaultWith (string >> failtest)

        let send n target taskName values =
            task {
                let! submission =
                    personSubmission fixture.Store (id fixture n) target None taskName values

                return! Service.executeSubmission (actor: ServiceContext) submission ct
            }

        let! registered =
            send 30 owner "people.register" (Submissions.profile DomainInputs.actorProfile)

        let! unknown = send 31 service "people.disable" Map.empty
        let! last = send 32 owner "people.disable" Map.empty

        let! admitted =
            send 33 service "people.register" (Submissions.profile DomainInputs.actorProfile)

        acceptedPeople admitted

        let! human =
            grant
                actor
                (id fixture 34)
                service
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        let! scope =
            grant
                actor
                (id fixture 39)
                owner
                {
                    Role = Role.Owner
                    Scope = GrantTarget.CaseId(CaseFixtures.caseId (id fixture 10))
                }
                true

        return
            [
                refused registered
                refused unknown
                refused last
                refused human
                refused scope
            ]
    }

let internal identityEvidence fixture actor =
    task {
        let pending = openRequest fixture 10 "REACH-1"
        let! opening = execute actor pending
        accepted opening |> ignore

        let! conflict =
            execute
                actor
                (request
                    fixture
                    10
                    "REACH-1"
                    0L
                    "case.open"
                    (Submissions.fields (
                        RegistrationInput.values (
                            DomainInputs.registrationWith
                                [ BusinessField.PolicyNumber, "Different claimant" ]
                                registration
                        )
                    )))

        let! revision =
            execute
                actor
                (request (fixture) (11) ("REACH-1") (2L) "case.close" (CaseTransitionInputs.closed))

        let reader =
            PrincipalKey.human "https://issuer.example/" "reader"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            ((task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (fixture.Store)
                        ((id fixture 35))
                        (reader)
                        (None)
                        "people.register"
                        (ClaimCore.TestSupport.Submissions.profile (
                            ClaimCore.TestSupport.DomainInputs.actorProfile
                        ))

                return! Service.executeSubmission (actor) submission ((ct))
            }))

        acceptedPeople registered

        let! granted =
            grant
                actor
                (id fixture 36)
                reader
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople granted

        let! stalePerson =
            personSubmission fixture.Store (id fixture 900101) reader None "people.enable" Map.empty

        let! changedProfile =
            (task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (fixture.Store)
                        ((id fixture 900102))
                        (reader)
                        (None)
                        "people.setProfile"
                        (ClaimCore.TestSupport.Submissions.profile (DomainInputs.actorProfile))

                return! Service.executeSubmission (actor) submission (ct)
            })

        acceptedPeople changedProfile

        let! actorChanged = Service.executeSubmission actor ((stalePerson)) ct

        let! steward =
            grant
                actor
                (id fixture 900103)
                owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople steward
        let erase = request (fixture) (900104) ("REACH-1") (1L) "case.erase" (Map.empty)

        let! child =
            Service.executeSubmission
                actor
                ((ClaimCore.TestSupport.Submissions.createRaw)
                    "note.add"
                    (id fixture 900105)
                    (ClaimCore.Application.Contracts.OperationBinding.Note(
                        (caseTarget fixture "REACH-1"),
                        None
                    ))
                    (Map.ofList
                        [
                            TaskInputId.Target InputTarget.NoteText,
                            ClaimCore.Application.Contracts.SubmissionValue.Text(
                                "Family review witness"
                            )
                        ]))
                ct

        accepted child |> ignore
        let! familyChanged = execute actor erase

        return
            [
                refused conflict
                refused revision
                refused actorChanged
                refused familyChanged
            ],
            reader
    }

let internal principalEvidence fixture (actor: ServiceContext) =
    task {
        let evidence = ResizeArray<_>()

        for index, (issuer, subject) in
            List.indexed
                [
                    "", "valid"
                    "https://issuer.example/", ""
                    "invalid", "valid"
                    "https://issuer.example/", String('x', 513)
                ] do
            let request =
                Submissions.createRaw
                    "people.register"
                    (id fixture (60 + index))
                    (OperationBinding.People(None, 0L))
                    (Submissions.profile DomainInputs.actorProfile
                     |> Map.add
                         (Submissions.input InputTarget.PrincipalKind)
                         (SubmissionValue.Text "HUMAN")
                     |> Map.add
                         (Submissions.input InputTarget.PrincipalIssuer)
                         (SubmissionValue.Text issuer)
                     |> Map.add
                         (Submissions.input InputTarget.PrincipalSubject)
                         (SubmissionValue.Text subject))

            let! outcome = (Service.executeSubmission actor ((request)) (ct))

            evidence.Add(refused outcome)

        return List.ofSeq evidence
    }

let private invalidTextSamples =
    [
        ""
        String(char 0xD800, 1)
        " padded"
        String('x', 201)
        "x\u0001y"
        "x\u200by"
    ]

let private profileAndDirectoryEvidence fixture actor =
    task {
        let results = ResizeArray<_>()

        let target =
            PrincipalKey.human "https://issuer.example/" "profile-diagnostics"
            |> Result.defaultWith (string >> failtest)

        for index, raw in List.indexed invalidTextSamples do
            for contact in [ false; true ] do
                let profile =
                    if contact then
                        {
                            ActorProfileInput.DisplayName = "Valid name"
                            DigestEmail = None
                            Contact = Some raw
                        }
                    else
                        {
                            ActorProfileInput.DisplayName = raw
                            DigestEmail = None
                            Contact = None
                        }

                let! reply =
                    (task {
                        let! submission =
                            ClaimCore.TestSupport.StoreFixtures.personSubmission
                                (fixture.Store)
                                ((id fixture (600 + index * 2 + (if contact then 1 else 0))))
                                (target)
                                (None)
                                "people.register"
                                (ClaimCore.TestSupport.Submissions.profile (profile))

                        return! Service.executeSubmission (actor) submission (ct)
                    })

                results.Add(refused reply)

            let! reply =
                Service.actors
                    actor
                    {
                        ActorListRequest.Text = Some raw
                        Role = None
                        AfterCursor = None
                        Limit = 10
                    }
                    ct

            results.Add(queryRefused reply)

        let assignment =
            {
                Role = Role.CaseReader
                Scope = GrantTarget.Installation
            }

        for index, grants in
            List.indexed [ []; List.replicate 49 assignment; [ assignment; assignment ] ] do
            let! reply =
                (task {
                    let! submission =
                        ClaimCore.TestSupport.StoreFixtures.personSubmission
                            (fixture.Store)
                            ((id fixture (650 + index)))
                            (target)
                            (None)
                            "people.admit"
                            (ClaimCore.TestSupport.Submissions.profile (DomainInputs.actorProfile)
                             |> Map.add
                                 (TaskInputId.Target InputTarget.Grants)
                                 (ClaimCore.Application.Contracts.SubmissionValue.Rows(
                                     (grants) |> List.map ClaimCore.TestSupport.Submissions.grant
                                 )))

                    return! Service.executeSubmission (actor) submission (ct)
                })

            results.Add(refused reply)

        return List.ofSeq results
    }

let internal boundaryEvidence () =
    task {
        let! database, fixture = fixture ()

        use _owned =
            { new System.IDisposable with
                member _.Dispose() = fixture.Dispose()
            }

        let! actor = caseWorker fixture
        let! identity, reader = identityEvidence fixture actor

        let! denied =
            execute
                (core fixture reader)
                (request (fixture) (37) ("REACH-1") (1L) "case.close" (CaseTransitionInputs.closed))

        let! queries = queryEvidence fixture actor
        let! people = peopleEvidence fixture actor
        let! principals = principalEvidence fixture actor
        let! profiles = profileAndDirectoryEvidence fixture actor

        let expired = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddMinutes(-11.))

        let pending =
            ClaimCore.TestSupport.Submissions.withOperationId
                (expired)
                (request (fixture) (42) ("REACH-1") (1L) "case.close" (CaseTransitionInputs.closed))

        let! outOfWindow = execute actor pending
        let! confirmed = Service.operationGet actor { OperationId = expired } ct

        Expect.equal
            confirmed.Outcome
            (Outcome.Succeeded OperationLookupResult.NotAccepted)
            "Real lookup commits final absence"

        let! behindFrontier = execute actor pending

        let! maintenance =
            RegisterMaintenanceStore.change
                database.OwnerSource
                database.MasterKey
                (MaintenanceChange.Acquire(
                    0L,
                    System.Guid.Parse "10000000-0000-4000-8000-000000000099"
                ))
                ct

        Expect.isTrue maintenance.State.Active "The owner explicitly pauses actual register writes"

        let! paused =
            execute
                actor
                (request (fixture) (80) ("REACH-1") (1L) "case.close" (CaseTransitionInputs.closed))

        return
            identity
            @ [ refused denied; refused outOfWindow; refused behindFrontier; refused paused ]
            @ queries
            @ people
            @ principals
            @ profiles
    }
