module ClaimCore.TestSupport.StoreContractReads

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

let internal withFixture factory run =
    async {
        let! fixture = factory () |> Async.AwaitTask

        try
            do! run fixture |> Async.AwaitTask
        finally
            fixture.Dispose()
    }

let internal storedPage result =
    result |> Result.defaultWith (fun _ -> failtest "Store read failed")

[<NoComparison>]
type internal HistoryInspection =
    {
        Case: StoredCase option
        Entries: StoredEffect list
        Next: EffectPosition option
    }

let internal caseTimeline fixture reference before limit =
    fixture.Store.Read(
        {
            ReadOperation.Work =
                (fun tx ->
                    task {
                        let! current = FamilyReads.findCaseByReference tx reference

                        let id =
                            current
                            |> Option.map _.CaseId
                            |> Option.defaultWith (fun () ->
                                failtest "Expected a case history target")

                        let! page =
                            LedgerReads.timelinePage
                                tx
                                ({
                                    TimelineStorage.PageQuery.CaseId = id
                                    Filter = Some TimelineFilter.Facts
                                    Range = None
                                    BeforePosition = before
                                    Limit = limit
                                })

                        return
                            {
                                Case = current
                                Entries = page.Items
                                Next = page.Next
                            }
                    })
            CancellationToken = CancellationToken.None
        }
    )

let internal casePage fixture principal visibility after limit =
    fixture.Store.Read(
        {
            ReadOperation.Work =
                (fun tx ->
                    task {
                        let! actor = AccessReads.access tx (principal) (None)

                        return!
                            FamilyReads.casePage
                                tx
                                ({
                                    TransactionPages.CaseQuery.Selection =
                                        {
                                            DateBasis = Inputs.workBasis
                                            Visibility = visibility
                                            ActorId = actor.Actor.Value.ActorId
                                            ReadRoles = Access.readRoles principal
                                            ForbiddenRoles = Access.invalidRoles principal
                                            Filter = CaseListFilter.empty<CaseSearchText>
                                        }
                                    AfterPosition = after
                                    Limit = limit
                                })
                    })
            CancellationToken = CancellationToken.None
        }
    )

let internal listCases fixture visibility after limit =
    casePage fixture fixture.Owner visibility after limit

let internal existingCaseId fixture reference =
    task {
        let! result =
            fixture.Store.Read
                {
                    Work = fun tx -> FamilyReads.findCaseByReference tx reference
                    CancellationToken = CancellationToken.None
                }

        return
            match result with
            | Ok(Some record) -> record.CaseId
            | _ -> failtest "Expected existing case identity"

    }

let internal assignedReader fixture (actor: ServiceContext) =
    task {
        let principal =
            PrincipalKey.human "https://issuer.example/" "assigned-reader"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            ((task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (fixture.Store)
                        ((id fixture 15))
                        (principal)
                        (None)
                        "people.register"
                        (ClaimCore.TestSupport.Submissions.profile (
                            ClaimCore.TestSupport.Inputs.actorProfile
                        ))

                return! Service.executeSubmission (actor) submission ((CancellationToken.None))
            }))

        acceptedPeople registered

        let! granted =
            grant
                actor
                (id fixture 16)
                principal
                {
                    Role = Role.CaseReader
                    Scope = (GrantTarget.CaseReference "C")
                }
                true

        acceptedPeople granted

        return principal
    }

let internal registerReader fixture (owner: ServiceContext) target =
    task {
        let! registered =
            ((task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (fixture.Store)
                        ((id fixture 2))
                        (target)
                        (None)
                        "people.register"
                        (ClaimCore.TestSupport.Submissions.profile (
                            ClaimCore.TestSupport.Inputs.actorProfile
                        ))

                return! Service.executeSubmission (owner) submission ((CancellationToken.None))
            }))

        acceptedPeople registered

        let! added =
            grant
                owner
                (id fixture 3)
                target
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople added
    }
