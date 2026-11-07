module ClaimCore.Tests.AuthorityDecisionTests

open ClaimCore.TestSupport

open Expecto
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.Tests.DecisionFixtures


let private lastOwner () =
    let target = actor owner ((CaseFixtures.actorId (id 1))) [ Role.Owner ] []
    let view = authorityView worker target None

    let last =
        { view with
            Read =
                { view.Read with
                    EnabledHumanOwners = Some 1
                }
        }

    for act in
        [
            AuthorityAct.Disable
            AuthorityAct.Revoke
                {
                    AuthoredGrant.Role = Role.Owner
                    Scope = GrantTarget.Installation
                }
        ] do
        let request = admitted 100 owner act

        Expect.equal
            (executeAuthority installation owner request last)
            (Verdict.Refuse(Refusal.Authority AuthorityRefusal.LastOwner))
            "The last enabled human owner remains protected"

let private humanOnly () =
    let target = actor service ((CaseFixtures.actorId (id 2))) [] []

    for role in [ Role.Owner; Role.DataSteward ] do
        let request =
            admitted
                101
                service
                (AuthorityAct.Grant
                    {
                        Role = role
                        Scope = GrantTarget.Installation
                    })

        Expect.equal
            (executeAuthority installation owner request (authorityView worker target None))
            (Verdict.Refuse(Refusal.Authority AuthorityRefusal.HumanPrincipalRequired))
            "Service principals cannot receive human-only grants"

    let view = authorityView AuthorityView.empty AuthorityView.empty None

    let genesis =
        { view with
            Head =
                { head with
                    Sequence = CaseFixtures.sequence 0L
                }
            Read =
                { view.Read with
                    ActorCount = Some 0
                    EnabledHumanOwners = Some 0
                }
        }

    Expect.equal
        (provisionAuthority installation ((CaseFixtures.operationId (id 102))) service genesis)
        (Verdict.Refuse(Refusal.Authority AuthorityRefusal.HumanPrincipalRequired))
        "Bootstrap also requires a human"

let private replay () =
    let request =
        admitted 103 other (AuthorityAct.Register ClaimCore.TestSupport.Inputs.actorProfile)

    let view = authorityView worker AuthorityView.empty None

    let entry, receipt =
        match executeAuthority installation owner request view with
        | Verdict.Proceed(entries,
                          [ AuthorityProjection.Register(target, principal)
                            AuthorityProjection.SetProfile _ ],
                          receipt) ->
            Expect.equal
                target
                (ActorId.ofOperationId (request.OperationId))
                "Registration owns the target identity"

            Expect.equal principal other "Exact authored principal"
            entries, receipt
        | _ -> failtest "Expected actor registration"

    let target = actor other (ActorId.ofOperationId (request.OperationId)) [] []

    let existing =
        authorityView
            worker
            target
            (Some(
                entry
                |> List.map LedgerEntries.stored
                |> StoredOperation.create
                |> Result.defaultWith (string >> failtest)
            ))

    let currentReceipt =
        match receipt.Subject with
        | ReceiptSubject.Authority subject ->
            { receipt with
                Subject =
                    ReceiptSubject.Authority
                        { subject with
                            TargetActor =
                                AuthorityTargetDisclosure.Disclosed
                                    {
                                        ActorId = ActorId.ofOperationId request.OperationId
                                        DisplayName = DisplayName.Erased
                                        PrincipalKind = PrincipalKind.Human
                                        Availability = ActorAvailability.Available
                                    }
                        }
            }
        | _ -> failtest "Expected authority acceptance"

    match executeAuthority installation owner request existing with
    | Verdict.Replay actual ->
        expectReceipt
            actual
            currentReceipt
            "Executor and position survive; target name follows the current empty profile"
    | other -> failtestf "Expected authority replay: %A" other

    let changed =
        AuthorityRequests.input
            (OperationId.value (request.OperationId))
            request.Target
            AuthorityAct.Disable
        |> Result.bind Authority.parse
        |> Result.defaultWith (string >> failtest)

    Expect.equal
        (executeAuthority installation owner changed existing)
        (Verdict.Refuse Refusal.IdempotencyConflict)
        "Changed content cannot reuse the ID"

    let revoked =
        { existing with
            Read =
                { existing.Read with
                    Actor = actor owner ((CaseFixtures.actorId (id 1))) [ Role.CaseReader ] []
                }
        }

    Expect.equal
        (executeAuthority installation owner request revoked)
        (Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable))
        "Current management precedes replay"

