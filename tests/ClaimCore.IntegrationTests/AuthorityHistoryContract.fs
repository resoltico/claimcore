module ClaimCore.TestSupport.AuthorityHistoryContract

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures

let private page (outcome: Outcome<AuthorityHistoryPage>) =
    match outcome with
    | Outcome.Succeeded value -> value
    | _ -> failtestf "Expected authority history: %A" outcome

let private read actor target cursor limit =
    Service.authorityHistory
        actor
        {
            TargetActorId = target
            AfterCursor = cursor
            Limit = limit
        }
        Threading.CancellationToken.None

let private execute actor number target act =
    task {
        let! reply =
            Service.execute
                actor
                (OperationInput.Authority(authorityRequest (id number) target act))
                Threading.CancellationToken.None

        return accepted reply
    }

let private principal subject =
    PrincipalKey.human "https://issuer.example/" subject
    |> Result.defaultWith (string >> failtest)

let internal factsAndPaging fixture =
    task {
        let! owner = caseWorker fixture
        let target = principal "authority-history-target"
        let! registered = execute owner 10 target AuthorityAct.Register

        let targetId =
            match registered.Subject with
            | ReceiptSubject.Authority value -> value.TargetActorId
            | _ -> failtest "Expected authority subject"

        let! opened =
            Service.execute
                owner
                (OperationInput.Case(
                    request 11 "HISTORY-CASE" 0L (AuthoredCaseAct.Open registration)
                ))
                Threading.CancellationToken.None

        accepted opened |> ignore

        let grant =
            {
                Role = Role.CaseReader
                Scope = GrantTarget.CaseReference "HISTORY-CASE"
            }

        let! _ = execute owner 12 target (AuthorityAct.Grant grant)
        let! _ = execute owner 13 target (AuthorityAct.Revoke grant)
        let! _ = execute owner 14 target AuthorityAct.Disable
        let! _ = execute owner 15 target AuthorityAct.Enable
        let! all = read owner None None 50
        let entries = (page all).Entries

        Expect.equal
            (entries |> List.map (fun entry -> Sequence.value entry.Sequence))
            [ 9L; 8L; 7L; 6L; 4L; 3L; 2L; 1L ]
            "Mixed case entries never enter authority history"

        Expect.equal
            (entries |> List.map (fun entry -> AuthorityHistoryFacts.kind entry.Facts))
            [
                AuthorityKind.Enable
                AuthorityKind.Disable
                AuthorityKind.Revoke
                AuthorityKind.Grant
                AuthorityKind.Register
                AuthorityKind.Grant
                AuthorityKind.Grant
                AuthorityKind.ProvisionOwner
            ]
            "Every authority act has independently specified chronological meaning"

        Expect.equal
            entries[3].Facts
            (AuthorityHistoryFacts.Granted grant)
            "Typed case reference and role"

        Expect.equal
            entries[4].Facts
            (AuthorityHistoryFacts.Registered target)
            "Registered principal facts"

        Expect.equal
            entries[7].Facts
            (AuthorityHistoryFacts.OwnerProvisioned fixture.Owner)
            "Provision facts"

        Expect.isTrue
            (entries
             |> List.forall (fun entry -> entry.RecordedBy = entries[7].TargetActorId))
            "The executing actor is retained"

        let! first = read owner None None 2
        let! second = read owner None (page first).NextCursor 2
        let! third = read owner None (page second).NextCursor 2
        let! fourth = read owner None (page third).NextCursor 2
        let paged = [ first; second; third; fourth ] |> List.collect (page >> _.Entries)
        Expect.equal paged entries "Newest-first keyset pages cover the mixed ledger exactly once"
        Expect.isNone (page fourth).NextCursor "Final page has no continuation"
        let! filtered = read owner (Some(ActorId.value targetId)) None 50

        Expect.equal
            ((page filtered).Entries |> List.map (fun entry -> Sequence.value entry.Sequence))
            [ 9L; 8L; 7L; 6L; 4L ]
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
                (OperationInput.Case(
                    request 10 "HISTORY-ACCESS" 0L (AuthoredCaseAct.Open registration)
                ))
                Threading.CancellationToken.None

        accepted opened |> ignore
        let handler = principal "history-handler"
        let scoped = principal "history-scoped-auditor"
        let auditor = principal "history-auditor"
        let! _ = execute owner 11 handler AuthorityAct.Register

        let! _ =
            execute
                owner
                12
                handler
                (AuthorityAct.Grant
                    {
                        Role = Role.CaseHandler
                        Scope = GrantTarget.Installation
                    })

        let! _ = execute owner 13 scoped AuthorityAct.Register

        let! _ =
            execute
                owner
                14
                scoped
                (AuthorityAct.Grant
                    {
                        Role = Role.Auditor
                        Scope = GrantTarget.CaseReference "HISTORY-ACCESS"
                    })

        let! _ = execute owner 15 auditor AuthorityAct.Register

        let! _ =
            execute
                owner
                16
                auditor
                (AuthorityAct.Grant
                    {
                        Role = Role.Auditor
                        Scope = GrantTarget.Installation
                    })

        for denied in [ handler; scoped; principal "history-unknown" ] do
            let! result = read (core fixture denied) None (Some "malformed") 1

            Expect.equal
                result
                (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
                "Installation ReadAuthority is checked before cursor interpretation"

        let viewer = core fixture auditor
        let! first = read viewer None None 1
        Expect.isSome (page first).NextCursor "An installation auditor can read a bounded page"

        let! _ =
            execute
                owner
                20
                auditor
                (AuthorityAct.Revoke
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
            member _.Read operation =
                fixture.Store.Read
                    { operation with
                        Work =
                            fun tx ->
                                operation.Work
                                    { new IReadTx with
                                        member _.ObservedAt = tx.ObservedAt
                                        member _.Authority query = tx.Authority query
                                        member _.FindCase id = tx.FindCase id
                                        member _.FindEntry id = tx.FindEntry id
                                        member _.CasePage query = tx.CasePage query
                                        member _.CaseSummary query = tx.CaseSummary query
                                        member _.HistoryPage query = tx.HistoryPage query
                                        member _.ActorPage query = tx.ActorPage query
                                        member _.GrantPage query = tx.GrantPage query
                                        member _.FindActor id = tx.FindActor id
                                        member _.EnabledHumanOwners() = tx.EnabledHumanOwners()
                                        member _.ActorCount() = tx.ActorCount()

                                        member _.AuthorityHistoryPage query =
                                            task {
                                                let! page = tx.AuthorityHistoryPage query
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
        let ownerId = CaseFixtures.actorId (id 1)

        let changedNonce (page: Page<StoredEntry, Sequence>) =
            let first = page.Items.Head
            let nonce = first.PayloadNonce.Value |> Array.copy
            nonce[0] <- nonce[0] ^^^ 1uy

            { page with
                Items = { first with PayloadNonce = Some nonce } :: page.Items.Tail
            }

        let tampered = changingHistory fixture changedNonce
        let! result = read (core tampered fixture.Owner) None None 50

        Expect.equal
            result
            (Outcome.Failed CoreFault.OperationContentConflict)
            "Authority history verifies the salted payload commitment before exposing facts"

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

        let foreignTarget (page: Page<StoredEntry, Sequence>) =
            let first = page.Items.Head

            let subject =
                match first.Subject with
                | EntryTarget.Authority(_, act) ->
                    EntryTarget.Authority(CaseFixtures.actorId (id 99), act)
                | _ -> failtest "Expected an authority row"

            { page with
                Items = { first with Subject = subject } :: page.Items.Tail
            }

        let foreign = changingHistory fixture foreignTarget

        let! wrongTarget =
            read (core foreign fixture.Owner) (Some(ActorId.value ownerId)) None 50

        Expect.equal
            wrongTarget
            (Outcome.Failed CoreFault.StoreIntegrityError)
            "A filtered page cannot substitute a different target actor"
    }
