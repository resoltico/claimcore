module ClaimCore.TestSupport.PeopleHistoryContract

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.TestSupport.StoreFixtures

let private page (outcome: Outcome<PeopleHistoryPage>) =
    match outcome with
    | Outcome.Succeeded value -> value
    | _ -> failtestf "Expected people history: %A" outcome

let private read actor target cursor limit =
    Service.peopleHistory
        actor
        {
            TargetActorId = target
            AfterCursor = cursor
            Limit = limit
        }
        Threading.CancellationToken.None

let private execute fixture actor number target act =
    task {
        let! reply =
            Service.execute
                actor
                (OperationInput.People(peopleRequest (id fixture number) target act))
                Threading.CancellationToken.None

        return accepted reply
    }

let private principal subject =
    PrincipalKey.human "https://issuer.example/" subject
    |> Result.defaultWith (string >> failtest)

let internal factsAndPaging fixture =
    task {
        let! owner = caseWorker fixture
        let target = principal "people-history-target"

        let! registered =
            execute
                fixture
                owner
                10
                target
                (PeopleAct.Register ClaimCore.TestSupport.Inputs.actorProfile)

        let targetId =
            match registered.Subject with
            | ReceiptSubject.People value -> PeopleTargetDisclosure.actorId value.TargetActor
            | _ -> failtest "Expected people subject"

        let! opened =
            Service.execute
                owner
                (ClaimCore.TestSupport.Inputs.caseOperation (
                    request fixture 11 "HISTORY-CASE" 0L (AuthoredCaseAct.Open registration)
                ))
                Threading.CancellationToken.None

        accepted opened |> ignore

        let grant =
            {
                Role = Role.CaseReader
                Scope = GrantTarget.CaseReference "HISTORY-CASE"
            }

        let! _ = execute fixture owner 12 target (PeopleAct.Grant grant)
        let! _ = execute fixture owner 13 target (PeopleAct.Revoke grant)
        let! _ = execute fixture owner 14 target PeopleAct.Disable
        let! _ = execute fixture owner 15 target PeopleAct.Enable
        let! all = read owner None None 50
        let entries = (page all).Entries

        Expect.equal
            (entries |> List.map (fun entry -> EffectPosition.format entry.Position))
            [ "9/0"; "8/0"; "7/0"; "6/0"; "4/1"; "4/0"; "3/0"; "2/0"; "1/1"; "1/0" ]
            "Mixed case entries never enter people history"

        Expect.equal
            (entries |> List.map (fun entry -> PeopleHistoryFacts.kind entry.Facts))
            [
                PeopleActKind.Enable
                PeopleActKind.Disable
                PeopleActKind.Revoke
                PeopleActKind.Grant
                PeopleActKind.SetProfile
                PeopleActKind.Register
                PeopleActKind.Grant
                PeopleActKind.Grant
                PeopleActKind.SetProfile
                PeopleActKind.ProvisionOwner
            ]
            "Every people act has independently specified chronological meaning"

        Expect.equal
            entries[3].Facts
            (PeopleHistoryFacts.Granted
                {
                    Role = Role.CaseReader
                    Scope =
                        GrantScope.Case(
                            CaseFixtures.caseId (id fixture 11),
                            CaseFixtures.reference "HISTORY-CASE"
                        )
                })
            "Typed case reference and role"

        Expect.equal
            entries[5].Facts
            (PeopleHistoryFacts.Registered target)
            "Registered principal facts"

        Expect.equal
            entries[9].Facts
            (PeopleHistoryFacts.OwnerProvisioned fixture.Owner)
            "Provision facts"

        Expect.isTrue
            (entries
             |> List.forall (fun entry -> entry.RecordedBy.ActorId = entries[9].Target.ActorId))
            "The executing actor is retained"

        let! first = read owner None None 2
        let! second = read owner None (page first).NextCursor 2
        let! third = read owner None (page second).NextCursor 2
        let! fourth = read owner None (page third).NextCursor 2
        let! fifth = read owner None (page fourth).NextCursor 2

        let paged =
            [ first; second; third; fourth; fifth ] |> List.collect (page >> _.Entries)

        Expect.equal paged entries "Newest-first keyset pages cover the mixed ledger exactly once"
        Expect.isNone (page fifth).NextCursor "Final page has no continuation"
        let! filtered = read owner (Some(ActorId.value targetId)) None 50

        Expect.equal
            ((page filtered).Entries
             |> List.map (fun entry -> EffectPosition.format entry.Position))
            [ "9/0"; "8/0"; "7/0"; "6/0"; "4/1"; "4/0" ]
            "Actor filter means target, not executor"

        let! filteredFirst = read owner (Some(ActorId.value targetId)) None 1
        let! wrongFilter = read owner None (page filteredFirst).NextCursor 1

        Expect.equal
            wrongFilter
            (Outcome.Rejected Refusal.InvalidCursor)
            "Cursor binds the target filter"
    }

