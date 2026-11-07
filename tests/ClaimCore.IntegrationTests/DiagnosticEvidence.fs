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
open ClaimCore.Postgres
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

        for index, request in scalarRequests () |> List.indexed do
            // Distinct witnesses reserve distinct operation identities, including known business refusals.
            let pending =
                if request.OperationId = Guid.Empty then
                    request
                else
                    { request with
                        OperationId = id (100 + index)
                    }

            let! outcome = execute actor pending
            results.Add(refused outcome)

        // Malformed existing-case acts now require the authoritative readable snapshot and capability.
        let! scalarCase = execute actor (openRequest 480 "REACH-1")
        accepted scalarCase |> ignore

        let! scalarSteward =
            grant
                actor
                (id 481)
                owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority scalarSteward

        for index, request in List.indexed (progressRequests ()) do
            let! outcome =
                execute
                    actor
                    { request with
                        OperationId = id (482 + index)
                    }

            results.Add(refused outcome)

        let! workOpening = execute actor (openRequest 490 "REACH-WORK")
        accepted workOpening |> ignore

        for request in workScalarRequests () do
            let! outcome = Service.execute actor request ct
            results.Add(refused outcome)

        let target = CaseSelector.Reference "REACH-WORK"

        let work operation responsible summary =
            OperationInput.Work
                {
                    OperationId = id operation
                    Target = target
                    ExpectedRevision = 0L
                    Act =
                        WorkAct.UpdateWork
                            {
                                Responsible = responsible
                                Summary = summary
                            }
                }

        let reminder operation act =
            OperationInput.Reminder
                {
                    OperationId = id operation
                    Target = target
                    Act = act
                }

        for request in
            [
                work 509 WorkValue.Keep WorkValue.Clear
                work
                    510
                    (WorkValue.Set(ActorId.format (CaseFixtures.actorId (id 999))))
                    WorkValue.Keep
            ] do
            let! outcome = Service.execute actor request ct
            results.Add(refused outcome)

        let plan =
            {
                ReminderPlanInput.Action = "Request report"
                Due = "2027-01-01"
            }

        let! planned =
            Service.execute actor (reminder 520 (AuthoredReminderAct.Create plan)) ct

        accepted planned |> ignore

        for request in
            [
                reminder 521 (AuthoredReminderAct.Reopen(id 520, 1L, "2027-01-01"))
                reminder 522 (AuthoredReminderAct.Update(id 520, 1L, plan))
            ] do
            let! outcome = Service.execute actor request ct
            results.Add(refused outcome)

        let! completed =
            Service.execute actor (reminder 523 (AuthoredReminderAct.Complete(id 520, 1L))) ct

        accepted completed |> ignore

        let! repeated =
            Service.execute actor (reminder 524 (AuthoredReminderAct.Complete(id 520, 2L))) ct

        results.Add(refused repeated)

        let! closed =
            execute actor (request 525 "REACH-WORK" 1L (AuthoredCaseAct.Bare BareAct.Close))

        accepted closed |> ignore

        let! inactive =
            Service.execute actor (reminder 526 (AuthoredReminderAct.Create plan)) ct

        results.Add(refused inactive)

        let! stewardship =
            grant
                actor
                (id 527)
                owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority stewardship

        let! voided =
            execute
                actor
                (request 528 "REACH-WORK" 2L (AuthoredCaseAct.Void "Synthetic diagnostic state"))

        accepted voided |> ignore

        let! blocked =
            Service.execute actor (reminder 529 (AuthoredReminderAct.Create plan)) ct

        results.Add(refused blocked)
        return List.ofSeq results
    }

let internal stateScenarios () =
    [
        [], (AuthoredCaseAct.Open registration)
        [], (AuthoredCaseAct.Bare BareAct.Reopen)
        [ closed ], (AuthoredCaseAct.Bare BareAct.Close)
        [ closed ], decided
        [ decided ], (AuthoredCaseAct.AmendRegistration(Inputs.registrationUpdate registration))
        [ decided ],
        correction
            { keep with
                Registration = Amendment.Update(Inputs.registrationUpdate registration)
            }
        [ closed ], correction { keep with Decision = Change.Clear }
        [], payment
        [], (AuthoredCaseAct.Bare BareAct.ClearPayment)
        [ decided; payment ], payment
        [ decided; payment ], (AuthoredCaseAct.Bare BareAct.WithdrawDecision)
        [
            (AuthoredCaseAct.Decide(
                ClaimCore.TestSupport.Inputs.decisionWith
                    [ BusinessField.PayableAmount, ("0") ]
                    (decision)
            ))
        ],
        payment
        [ voided ], closed
        [ voided ], voided
        [], (AuthoredCaseAct.Reinstate "Synthetic reason")
        [ (AuthoredCaseAct.Bare BareAct.Erase) ], closed
        [ decided; payment ], correction { keep with Decision = Change.Clear }
        [ decided; payment ],
        correction
            { keep with
                Decision = Change.Update(Inputs.decisionUpdate decision)
            }
        [ decided; payment ],
        correction
            { keep with
                Decision =
                    Change.Update(
                        Inputs.decisionUpdate (
                            Inputs.decisionWith [ BusinessField.PayableAmount, "0" ] decision
                        )
                    )
                Payment = PaymentChange.Reaffirm
            }
    ]

