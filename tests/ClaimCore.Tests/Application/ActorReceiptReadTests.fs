module ClaimCore.Tests.ActorReceiptReadTests

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Protocol
open ClaimCore.Json
open ClaimCore.TestSupport

let private id number =
    Guid.Parse(sprintf "e93eb71c-1904-4604-acb9-%012d" number)

let private get (task: System.Threading.Tasks.Task<'value>) = task.GetAwaiter().GetResult()

let private accepted =
    function
    | Ok value -> value
    | Error error -> failtestf "%A" error

let private principal subject =
    PrincipalKey.human "https://receipt-names.example/" subject |> accepted

let private actor number name =
    {
        Actor.ActorId = CaseFixtures.actorId (id number)
        Principal = principal name
        Enabled = true
        Profile = ActorProfile.parse { DisplayName = name; Contact = None } |> accepted |> Some
    }

let private calendar =
    { new IBusinessCalendar with
        member _.TimeZoneId = "Etc/UTC"

        member _.DateAt instant =
            DateOnly.FromDateTime instant.UtcDateTime
    }

let private core store principal =
    Service.create "fixture-version" (id 1) store calendar principal

let private execute service input =
    (Service.execute service input CancellationToken.None |> get).Outcome
    |> function
        | ExecuteResult.Accepted receipt -> receipt
        | other -> failtestf "%A" other

let private authority number target act =
    let kind, issuer, subject = PrincipalKey.parts target

    OperationInput.Authority
        {
            OperationId = id number
            Target =
                {
                    Kind = kind
                    Issuer = issuer
                    Subject = subject
                }
            Act = act
        }

let private target (receipt: OperationReceipt) =
    match receipt.Subject with
    | ReceiptSubject.Authority receipt -> receipt.TargetActor
    | other -> failtestf "%A" other

let private status service operation =
    (Service.status service { OperationId = operation } CancellationToken.None |> get).Outcome
    |> function
        | Outcome.Succeeded(OperationStatus.Accepted receipt) -> receipt
        | other -> failtestf "%A" other

[<Tests>]
let tests =
    testList
        "current actor receipt read projections"
        [
            testCase
                "missing or mismatched authorized work references are integrity errors"
                (fun () ->
                    let parent = CaseFixtures.caseId (id 90)
                    let identity = CaseFixtures.actorId (id 91)

                    let snapshot: CaseWorkSnapshot =
                        {
                            CaseId = parent
                            Revision = Revision.first
                            Work =
                                {
                                    Responsible = Some identity
                                    Summary = None
                                }
                        }

                    Expect.equal
                        (CaseWorkReadModels.snapshot Map.empty snapshot)
                        (Error CoreFault.StoreIntegrityError)
                        "An absent authorized account cannot become an invented erased or unknown name"

                    let other =
                        Inputs.namedActorReference (CaseFixtures.actorId (id 92)) "Wrong person"

                    Expect.equal
                        (CaseWorkReadModels.snapshot
                            (Map.ofList [ (parent, identity), other ])
                            snapshot)
                        (Error CoreFault.StoreIntegrityError)
                        "A map key cannot disguise a different actor identity")
            testCase
                "own receipt metadata survives lost authority without disclosing target names"
                (fun () ->
                    use store =
                        new MemoryStore.Store(
                            id 1,
                            DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero)
                        )

                    let caller, guardian, person =
                        actor 2 "caller", actor 3 "guardian", actor 4 "target"

                    store.SeedActor caller [ Role.Owner, None ]
                    store.SeedActor guardian [ Role.Owner, None ]
                    store.SeedActor person [ Role.CaseReader, None ]
                    let callerCore = core (store :> IStore) caller.Principal
                    let guardianCore = core (store :> IStore) guardian.Principal

                    let receipt =
                        execute
                            callerCore
                            (authority
                                10
                                person.Principal
                                (AuthorityAct.SetProfile
                                    {
                                        DisplayName = "Saved target"
                                        Contact = None
                                    }))

                    match target receipt with
                    | AuthorityTargetDisclosure.Disclosed actor ->
                        Expect.equal
                            actor.DisplayName
                            (DisplayName.Named(Inputs.name "Saved target"))
                            "Accepted profile changes show the proposed current saved name"
                    | _ -> failtest "Authorized target must be disclosed"

                    execute
                        guardianCore
                        (authority
                            11
                            caller.Principal
                            (AuthorityAct.Revoke
                                {
                                    Role = Role.Owner
                                    Scope = GrantTarget.Installation
                                }))
                    |> ignore

                    execute
                        guardianCore
                        (authority
                            12
                            person.Principal
                            (AuthorityAct.SetProfile
                                {
                                    DisplayName = "Current private target"
                                    Contact = None
                                }))
                    |> ignore

                    let hidden = status callerCore (id 10)

                    Expect.equal
                        (target hidden)
                        (AuthorityTargetDisclosure.Withheld person.ActorId)
                        "Known target metadata remains while current profile details are withheld"

                    let wire =
                        ReceiptCodecs.receipt.Write hidden
                        |> WireWriter.bytes
                        |> Text.Encoding.UTF8.GetString

                    Expect.isFalse
                        (wire.Contains "Current private target" || wire.Contains "Saved target")
                        "Losing ReadAuthority cannot expose another account current or former name"

                    execute
                        guardianCore
                        (authority
                            13
                            caller.Principal
                            (AuthorityAct.Grant
                                {
                                    Role = Role.Owner
                                    Scope = GrantTarget.Installation
                                }))
                    |> ignore

                    match target (status callerCore (id 10)) with
                    | AuthorityTargetDisclosure.Disclosed actor ->
                        Expect.equal
                            actor.DisplayName
                            (DisplayName.Named(Inputs.name "Current private target"))
                            "Regrant restores the current name rather than the historical name"
                    | _ -> failtest "Regranted authority must disclose current target"

                    execute guardianCore (authority 14 person.Principal AuthorityAct.EraseProfile)
                    |> ignore

                    match target (status callerCore (id 10)) with
                    | AuthorityTargetDisclosure.Disclosed actor ->
                        Expect.equal
                            actor.DisplayName
                            DisplayName.Erased
                            "Actual profile erasure is distinct from withheld authorization"
                    | _ -> failtest "Authorized erased profile remains a disclosed ActorRef"

                    let disabled =
                        execute guardianCore (authority 15 person.Principal AuthorityAct.Disable)

                    match target disabled with
                    | AuthorityTargetDisclosure.Disclosed actor ->
                        Expect.equal
                            actor.Availability
                            ActorAvailability.Disabled
                            "Accepted disable transitions use the current post-state"
                    | _ -> failtest "Authorized disabled target remains disclosed")
            testCase
                "work before and after share batched current names and retain erasure priorities"
                (fun () ->
                    use store =
                        new MemoryStore.Store(
                            id 1,
                            DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero)
                        )

                    let caller, alice, bob, reader =
                        actor 20 "work caller", actor 21 "Alice", actor 22 "Bob", actor 23 "reader"

                    store.SeedActor
                        caller
                        [ Role.Owner, None; Role.CaseHandler, None; Role.DataSteward, None ]

                    let service = core (store :> IStore) caller.Principal

                    execute
                        service
                        (Inputs.caseOperation
                            {
                                OperationId = id 24
                                CaseReference = "NAMED-WORK"
                                ExpectedRevision = 0L
                                Act = CaseAct.Open Inputs.registration
                            })
                    |> ignore

                    let parent = CaseId.ofOperationId (CaseFixtures.operationId (id 24))
                    store.SeedActor alice [ Role.CaseHandler, Some parent ]
                    store.SeedActor bob [ Role.CaseHandler, Some parent ]
                    store.SeedActor reader [ Role.CaseReader, Some parent ]

                    let change number revision responsible =
                        OperationInput.Work
                            {
                                OperationId = id number
                                Target = CaseSelector.Reference "NAMED-WORK"
                                ExpectedRevision = revision
                                Act =
                                    WorkAct.UpdateWork
                                        {
                                            Responsible = WorkValue.Set(ActorId.format responsible)
                                            Summary = WorkValue.Set "Private handling summary"
                                        }
                            }

                    execute service (change 25 0L alice.ActorId) |> ignore
                    let next = change 26 1L bob.ActorId

                    let review =
                        (Service.preview service next CancellationToken.None |> get).Outcome
                        |> function
                            | Outcome.Succeeded(Previewed.Reviewed(OperationReview.Work review)) ->
                                review
                            | other -> failtestf "%A" other

                    Expect.equal
                        (review.Before.Responsible |> Option.map _.DisplayName)
                        (Some(DisplayName.Named(Inputs.name "Alice")))
                        "Before actor on the same case remains present"

                    Expect.equal
                        (review.Proposed.Responsible |> Option.map _.DisplayName)
                        (Some(DisplayName.Named(Inputs.name "Bob")))
                        "Proposed actor does not overwrite the before relation"

                    execute service next |> ignore

                    execute
                        service
                        (authority
                            27
                            alice.Principal
                            (AuthorityAct.SetProfile
                                {
                                    DisplayName = "Current Alice"
                                    Contact = None
                                }))
                    |> ignore

                    let observe service =
                        (Service.observe service { OperationId = id 25 } CancellationToken.None
                         |> get)
                            .Outcome
                        |> function
                            | Outcome.Succeeded receipt -> receipt
                            | other -> failtestf "%A" other

                    match (observe service).Subject with
                    | ReceiptSubject.Work {
                                              Snapshot = WorkDisclosure.Disclosed view
                                          } ->
                        Expect.equal
                            (view.Responsible |> Option.map _.DisplayName)
                            (Some(DisplayName.Named(Inputs.name "Current Alice")))
                            "Historical work uses the current saved name"
                    | other -> failtestf "%A" other

                    match (observe (core (store :> IStore) reader.Principal)).Subject with
                    | ReceiptSubject.Work { Snapshot = WorkDisclosure.Withheld } -> ()
                    | other ->
                        failtestf "Old version must remain withheld without full history: %A" other

                    execute
                        service
                        (Inputs.caseOperation
                            {
                                OperationId = id 28
                                CaseReference = "NAMED-WORK"
                                ExpectedRevision = 1L
                                Act = CaseAct.Bare BareAct.Erase
                            })
                    |> ignore

                    match (observe service).Subject with
                    | ReceiptSubject.Work { Snapshot = WorkDisclosure.Erased } -> ()
                    | other -> failtestf "Erasure wins over full-history rights: %A" other)
        ]
