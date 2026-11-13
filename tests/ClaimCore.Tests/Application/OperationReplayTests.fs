module ClaimCore.Tests.OperationReplayTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

let private valid result =
    Result.defaultWith (fun error -> failtestf "Root replay witness: %A" error) result

let private installation = Guid.Parse "00000000-0000-4000-8000-000000000001"

let private operation number =
    Guid.Parse(sprintf "00000000-0000-7000-8000-%012d" number)
    |> OperationId.create
    |> Option.get

let private actor number =
    ActorId.ofOperationId (operation number)

let private principal number =
    PrincipalKey.human "https://issuer.example/" ("actor-" + string number) |> valid

let private profile name =
    ActorProfile.parse { DisplayName = name; Contact = None } |> valid

let private nonce number () =
    Array.init 32 (fun index -> byte ((number + index) % 256))

let private people number (head: LedgerPosition) executor target act saved =
    let header =
        {
            OperationHeader.InstallationId = installation
            Sequence = Sequence.successor head.Sequence
            PreviousHash = head.EntryHash
            OperationId = operation number
            ActorId = executor
            RecordedAt = DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero)
            BusinessDate = None
        }

    RetainedOperation.create
        header
        (CommandSkeleton.People
            {
                TargetActorId = actor target
                Principal = principal target
                Act = act
            })
        (OperationValues.People { Profile = saved })
        (nonce number)
    |> valid

let private erased (value: RetainedOperation) =
    OperationRoot.restore
        value.Root.Header
        (OperationRoot.commandBytes value.Root)
        (OperationRoot.effectBytes value.Root)
        (value.Root.Structure.Compartments |> List.map Compartment.withoutContent)
        value.Root.Hash
    |> valid
    |> RetainedOperation.decode
    |> valid

let private append state root =
    Replay.append installation None state root |> valid

let private owner () =
    people
        1
        LedgerPosition.genesis
        (actor 1)
        1
        (PeopleAct.ProvisionOwner(0, People.initialOwnerRoles))
        (Some(profile "Owner"))

[<Tests>]
let tests =
    testList
        "operation root replay"
        [
            testCase
                "bootstrap registers profile and roles under one pre-operation decision"
                (fun () ->
                    let root = owner ()
                    let state = append (Replay.initial None) root

                    Expect.equal
                        state.Head.Sequence
                        Sequence.first
                        "A compound operation advances once"

                    Expect.equal state.Actors.Count 1 "One actor registered"
                    let current = state.Actors[actor 1]

                    Expect.equal
                        current.Actor.Profile
                        (Some(profile "Owner"))
                        "Typed profile applied"

                    for role in Role.Owner :: People.initialOwnerRoles do
                        Expect.contains
                            current.Grants
                            {
                                Role = role
                                Scope = Scope.Installation
                            }
                            "All declared grants applied"

                    Expect.equal
                        (EffectPosition.index
                            state.GrantSources[(actor 1, Role.Owner, Scope.Installation)])
                        0
                        "Implicit owner grant belongs to registration effect"

                    Expect.equal
                        (EffectPosition.index
                            state.GrantSources[(actor 1, Role.CaseHandler, Scope.Installation)])
                        2
                        "Initial role provenance uses its effect ordinal"

                    Expect.isOk (Replay.erasure state) "Complete profile needs no erase evidence")
            testCase
                "profile erasure does not skip mixed registration and grant semantics"
                (fun () ->
                    let root = owner ()
                    let initial = append (Replay.initial None) root

                    let grants =
                        [
                            {
                                Role = Role.Auditor
                                Scope = Scope.Installation
                            }
                            {
                                Role = Role.CaseHandler
                                Scope = Scope.Installation
                            }
                        ]

                    let admitted =
                        people
                            2
                            initial.Head
                            (actor 1)
                            2
                            (PeopleAct.Admit { Profile = 0; Grants = grants })
                            (Some(profile "Removed profile"))

                    let beforeErase = append initial (erased admitted)

                    Expect.equal
                        (beforeErase.Actors[actor 2].Grants |> Set.ofList)
                        (Set.ofList grants)
                        "Unavailable profile never drops retained grants"

                    Expect.isNone
                        beforeErase.Actors[actor 2].Actor.Profile
                        "No dummy profile is invented"

                    Expect.isError
                        (Replay.erasure beforeErase)
                        "Unexplained private loss remains corruption"

                    let erase = people 3 beforeErase.Head (actor 1) 2 PeopleAct.EraseProfile None

                    let after = append beforeErase erase

                    Expect.isOk
                        (Replay.erasure after)
                        "Later accepted owner erase accounts for the missing profile"

                    let later =
                        people
                            4
                            after.Head
                            (actor 1)
                            2
                            (PeopleAct.SetProfile 0)
                            (Some(profile "Later profile"))

                    let after = append after later

                    Expect.equal
                        after.Actors[actor 2].Actor.Profile
                        (Some(profile "Later profile"))
                        "Later re-profile survives"

                    Expect.equal
                        (after.Actors[actor 2].Grants |> Set.ofList)
                        (Set.ofList grants)
                        "Re-profile never changes earlier grants"

                    Expect.isOk
                        (Replay.erasure after)
                        "Earlier erase does not require later profile clearing")
            testCase "an erased profile cannot grant people to execute its own command" (fun () ->
                let root = owner ()
                let initial = append (Replay.initial None) root

                let grants =
                    [
                        {
                            Role = Role.Owner
                            Scope = Scope.Installation
                        }
                    ]

                let self =
                    people
                        2
                        initial.Head
                        (actor 2)
                        2
                        (PeopleAct.Admit { Profile = 0; Grants = grants })
                        (Some(profile "Untrusted new owner"))
                    |> erased

                match Replay.append installation None initial self with
                | Error finding ->
                    Expect.equal
                        finding.Finding
                        VerificationFinding.PeopleFold
                        "Command authorization uses earlier requester state"
                | Ok _ ->
                    failtest "Post-registration owner permission authorized an earlier command")
            testCase
                "available profile content before an accepted erase is independently detected"
                (fun () ->
                    let root = owner ()
                    let initial = append (Replay.initial None) root

                    let registered =
                        people
                            2
                            initial.Head
                            (actor 1)
                            2
                            (PeopleAct.Register 0)
                            (Some(profile "Still available"))

                    let before = append initial registered
                    let erase = people 3 before.Head (actor 1) 2 PeopleAct.EraseProfile None

                    match Replay.append installation None before erase with
                    | Error finding ->
                        Expect.equal
                            finding.Finding
                            VerificationFinding.ErasureIncomplete
                            "Accepted erase requires clearing earlier private bytes"
                    | Ok _ -> failtest "Available earlier profile survived erase verification")
        ]