let private provision () =
    let initial = authorityView AuthorityView.empty AuthorityView.empty None

    let initial =
        { initial with
            Head =
                { head with
                    Sequence = CaseFixtures.sequence 0L
                }
            Read =
                { initial.Read with
                    ActorCount = Some 0
                    EnabledHumanOwners = Some 0
                }
        }

    let entry, receipt =
        match
            provisionAuthority installation ((CaseFixtures.operationId (id 104))) owner initial
        with
        | Verdict.Proceed(entries,
                          [ AuthorityProjection.ProvisionOwner(target, principal)
                            AuthorityProjection.SetProfile _ ],
                          receipt) ->
            Expect.equal
                (target, principal)
                ((CaseFixtures.actorId (id 104)), owner)
                "Bootstrap has explicit identity"

            entries, receipt
        | _ -> failtest "Expected explicit bootstrap"

    Expect.equal
        entry.Head.Subject
        (EntryTarget.Authority((CaseFixtures.actorId (id 104)), AuthorityKind.ProvisionOwner))
        "Bootstrap records its distinct durable kind"

    let target = actor owner ((CaseFixtures.actorId (id 104))) [ Role.Owner ] []

    let target =
        { target with
            Actor =
                target.Actor
                |> Option.map (fun actor ->
                    { actor with
                        Profile =
                            Some(
                                ActorProfile.parse Inputs.ownerProfile
                                |> Result.defaultWith (string >> failtest)
                            )
                    })
        }

    let view =
        authorityView
            target
            target
            (Some(
                entry
                |> List.map LedgerEntries.stored
                |> StoredOperation.create
                |> Result.defaultWith (string >> failtest)
            ))

    let currentReceipt =
        match receipt.Subject with
        | ReceiptSubject.Authority subject ->
            Expect.equal
                subject.TargetActor
                (AuthorityTargetDisclosure.Withheld(CaseFixtures.actorId (id 104)))
                "Initial bootstrap has no pre-existing authority read grant"

            { receipt with
                Subject =
                    ReceiptSubject.Authority
                        { subject with
                            TargetActor =
                                AuthorityTargetDisclosure.Disclosed
                                    {
                                        ActorId = CaseFixtures.actorId (id 104)
                                        DisplayName =
                                            DisplayName.Named(
                                                PartyName.parse Inputs.ownerProfile.DisplayName
                                                |> Result.defaultWith (string >> failtest)
                                            )
                                        PrincipalKind = PrincipalKind.Human
                                        Availability = ActorAvailability.Available
                                    }
                        }
            }
        | _ -> failtest "Expected owner provisioning acceptance"

    match provisionAuthority installation ((CaseFixtures.operationId (id 104))) owner view with
    | Verdict.Replay actual ->
        expectReceipt
            actual
            currentReceipt
            "Bootstrap replay preserves acceptance and uses current authority-read rights"
    | other -> failtestf "Expected bootstrap replay: %A" other

let private reviewAndFences () =
    let request =
        admitted 105 other (AuthorityAct.Register ClaimCore.TestSupport.Inputs.actorProfile)

    let view = authorityView worker AuthorityView.empty None

    match
        AuthorityExecution.review
            (DecisionFixtures.services installation calendar owner)
            request
            view.Read
    with
    | Verdict.Proceed {
                          Change = AuthorityChange.Registered _
                      } -> ()
    | _ -> failtest "Review must be a pure proposal"

    let scoped =
        actor
            owner
            ((CaseFixtures.actorId (id 1)))
            []
            [ (CaseFixtures.caseId (id 77)), [ Role.Owner ] ]

    Expect.isEmpty
        (Advice.installationOffers owner scoped)
        "A case grant cannot offer installation administration"

    Expect.isEmpty
        (Advice.installationOffers
            service
            (actor service ((CaseFixtures.actorId (id 2))) [ Role.Owner ] []))
        "Invalid service owner grants convey no offers"

    let installationOwner =
        actor owner ((CaseFixtures.actorId (id 1))) [ Role.Owner ] []

    Expect.equal
        (Advice.installationOffers owner installationOwner)
        [
            InstallationOffer.AuthorityAct AuthorityKind.Register
            InstallationOffer.AuthorityAct AuthorityKind.Grant
            InstallationOffer.AuthorityAct AuthorityKind.Revoke
            InstallationOffer.AuthorityAct AuthorityKind.Enable
            InstallationOffer.AuthorityAct AuthorityKind.Disable
            InstallationOffer.AuthorityAct AuthorityKind.SetProfile
            InstallationOffer.AuthorityAct AuthorityKind.EraseProfile
            InstallationOffer.AuthorityAct AuthorityKind.Admit
        ]
        "Installation offers contain only ordinary operations; owner provisioning uses its dedicated bootstrap boundary"

