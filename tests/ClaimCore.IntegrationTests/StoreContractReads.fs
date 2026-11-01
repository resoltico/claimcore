module ClaimCore.TestSupport.StoreContractReads

open ClaimCore.TestSupport

open ClaimCore.Protocol
open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
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

type internal HistoryInspection =
    {
        Case: StoredCase option
        Entries: StoredEntry list
        Next: Revision option
    }

let internal history fixture reference after limit =
    fixture.Store.Read(
        {
            ReadOperation.Work =
                (fun tx ->
                    task {
                        let! current = tx.FindCaseByReference reference

                        let id =
                            current
                            |> Option.map _.CaseId
                            |> Option.defaultWith (fun () ->
                                failtest "Expected a case history target")

                        let! page =
                            tx.HistoryPage(
                                {
                                    HistoryPageQuery.CaseId = id
                                    AfterRevision = after
                                    Limit = limit
                                }
                            )

                        return
                            {
                                Case = current
                                Entries = page.Page.Items
                                Next = page.Page.Next
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
                        let! actor =
                            tx.Authority(
                                {
                                    AuthorityQuery.Principal = principal
                                    CaseId = None
                                }
                            )

                        return!
                            tx.CasePage(
                                {
                                    CasePageQuery.DateBasis = Inputs.workBasis
                                    Visibility = visibility
                                    ActorId = actor.Actor.Value.ActorId
                                    ReadRoles = Authority.readRoles principal
                                    ForbiddenRoles = Authority.invalidRoles principal
                                    AfterPosition = after
                                    Filter = CaseListFilter.empty<CaseSearchText>
                                    Limit = limit
                                }
                            )
                    })
            CancellationToken = CancellationToken.None
        }
    )

let internal listCases fixture visibility after limit =
    casePage fixture fixture.Owner visibility after limit

let internal existingCaseId fixture reference =
    task {
        let! result =
            inspectCase fixture (fixture.Owner) (None) (reference) (CancellationToken.None)

        return
            match result with
            | Ok { Case = Some record } -> record.CaseId
            | _ -> failtest "Expected existing case identity"
    }

let internal assignedReader (actor: ServiceContext) =
    task {
        let principal =
            PrincipalKey.human "https://issuer.example/" "assigned-reader"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            (Service.execute
                actor
                (OperationInput.Authority(authorityRequest (id 15) principal AuthorityAct.Register))
                (CancellationToken.None))

        acceptedAuthority registered

        let! granted =
            grant
                actor
                (id 16)
                principal
                {
                    Role = Role.CaseReader
                    Scope = (GrantTarget.CaseReference "C")
                }
                true

        acceptedAuthority granted

        return principal
    }

let internal registerReader (owner: ServiceContext) target =
    task {
        let! registered =
            (Service.execute
                owner
                (OperationInput.Authority(authorityRequest (id 2) target AuthorityAct.Register))
                (CancellationToken.None))

        acceptedAuthority registered

        let! added =
            grant
                owner
                (id 3)
                target
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority added
    }