let internal stateEvidence (setup, final) =
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
                (id 3)
                owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority granted
        let! opening = execute actor (openRequest 10 "REACH-1")
        accepted opening |> ignore

        for index, act in List.indexed setup do
            let! outcome =
                execute actor (actRequest (11 + index) "REACH-1" (int64 (index + 1)) act)

            accepted outcome |> ignore

        let! outcome =
            execute actor (actRequest 50 "REACH-1" (int64 (setup.Length + 1)) final)

        return refused outcome
    }

let internal queryEvidence actor =
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

        let! missing =
            (Service.get
                actor
                ({
                    Target = CaseSelector.Reference("MISSING")
                })
                (ct))

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
            [ queryRefused limit; queryRefused cursor; queryRefused missing ]
            @ List.ofSeq evidence
    }

let internal authorityEvidence actor =
    task {
        let service =
            PrincipalKey.service "https://issuer.example/" "synthetic"
            |> Result.defaultWith (string >> failtest)

        let send n target act =
            (Service.execute
                (actor: ServiceContext)
                (OperationInput.Authority(authorityRequest (id n) target act))
                (ct))

        let! registered =
            send 30 owner (AuthorityAct.Register ClaimCore.TestSupport.Inputs.actorProfile)

        let! unknown = send 31 service (AuthorityAct.Disable)
        let! last = send 32 owner (AuthorityAct.Disable)

        let! admitted =
            send 33 service (AuthorityAct.Register ClaimCore.TestSupport.Inputs.actorProfile)

        acceptedAuthority admitted

        let! human =
            grant
                actor
                (id 34)
                service
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        let! scope =
            grant
                actor
                (id 39)
                owner
                {
                    Role = Role.Owner
                    Scope = GrantTarget.CaseId(CaseFixtures.caseId (id 10))
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

let internal identityEvidence actor =
    task {
        let pending = openRequest 10 "REACH-1"
        let! opening = execute actor pending
        accepted opening |> ignore
        let! conflict = execute actor { pending with ExpectedRevision = 1L }

        let! revision =
            execute actor (request 11 "REACH-1" 0L (AuthoredCaseAct.Bare BareAct.Close))

        let reader =
            PrincipalKey.human "https://issuer.example/" "reader"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            (Service.execute
                actor
                (OperationInput.Authority(
                    authorityRequest
                        (id 35)
                        reader
                        (AuthorityAct.Register ClaimCore.TestSupport.Inputs.actorProfile)
                ))
                (ct))

        acceptedAuthority registered

        let! granted =
            grant
                actor
                (id 36)
                reader
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority granted
        return [ refused conflict; refused revision ], reader
    }

let internal principalEvidence (actor: ServiceContext) =
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
            let request: ClaimCore.Domain.AuthorityRequest =
                {
                    OperationId = id (60 + index)
                    Target =
                        {
                            Kind = PrincipalKind.Human
                            Issuer = issuer
                            Subject = subject
                        }
                    Act =
                        (ClaimCore.Domain.AuthorityAct.Register
                            ClaimCore.TestSupport.Inputs.actorProfile)
                }

            let! outcome = (Service.execute actor (OperationInput.Authority request) (ct))

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

let private profileAndDirectoryEvidence actor =
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
                            Contact = Some raw
                        }
                    else
                        {
                            ActorProfileInput.DisplayName = raw
                            Contact = None
                        }

                let! reply =
                    Service.execute
                        actor
                        (OperationInput.Authority(
                            authorityRequest
                                (id (600 + index * 2 + (if contact then 1 else 0)))
                                target
                                (AuthorityAct.Register profile)
                        ))
                        ct

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
                Service.execute
                    actor
                    (OperationInput.Authority(
                        authorityRequest
                            (id (650 + index))
                            target
                            (AuthorityAct.Admit
                                {
                                    Profile = Inputs.actorProfile
                                    Grants = grants
                                })
                    ))
                    ct

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
        let! identity, reader = identityEvidence actor

        let! denied =
            execute
                (core fixture reader)
                (request 37 "REACH-1" 1L (AuthoredCaseAct.Bare BareAct.Close))

        let! queries = queryEvidence actor
        let! authority = authorityEvidence actor
        let! principals = principalEvidence actor
        let! profiles = profileAndDirectoryEvidence actor

        let! resolved =
            (ClaimCore.TestSupport.StoreFixtures.resolve actor ({ OperationId = id 42 }) (ct))

        Expect.equal resolved (Outcome.Succeeded Resolved.NotAccepted) "Resolve installs a fence"

        let! fenced =
            execute actor (request 42 "REACH-1" 1L (AuthoredCaseAct.Bare BareAct.Close))

        let! maintenance =
            RegisterMaintenanceStore.change database.OwnerSource MaintenanceChange.Enter ct

        Expect.isOk maintenance "The owner explicitly pauses actual register writes"

        let! paused =
            execute actor (request 80 "REACH-1" 1L (AuthoredCaseAct.Bare BareAct.Close))

        return
            identity
            @ [ refused denied; refused fenced; refused paused ]
            @ queries
            @ authority
            @ principals
            @ profiles
    }