let private meaninglessScope () =
    let caseId = CaseFixtures.caseId (id 77)

    let target: ActorAuthority =
        {
            Actor =
                {
                    ActorId = CaseFixtures.actorId (id 2)
                    Principal = other
                    Enabled = true
                    Profile = None
                }
            Grants = []
        }

    let facts: AuthorityFacts =
        {
            TargetActor = Some target
            TargetCases = Map.ofList [ GrantTarget.CaseId caseId, caseId ]
            EnabledHumanOwners = Some 2
            ActorCount = Some 2
            Requester = worker
        }

    let command role scope =
        admitted 106 other (AuthorityAct.Grant { Role = role; Scope = scope })

    Expect.isError
        (Authority.decide facts (command Role.Owner (GrantTarget.CaseId caseId)))
        "A case OWNER grant has no potential capability"

    for role, scope in
        [
            Role.Owner, GrantTarget.Installation
            Role.CaseReader, GrantTarget.CaseId caseId
            Role.CaseHandler, GrantTarget.CaseId caseId
        ] do
        Expect.isOk
            (Authority.decide facts (command role scope))
            "Meaningful scopes remain admissible"

    let disabled =
        { facts with
            TargetActor =
                Some
                    { target with
                        Actor = { target.Actor with Enabled = false }
                    }
        }

    Expect.isOk
        (Authority.decide disabled (command Role.CaseReader (GrantTarget.CaseId caseId)))
        "Disabled targets may receive dormant rights"

    let overlapping =
        { facts with
            TargetActor =
                Some
                    { target with
                        Grants =
                            [
                                {
                                    Role = Role.CaseHandler
                                    Scope = Scope.Case caseId
                                }
                            ]
                    }
        }

    Expect.isOk
        (Authority.decide overlapping (command Role.CaseReader (GrantTarget.CaseId caseId)))
        "Overlapping roles remain meaningful"

let private accessExplanation () =
    let caseId = CaseFixtures.caseId (id 77)

    let target =
        actor
            other
            (CaseFixtures.actorId (id 2))
            [ Role.CaseRegistrar ]
            [ caseId, [ Role.CaseReader ] ]

    let explained = Authority.explain other target (Scope.Case caseId)

    let expected =
        [
            {
                Capability = Capability.ReadCase
                DirectRoles = [ Role.CaseReader ]
                InstallationRoles = [ Role.CaseRegistrar ]
                Effective = true
            }
            {
                Capability = Capability.ReadHistory
                DirectRoles = [ Role.CaseReader ]
                InstallationRoles = [ Role.CaseRegistrar ]
                Effective = true
            }
            {
                Capability = Capability.Register
                DirectRoles = []
                InstallationRoles = [ Role.CaseRegistrar ]
                Effective = true
            }
        ]

    Expect.equal explained expected "Scoped and inherited overlapping sources are explicit"

    let disabled =
        { target with
            Actor = target.Actor |> Option.map (fun actor -> { actor with Enabled = false })
        }

    Expect.equal
        (Authority.explain other disabled (Scope.Case caseId))
        (expected |> List.map (fun value -> { value with Effective = false }))
        "Disabled actors retain dormant sources without effective rights"

    let noScoped = { target with Cases = Map.empty }

    Expect.equal
        (Authority.explain other noScoped (Scope.Case caseId))
        (expected |> List.map (fun value -> { value with DirectRoles = [] }))
        "Removing one overlapping grant does not claim inherited access disappeared"

    let facts: AuthorityFacts =
        {
            TargetActor =
                Some
                    {
                        Actor =
                            {
                                ActorId = CaseFixtures.actorId (id 3)
                                Principal = service
                                Enabled = true
                                Profile = None
                            }
                        Grants =
                            [
                                {
                                    Role = Role.Owner
                                    Scope = Scope.Case caseId
                                }
                            ]
                    }
            TargetCases = Map.ofList [ GrantTarget.CaseId caseId, caseId ]
            EnabledHumanOwners = Some 2
            ActorCount = Some 3
            Requester = worker
        }

    let revoke =
        admitted
            107
            service
            (AuthorityAct.Revoke
                {
                    Role = Role.Owner
                    Scope = GrantTarget.CaseId caseId
                })

    Expect.equal
        (Authority.decide facts revoke)
        (Ok(AuthorityChange.Revoked(Role.Owner, Scope.Case caseId)))
        "Recognized inadmissible grants remain removable"

    let owner =
        Catalog.document.Roles |> List.find (fun value -> value.Role = Role.Owner)

    Expect.equal
        owner.AdmittedScopes
        [ ScopeKind.Installation ]
        "Published owner scopes are meaningful"

    Expect.equal
        owner.AdmittedPrincipalKinds
        [ PrincipalKind.Human ]
        "Published owner admission remains human-only"

[<Tests>]
let tests =
    testList
        "pure authority decisions"
        [
            testCase
                "access review explains scoped overlapping and dormant rights and permits obsolete grant cleanup"
                accessExplanation
            testCase
                "meaningless case OWNER refuses while meaningful dormant and overlapping grants remain valid"
                meaninglessScope
            testCase "last-owner protection is a pure locked-view decision" lastOwner
            testCase "human-only grants and bootstrap refuse service principals" humanOnly
            testCase
                "authority replay preserves attribution conflicts and current permissions"
                replay
            testCase "owner provisioning has explicit idempotent identity" provision
            testCase "review fences and installation offers use current authority" reviewAndFences
        ]