let internal permissions fixture =
    task {
        let! owner = caseWorker fixture

        let! opened =
            Service.execute
                owner
                (ClaimCore.TestSupport.Inputs.caseOperation (
                    request fixture 10 "HISTORY-ACCESS" 0L (AuthoredCaseAct.Open registration)
                ))
                Threading.CancellationToken.None

        accepted opened |> ignore
        let handler = principal "history-handler"
        let scoped = principal "history-scoped-auditor"
        let auditor = principal "history-auditor"

        let! _ =
            execute
                fixture
                owner
                11
                handler
                (PeopleAct.Register ClaimCore.TestSupport.Inputs.actorProfile)

        let! _ =
            execute
                fixture
                owner
                12
                handler
                (PeopleAct.Grant
                    {
                        Role = Role.CaseHandler
                        Scope = GrantTarget.Installation
                    })

        let! _ =
            execute
                fixture
                owner
                13
                scoped
                (PeopleAct.Register ClaimCore.TestSupport.Inputs.actorProfile)

        let! _ =
            execute
                fixture
                owner
                14
                scoped
                (PeopleAct.Grant
                    {
                        Role = Role.Auditor
                        Scope = GrantTarget.CaseReference "HISTORY-ACCESS"
                    })

        let! _ =
            execute
                fixture
                owner
                15
                auditor
                (PeopleAct.Register ClaimCore.TestSupport.Inputs.actorProfile)

        let! _ =
            execute
                fixture
                owner
                16
                auditor
                (PeopleAct.Grant
                    {
                        Role = Role.Auditor
                        Scope = GrantTarget.Installation
                    })

        for denied in [ handler; scoped; principal "history-unknown" ] do
            let! result = read (core fixture denied) None (Some "malformed") 1

            Expect.equal
                result
                (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
                "Installation ReadAccess is checked before cursor interpretation"

        let viewer = core fixture auditor
        let! first = read viewer None None 1
        Expect.isSome (page first).NextCursor "An installation auditor can read a bounded page"

        let! _ =
            execute
                fixture
                owner
                20
                auditor
                (PeopleAct.Revoke
                    {
                        Role = Role.Auditor
                        Scope = GrantTarget.Installation
                    })

        let! revoked = read viewer None (page first).NextCursor 1

        Expect.equal
            revoked
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "A continuation never retains a revoked permission"

        let! malformed = read owner None (Some "malformed") 1

        Expect.equal
            malformed
            (Outcome.Rejected Refusal.InvalidCursor)
            "Authorized cursor errors remain explicit"
    }

let private changingHistory (fixture: Fixture) change =
    let store =
        { new IStore with
            member _.Context query = fixture.Store.Context query

            member _.Read operation =
                fixture.Store.Read
                    { operation with
                        Work =
                            fun tx ->
                                operation.Work
                                    { new IReadTx with
                                        member _.AccessRequestPage query =
                                            tx.AccessRequestPage query

                                        member _.CaseState id = tx.CaseState id

                                        member _.CaseFamily id = tx.CaseFamily id

                                        member _.PendingReminderDue(parent, excluded) =
                                            tx.PendingReminderDue(parent, excluded)


                                        member _.LatestPayments(parent, selection) =
                                            tx.LatestPayments(parent, selection)


                                        member _.PaymentPage query = tx.PaymentPage query
                                        member _.ObservedAt = tx.ObservedAt
                                        member _.Maintenance() = tx.Maintenance()
                                        member _.ExpiredBefore() = tx.ExpiredBefore()

                                        member _.GlobalReadFacts principal =
                                            tx.GlobalReadFacts principal

                                        member _.Access query = tx.Access query

                                        member _.AccessCases(principal, ids) =
                                            tx.AccessCases(principal, ids)

                                        member _.FindCase id = tx.FindCase id

                                        member _.FindCaseByReference reference =
                                            tx.FindCaseByReference reference

                                        member _.FindWork id = tx.FindWork id
                                        member _.FindNote id = tx.FindNote id

                                        member _.NotePage query = tx.NotePage query

                                        member _.FindReminder id = tx.FindReminder id

                                        member _.WorkSummaries(ids, today) =
                                            tx.WorkSummaries(ids, today)

                                        member _.AssignedAccounts pairs = tx.AssignedAccounts pairs

                                        member _.ResponsibilityCounts(selection, actors) =
                                            tx.ResponsibilityCounts(selection, actors)

                                        member _.ReminderPage query = tx.ReminderPage query

                                        member _.CaseViewCounts selection =
                                            tx.CaseViewCounts selection

                                        member _.ReminderViewCounts selection =
                                            tx.ReminderViewCounts selection

                                        member _.TimelinePage query = tx.TimelinePage query
                                        member _.CaseStageFacts id = tx.CaseStageFacts id

                                        member _.PreviousEntries entries =
                                            tx.PreviousEntries entries

                                        member _.FindOperation id = tx.FindOperation id

                                        member _.CasePage query = tx.CasePage query
                                        member _.CaseSummary query = tx.CaseSummary query
                                        member _.ActorPage query = tx.ActorPage query
                                        member _.GrantPage query = tx.GrantPage query
                                        member _.ActorRoleSummaries ids = tx.ActorRoleSummaries ids
                                        member _.ActorRefs ids = tx.ActorRefs ids
                                        member _.ActorPrincipal id = tx.ActorPrincipal id

                                        member _.PeopleState principal = tx.PeopleState principal

                                        member _.FindActor id = tx.FindActor id
                                        member _.AssigneePage query = tx.AssigneePage query
                                        member _.EnabledHumanOwners() = tx.EnabledHumanOwners()
                                        member _.ActorCount() = tx.ActorCount()

                                        member _.PeopleHistoryPage query =
                                            task {
                                                let! page = tx.PeopleHistoryPage query
                                                return change page
                                            }
                                    }
                    }

            member _.Write operation = fixture.Store.Write operation
        }

    { fixture with Store = store }

let internal integrity fixture =
    task {
        let! _ = caseWorker fixture
        let ownerId = CaseFixtures.actorId (id fixture 1)

        let! original = read (core fixture fixture.Owner) None None 50
        let initial = page original

        let profileRoot =
            initial.Entries
            |> List.find (fun entry ->
                entry.Facts
                |> function
                    | PeopleHistoryFacts.ProfileSet _ -> true
                    | _ -> false)

        let! retained =
            fixture.Store.Read
                {
                    Work = (fun tx -> tx.FindOperation profileRoot.OperationId)
                    CancellationToken = Threading.CancellationToken.None
                }

        let retained =
            retained
            |> Result.defaultWith (fun error -> failtestf "Profile root: %A" error)
            |> Option.get

        let compartment = retained.Root.Structure.Compartments |> List.exactlyOne
        let nonce, bytes = Compartment.content compartment |> Option.get
        nonce[0] <- nonce[0] ^^^ 1uy

        Expect.equal
            (Compartment.restore
                fixture.InstallationId
                retained.OperationId
                compartment.Descriptor
                compartment.Commitment
                (Some nonce)
                (Some bytes))
            (Error CompartmentProblem.CommitmentMismatch)
            "A corrupt salt cannot become a typed history effect"

        let reordered =
            changingHistory fixture (fun page ->
                { page with
                    Items = List.rev page.Items
                })

        let! wrongOrder = read (core reordered fixture.Owner) None None 50

        Expect.equal
            wrongOrder
            (Outcome.Failed CoreFault.StoreIntegrityError)
            "A malformed store page cannot silently change history order"

        let otherPrincipal = principal "other-history-target"

        let! registered =
            execute
                fixture
                (core fixture fixture.Owner)
                99
                otherPrincipal
                (PeopleAct.Register Inputs.actorProfile)

        let! other =
            fixture.Store.Read
                {
                    Work = (fun tx -> tx.FindOperation registered.OperationId)
                    CancellationToken = Threading.CancellationToken.None
                }

        let other =
            other
            |> Result.defaultWith (fun error -> failtestf "Other root: %A" error)
            |> Option.get

        let otherEffect = other |> StoredEffect.ofOperation |> List.last

        let foreignTarget (page: Page<StoredEffect, EffectPosition>) =
            { page with
                Items = otherEffect :: page.Items.Tail
            }

        let foreign = changingHistory fixture foreignTarget
        let! unfiltered = read (core foreign fixture.Owner) None None 50

        Expect.isTrue
            (match unfiltered with
             | Outcome.Succeeded _ -> true
             | _ -> false)
            "The substituted root is a genuine readable target before filtering"

        let! wrongTarget =
            read (core foreign fixture.Owner) (Some(ActorId.value ownerId)) None 50

        Expect.equal
            wrongTarget
            (Outcome.Failed CoreFault.StoreIntegrityError)
            "A filtered page cannot substitute a different target actor"
    }
