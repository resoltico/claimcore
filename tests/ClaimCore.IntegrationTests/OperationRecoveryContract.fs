module ClaimCore.TestSupport.OperationRecoveryContract

open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let internal rolelessAuthorship fixture =
    task {
        let! owner = caseWorker fixture

        let target =
            PrincipalKey.human "https://issuer.example/" "recovering-without-roles"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            (task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (fixture.Store)
                        ((id fixture 10))
                        (target)
                        (None)
                        "people.register"
                        (ClaimCore.TestSupport.Submissions.profile (
                            ClaimCore.TestSupport.DomainInputs.actorProfile
                        ))

                return! Service.executeSubmission (owner) submission (CancellationToken.None)
            })

        acceptedPeople registered

        let! granted =
            grant
                owner
                (id fixture 11)
                target
                {
                    Role = Role.CaseHandler
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople granted
        let author = core fixture target

        let! opened =
            Service.executeSubmission
                author
                (((request
                    (fixture)
                    (20)
                    ("ROLELESS-RECEIPT")
                    (0L)
                    "case.open"
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values (registration)
                    )))))
                CancellationToken.None

        accepted opened |> ignore

        let! revoked =
            grant
                owner
                (id fixture 21)
                target
                {
                    Role = Role.CaseHandler
                    Scope = GrantTarget.Installation
                }
                false

        acceptedPeople revoked

        let readOnly =
            { new IStore with
                member _.Context query = fixture.Store.Context query
                member _.Read operation = fixture.Store.Read operation

                member _.Write _ =
                    failtest "Passive recovery must never request a write transaction"
            }

        let passive = core { fixture with Store = readOnly } target

        let! known =
            Service.operationGet
                passive
                {
                    OperationLookup.OperationId = id fixture 20
                }
                CancellationToken.None

        match known.Outcome with
        | Outcome.Succeeded(OperationLookupResult.Accepted(receipt, true)) ->
            Expect.equal
                receipt.OperationId
                (CaseFixtures.operationId (id fixture 20))
                "Authorship preserves exact acceptance identity"

            match receipt.Subject with
            | ReceiptSubject.Case value ->
                Expect.equal
                    value.Snapshot
                    SnapshotDisclosure.Withheld
                    "Roleless authors get metadata without current case content"
            | _ -> failtest "Expected the author's case receipt"
        | _ -> failtest "A valid roleless author can read its accepted status"

        let! pending =
            Service.operationGet passive { OperationId = id fixture 30 } CancellationToken.None

        match pending.Outcome with
        | Outcome.Succeeded(OperationLookupResult.Pending finalAt) ->
            Expect.equal
                finalAt
                (OperationLifetime.finalAt (CaseFixtures.operationId (id fixture 30)))
                "Pending publishes its exact deadline"
        | other -> failtestf "Fresh absence must be pending without a write: %A" other

        let! visibleForeign =
            Service.operationGet
                (core { fixture with Store = readOnly } fixture.Owner)
                { OperationId = id fixture 20 }
                CancellationToken.None

        match visibleForeign.Outcome with
        | Outcome.Succeeded(OperationLookupResult.Accepted(_, false)) -> ()
        | other -> failtestf "Authorized foreign receipt must identify other authorship: %A" other

        let! disabled =
            (task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (fixture.Store)
                        ((id fixture 31))
                        (target)
                        (None)
                        "people.disable"
                        (Map.empty)

                return! Service.executeSubmission (owner) submission (CancellationToken.None)
            })

        acceptedPeople disabled

        let! denied =
            Service.operationGet passive { OperationId = id fixture 30 } CancellationToken.None

        match denied.Outcome with
        | Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable) -> ()
        | other -> failtestf "Disabled caller cannot look up an operation: %A" other
    }
