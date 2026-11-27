module ClaimCore.IntegrationTests.PaginationScenarios

open ClaimCore.TestSupport
open ClaimCore.Wire
open ClaimCore.Presentation

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.ApplicationFixtures


let internal cases fixture (actor: ServiceContext) =
    task {
        for n, reference in [ 10, "Case-c"; 11, "case-A"; 12, "CASE-B" ] do
            let! result =
                (Service.executeSubmission
                    actor
                    (((openRequest fixture n reference)))
                    (CancellationToken.None))

            accepted result |> ignore
    }

let internal succeeded =
    function
    | Outcome.Succeeded page -> page
    | _ -> failtest "Missing case page"

let internal reference (summary: CaseSummary) = CaseReference.format summary.Reference

let internal cursor =
    function
    | Some value -> value
    | None -> failtest "Missing continuation"

let internal reader fixture (admin: ServiceContext) =
    task {
        let target =
            PrincipalKey.human "https://issuer.example/" "reader"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            ((task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (fixture.Store)
                        ((id fixture 20))
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
                (id fixture 21)
                target
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople granted
        return target, core fixture target
    }

let internal createHistory fixture (actor: ServiceContext) =
    task {
        let! opened =
            (Service.executeSubmission
                actor
                (((openRequest fixture 10 "HISTORY-1")))
                (CancellationToken.None))

        accepted opened |> ignore

        for n, revision in [ 11, 1L; 12, 2L ] do
            let values =
                (ClaimCore.TestSupport.DomainInputs.registrationWith
                    [ BusinessField.PolicyNumber, ("Party " + string n) ]
                    (registration))

            let! amended =
                (Service.executeSubmission
                    actor
                    (((request
                        (fixture)
                        (n)
                        ("HISTORY-1")
                        (revision)
                        "case.amendRegistration"
                        (ClaimCore.TestSupport.Submissions.updates (
                            RegistrationUpdateInput.values (
                                (DomainInputs.registrationUpdate values)
                            )
                        )))))
                    (CancellationToken.None))

            accepted amended |> ignore
    }

let internal registerActors fixture (actor: ServiceContext) =
    task {
        for n in 10..12 do
            let target =
                PrincipalKey.human "https://issuer.example/" ("actor-" + string n)
                |> Result.defaultWith (string >> failtest)

            let! registered =
                ((task {
                    let! submission =
                        ClaimCore.TestSupport.StoreFixtures.personSubmission
                            (fixture.Store)
                            ((id fixture n))
                            (target)
                            (None)
                            "people.register"
                            (ClaimCore.TestSupport.Submissions.profile (
                                ClaimCore.TestSupport.DomainInputs.actorProfile
                            ))

                    return! Service.executeSubmission (actor) submission ((CancellationToken.None))
                }))

            acceptedPeople registered
    }

let internal differentHistoryResource
    fixture
    (actor: ServiceContext)
    (query: CaseTimelineRequest)
    cursor
    =
    task {
        let! other =
            (Service.executeSubmission
                actor
                (((openRequest fixture 13 "OTHER")))
                (CancellationToken.None))

        accepted other |> ignore

        let! resource =
            (Service.timeline
                actor
                ({ query with
                    Target = caseTarget fixture "OTHER"
                    AfterCursor = cursor
                })
                (CancellationToken.None))

        Expect.equal resource (Outcome.Rejected Refusal.InvalidCursor) "Resource is bound"
    }

let internal appendHistory fixture (actor: ServiceContext) =
    task {
        for revision in 1L .. 52L do
            let taskName, inputs =
                if revision % 2L = 1L then
                    "case.close", CaseTransitionInputs.closed
                else
                    "case.reopen", CaseTransitionInputs.reopened

            let! result =
                Service.executeSubmission
                    actor
                    (request fixture (20 + int revision) "LONG-HISTORY" revision taskName inputs)
                    CancellationToken.None

            accepted result |> ignore

    }

let internal invalidGrantPositions =
    [
        "{\"caseId\":null,\"role\":\"NOT_A_ROLE\"}"
        "{\"caseId\":\"00000000-0000-0000-0000-000000000000\",\"role\":\"CASE_READER\"}"
        "{\"caseId\":\"A0000000-0000-4000-8000-000000000001\",\"role\":\"CASE_READER\"}"
        "{ \"caseId\":null,\"role\":\"CASE_READER\"}"
        "{\"caseId\":null,\"role\":\"CASE_READER\",\"extra\":false}"
    ]
