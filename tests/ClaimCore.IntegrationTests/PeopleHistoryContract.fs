module ClaimCore.TestSupport.PeopleHistoryContract

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
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

let private execute fixture actor number target taskName values =
    task {
        let! input =
            personSubmission fixture.Store (id fixture number) target None taskName values

        let! reply = Service.executeSubmission actor input Threading.CancellationToken.None
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
                (fixture)
                (owner)
                (10)
                (target)
                "people.register"
                (ClaimCore.TestSupport.Submissions.profile (
                    ClaimCore.TestSupport.Inputs.actorProfile
                ))

        let targetId =
            match registered.Subject with
            | ReceiptSubject.People value -> PeopleTargetDisclosure.actorId value.TargetActor
            | _ -> failtest "Expected people subject"

        let! opened =
            Service.executeSubmission
                owner
                (((request
                    (fixture)
                    (11)
                    ("HISTORY-CASE")
                    (0L)
                    "case.open"
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values (registration)
                    )))))
                Threading.CancellationToken.None

        accepted opened |> ignore

        let grant =
            {
                Role = Role.CaseReader
                Scope = GrantTarget.CaseReference "HISTORY-CASE"
            }

        let! _ =
            execute
                (fixture)
                (owner)
                (12)
                (target)
                "people.grant"
                (ClaimCore.TestSupport.Submissions.grant (grant))

        let! _ =
            execute
                (fixture)
                (owner)
                (13)
                (target)
                "people.revoke"
                (ClaimCore.TestSupport.Submissions.grant (grant))

        let! _ = execute (fixture) (owner) (14) (target) "people.disable" (Map.empty)
        let! _ = execute (fixture) (owner) (15) (target) "people.enable" (Map.empty)
        let! all = read owner None None 50
        let entries = (page all).Entries

        Expect.equal
            (entries |> List.map (fun entry -> EffectPosition.format entry.Position))
            [ "9/0"; "8/0"; "7/0"; "6/0"; "4/0"; "3/0"; "2/0"; "1/0" ]
            "Mixed case entries never enter people history"

        Expect.equal
            (entries |> List.map (fun entry -> entry.Task))
            [
                (TaskId.ofLiteral "people.enable")
                (TaskId.ofLiteral "people.disable")
                (TaskId.ofLiteral "people.revoke")
                (TaskId.ofLiteral "people.grant")
                (TaskId.ofLiteral "people.register")
                (TaskId.ofLiteral "people.grant")
                (TaskId.ofLiteral "people.grant")
                (TaskId.ofLiteral "people.provisionOwner")
            ]
            "Every people act has independently specified chronological meaning"

        Expect.equal
            entries[3].Changes.Grants.Head.Grant
            {
                ActorGrant.Role = Role.CaseReader
                Scope = Scope.Case(CaseFixtures.caseId (id fixture 11))
            }
            "Typed immutable case identity and role"

        Expect.equal
            (entries[4].Changes.Principal |> Option.bind _.After)
            (Some target)
            "Registered principal facts"

        Expect.equal
            (entries[7].Changes.Principal |> Option.bind _.After)
            (Some fixture.Owner)
            "Provision principal facts"

        Expect.isTrue
            (entries
             |> List.forall (fun entry -> entry.RecordedBy.ActorId = entries[7].Target.ActorId))
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
            ((page filtered).Entries
             |> List.map (fun entry -> EffectPosition.format entry.Position))
            [ "9/0"; "8/0"; "7/0"; "6/0"; "4/0" ]
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
            Service.executeSubmission
                owner
                (((request
                    (fixture)
                    (10)
                    ("HISTORY-ACCESS")
                    (0L)
                    "case.open"
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values (registration)
                    )))))
                Threading.CancellationToken.None

        accepted opened |> ignore
        let handler = principal "history-handler"
        let scoped = principal "history-scoped-auditor"
        let auditor = principal "history-auditor"

        let! _ =
            execute
                (fixture)
                (owner)
                (11)
                (handler)
                "people.register"
                (ClaimCore.TestSupport.Submissions.profile (
                    ClaimCore.TestSupport.Inputs.actorProfile
                ))

        let! _ =
            execute
                (fixture)
                (owner)
                (12)
                (handler)
                "people.grant"
                (ClaimCore.TestSupport.Submissions.grant (
                    {
                        Role = Role.CaseHandler
                        Scope = GrantTarget.Installation
                    }
                ))

        let! _ =
            execute
                (fixture)
                (owner)
                (13)
                (scoped)
                "people.register"
                (ClaimCore.TestSupport.Submissions.profile (
                    ClaimCore.TestSupport.Inputs.actorProfile
                ))

        let! _ =
            execute
                (fixture)
                (owner)
                (14)
                (scoped)
                "people.grant"
                (ClaimCore.TestSupport.Submissions.grant (
                    {
                        Role = Role.Auditor
                        Scope = GrantTarget.CaseReference "HISTORY-ACCESS"
                    }
                ))

        let! _ =
            execute
                (fixture)
                (owner)
                (15)
                (auditor)
                "people.register"
                (ClaimCore.TestSupport.Submissions.profile (
                    ClaimCore.TestSupport.Inputs.actorProfile
                ))

        let! _ =
            execute
                (fixture)
                (owner)
                (16)
                (auditor)
                "people.grant"
                (ClaimCore.TestSupport.Submissions.grant (
                    {
                        Role = Role.Auditor
                        Scope = GrantTarget.Installation
                    }
                ))

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
                (fixture)
                (owner)
                (20)
                (auditor)
                "people.revoke"
                (ClaimCore.TestSupport.Submissions.grant (
                    {
                        Role = Role.Auditor
                        Scope = GrantTarget.Installation
                    }
                ))

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

let internal integrity fixture =
    task {
        let! _ = caseWorker fixture

        let! original = read (core fixture fixture.Owner) None None 50
        let initial = page original

        let profileRoot =
            initial.Entries |> List.find (fun entry -> entry.Changes.ProfilePresent.IsSome)

        let! retained =
            fixture.Store.Read
                {
                    Work = (fun tx -> LedgerReads.findOperation tx profileRoot.OperationId)
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
                compartment.Descriptor
                compartment.Commitment
                (Some nonce)
                (Some bytes))
            (Error CompartmentProblem.CommitmentMismatch)
            "A corrupt salt cannot become a typed history effect"


    }