[<Tests>]
let erasedPayments =
    testCase
        "erased case replay reconstructs payment membership and removal markers from public roots"
        (fun () ->
            let bootstrap = owner ()
            let mutable head = bootstrap.Root.Position
            let mutable current: CaseSnapshot option = None
            let mutable roots = [ bootstrap ]

            let case number act context =
                let input =
                    Case.parse
                        {
                            OperationId = OperationId.value (operation number)
                            CaseReference = "REPLAY-PAYMENTS"
                            ExpectedRevision =
                                current
                                |> Option.map (_.Revision >> Revision.value)
                                |> Option.defaultValue 0L
                            Act = act
                        }
                    |> valid

                let decision =
                    Case.decideSnapshot (DateOnly(2026, 11, 9)) (context current) input current
                    |> valid

                let selected =
                    match input.Act with
                    | CaseAct.RemovePayment id -> Some id
                    | _ -> None

                let scope =
                    CommandSkeleton.Case
                        {
                            CaseId = decision.After.CaseId
                            Reference = decision.After.Reference
                            ExpectedRevision = input.ExpectedRevision
                            Act = CaseAct.kind input.Act
                            SelectedPayment = selected
                        }

                let header =
                    { bootstrap.Root.Header with
                        Sequence = Sequence.successor head.Sequence
                        PreviousHash = head.EntryHash
                        OperationId = operation number
                        BusinessDate = Some(DateOnly(2026, 11, 9))
                    }

                let root =
                    RetainedOperation.create
                        header
                        scope
                        (OperationValues.Case { Command = input; Decision = decision })
                        (nonce number)
                    |> valid

                head <- root.Root.Position
                current <- Some decision.After
                roots <- roots @ [ root ]
                decision

            let registration =
                RegistrationInput.create
                    [
                        BusinessField.IncidentDate, "2026-01-01"
                        BusinessField.IncidentNotificationDate, "2026-01-02"
                        BusinessField.IncidentCountry, "LV"
                        BusinessField.ClaimantName, "Synthetic claimant"
                        BusinessField.InsurerName, "Synthetic insurer"
                        BusinessField.ClaimedAmount, "100"
                        BusinessField.ClaimedCurrency, "EUR"
                    ]
                |> valid

            let decision =
                DecisionInput.create
                    [
                        BusinessField.PaymentDecisionDate, "2026-01-03"
                        BusinessField.PayableAmount, "100"
                        BusinessField.PayableCurrency, "EUR"
                    ]
                |> valid

            let payment amount =
                PaymentInput.create
                    [
                        BusinessField.PaymentDate, "2026-01-04"
                        BusinessField.PaidAmount, amount
                        BusinessField.PaidCurrency, "EUR"
                    ]
                |> valid
                |> CaseAct.RecordPayment

            let noContext _ = Map.empty
            case 2 (CaseAct.Open registration) noContext |> ignore
            case 3 (CaseAct.Decide decision) noContext |> ignore

            let recording (_before: CaseSnapshot option) = Map.empty

            let first = case 4 (payment "10") recording
            let second = case 5 (payment "5") recording

            let selected =
                match first.Payment with
                | Some(PaymentDifference.Recorded entry) -> entry
                | _ -> failtest "Recorded entry required"

            let removal (before: CaseSnapshot option) =
                let before = before.Value in

                let remaining =
                    match second.Payment with
                    | Some(PaymentDifference.Recorded entry) -> entry
                    | _ -> failtest "Second recorded payment required"

                ClaimCore.TestSupport.CaseFixtures.paymentRows
                    before.CaseId
                    [ selected; remaining ]

            case 6 (CaseAct.RemovePayment(OperationId.value (operation 4))) removal
            |> ignore

            case 7 (CaseAct.Bare BareAct.Erase) noContext |> ignore

            let cleared =
                roots
                |> List.map (fun value ->
                    match value.Root.Structure.Command with
                    | CommandSkeleton.Case _ -> erased value
                    | _ -> value)

            let actual = cleared |> List.fold append (Replay.initial None)

            Expect.isOk
                (Replay.erasure actual)
                "Accepted case erasure accounts for all unavailable case inputs/results"

            let caseId = CaseId.ofOperationId (operation 2)
            let payments = actual.Cases[caseId].PaymentProjections
            Expect.equal payments.Count 2 "Every accepting payment identity remains reserved"

            Expect.equal
                payments[operation 4].Content
                (PaymentProjectionContent.Cleared true)
                "Erased removal target remains removed"

            Expect.equal
                payments[operation 5].Content
                (PaymentProjectionContent.Cleared false)
                "Another payment is never marked removed by inference"

            Expect.equal
                actual.Cases[caseId].State
                CaseState.Erased
                "No private financial position is invented"

            Expect.equal actual.Head head "The operation head advances once per root")
