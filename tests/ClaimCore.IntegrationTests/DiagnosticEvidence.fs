module ClaimCore.IntegrationTests.DiagnosticEvidence

open ClaimCore.Protocol

open ClaimCore.TestSupport

open ClaimCore.Json

open System
open System.Threading
open System.Text.Json
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
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

        return List.ofSeq results
    }

let internal stateScenarios () =
    [
        [], (AuthoredCaseAct.Open registration)
        [], (AuthoredCaseAct.Bare BareAct.Reopen)
        [ closed ], (AuthoredCaseAct.Bare BareAct.Close)
        [ closed ], decided
        [ decided ], (AuthoredCaseAct.AmendRegistration registration)
        [ decided ],
        correction
            { keep with
                Registration = Amendment.Replace registration
            }
        [], correction { keep with Decision = Change.Clear }
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
                Decision = Change.Replace decision
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
            (Service.list (actor: ServiceContext) ({ AfterCursor = None; Limit = 0 }) (ct))

        let! cursor = (Service.list actor ({ AfterCursor = Some "bad"; Limit = 1 }) (ct))

        let! missing = (Service.get actor ({ CaseReference = "MISSING" }) (ct))
        return [ queryRefused limit; queryRefused cursor; queryRefused missing ]
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

        let! registered = send 30 owner AuthorityAct.Register
        let! unknown = send 31 service (AuthorityAct.Disable)
        let! last = send 32 owner (AuthorityAct.Disable)
        let! admitted = send 33 service AuthorityAct.Register
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

        return [ refused registered; refused unknown; refused last; refused human ]
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
                (OperationInput.Authority(authorityRequest (id 35) reader AuthorityAct.Register))
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
                    Act = ClaimCore.Domain.AuthorityAct.Register
                }

            let! outcome = (Service.execute actor (OperationInput.Authority request) (ct))

            evidence.Add(refused outcome)

        return List.ofSeq evidence
    }

let internal boundaryEvidence () =
    task {
        let! _, fixture = fixture ()

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

        let! resolved = (Service.resolve actor ({ OperationId = id 42 }) (ct))

        Expect.equal resolved (Outcome.Succeeded Resolved.NotAccepted) "Resolve installs a fence"

        let! fenced =
            execute actor (request 42 "REACH-1" 1L (AuthoredCaseAct.Bare BareAct.Close))

        return identity @ [ refused denied; refused fenced ] @ queries @ authority @ principals
    }
