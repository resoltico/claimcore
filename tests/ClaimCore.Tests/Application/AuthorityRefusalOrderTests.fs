module ClaimCore.Tests.AuthorityRefusalOrderTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Tests.DecisionFixtures
open ClaimCore.TestSupport.StoreFixtures

[<RequireQualifiedAccess>]
type private Attempt =
    | Authored of AuthoredAuthorityAct
    | Provision

let private refusal =
    function
    | Verdict.Proceed _ -> None
    | Verdict.Refuse reason -> Some reason
    | Verdict.Replay _ -> failtest "No characterization input has an existing operation."
    | Verdict.Fail fault -> failtestf "Unexpected characterization fault: %A" fault

[<NoEquality; NoComparison>]
type private AuthorityRefusalScenario =
    {
        Name: string
        Caller: PrincipalKey * AuthorityView
        Attempt: Attempt
        Target: PrincipalKey * AuthorityView
        EnabledOwners: int
        Actors: int
        Current: StoredCase option
        Expected: Refusal option
    }

let private ordering () =
    let manager = owner, worker
    let missingManager = owner, AuthorityView.empty
    let serviceManager = service, actor service (id 603) [ Role.Owner ] []
    let missingHuman = other, AuthorityView.empty
    let missingService = service, AuthorityView.empty
    let human = other, actor other (id 602) [] []
    let serviceActor = service, actor service (id 603) [] []
    let lastOwner = owner, actor owner (id 1) [ Role.Owner ] []
    let secondOwner = other, actor other (id 602) [ Role.Owner ] []
    let disabled = actor other (id 602) [ Role.Owner ] []

    let disabledOwner =
        other,
        { disabled with
            Actor = disabled.Actor |> Option.map (fun actor -> { actor with Enabled = false })
        }

    let _, _, knownCase, _ = created 604 "AUTH-SCOPE"
    let caseScope = GrantTarget.CaseReference "AUTH-SCOPE"

    let grant role scope =
        Attempt.Authored(AuthorityAct.Grant { Role = role; Scope = scope })

    let revoke role scope =
        Attempt.Authored(AuthorityAct.Revoke { Role = role; Scope = scope })

    let register = Attempt.Authored AuthorityAct.Register
    let enable = Attempt.Authored AuthorityAct.Enable
    let disable = Attempt.Authored AuthorityAct.Disable

    // Hand-authored expectations pin refusal priority, not the current implementation's output.
    // Actor counts include other unloaded actors; last-owner rows target the requesting owner itself.
    let rows =
        [
            {
                Name = "register missing human"
                Caller = manager
                Attempt = register
                Target = missingHuman
                EnabledOwners = 2
                Actors = 5
                Current = None
                Expected = None
            }
            {
                Name = "register existing human"
                Caller = manager
                Attempt = register
                Target = human
                EnabledOwners = 2
                Actors = 5
                Current = None
                Expected = Some(Refusal.Authority AuthorityRefusal.ActorAlreadyRegistered)
            }
            {
                Name = "register missing service"
                Caller = manager
                Attempt = register
                Target = missingService
                EnabledOwners = 2
                Actors = 5
                Current = None
                Expected = None
            }
            {
                Name = "register existing service"
                Caller = manager
                Attempt = register
                Target = serviceActor
                EnabledOwners = 2
                Actors = 5
                Current = None
                Expected = Some(Refusal.Authority AuthorityRefusal.ActorAlreadyRegistered)
            }
            {
                Name = "grant missing actor precedes role restriction"
                Caller = manager
                Attempt = grant Role.Owner GrantTarget.Installation
                Target = missingService
                EnabledOwners = 1
                Actors = 5
                Current = None
                Expected = Some(Refusal.Authority AuthorityRefusal.ActorUnknown)
            }
            {
                Name = "revoke missing actor precedes missing case"
                Caller = manager
                Attempt = revoke Role.Owner caseScope
                Target = missingHuman
                EnabledOwners = 1
                Actors = 5
                Current = None
                Expected = Some(Refusal.Authority AuthorityRefusal.ActorUnknown)
            }
            {
                Name = "enable missing actor"
                Caller = manager
                Attempt = enable
                Target = missingHuman
                EnabledOwners = 2
                Actors = 5
                Current = None
                Expected = Some(Refusal.Authority AuthorityRefusal.ActorUnknown)
            }
            {
                Name = "disable missing actor"
                Caller = manager
                Attempt = disable
                Target = missingHuman
                EnabledOwners = 2
                Actors = 5
                Current = None
                Expected = Some(Refusal.Authority AuthorityRefusal.ActorUnknown)
            }
            {
                Name = "grant human owner"
                Caller = manager
                Attempt = grant Role.Owner GrantTarget.Installation
                Target = human
                EnabledOwners = 2
                Actors = 5
                Current = None
                Expected = None
            }
            {
                Name = "grant service owner"
                Caller = manager
                Attempt = grant Role.Owner GrantTarget.Installation
                Target = serviceActor
                EnabledOwners = 1
                Actors = 5
                Current = None
                Expected = Some(Refusal.Authority AuthorityRefusal.HumanPrincipalRequired)
            }
            {
                Name = "revoke service owner role even without a grant"
                Caller = manager
                Attempt = revoke Role.Owner GrantTarget.Installation
                Target = serviceActor
                EnabledOwners = 1
                Actors = 5
                Current = None
                Expected = Some(Refusal.Authority AuthorityRefusal.HumanPrincipalRequired)
            }
            {
                Name = "missing case precedes human-only role"
                Caller = manager
                Attempt = grant Role.DataSteward caseScope
                Target = serviceActor
                EnabledOwners = 1
                Actors = 5
                Current = None
                Expected = Some(Refusal.Access AccessRefusal.ResourceUnavailable)
            }
            {
                Name = "resolved case still requires human steward"
                Caller = manager
                Attempt = grant Role.DataSteward caseScope
                Target = serviceActor
                EnabledOwners = 1
                Actors = 5
                Current = Some knownCase
                Expected = Some(Refusal.Authority AuthorityRefusal.HumanPrincipalRequired)
            }
            {
                Name = "service case handler"
                Caller = manager
                Attempt = grant Role.CaseHandler caseScope
                Target = serviceActor
                EnabledOwners = 1
                Actors = 5
                Current = Some knownCase
                Expected = None
            }
            {
                Name = "last installation owner revoke"
                Caller = manager
                Attempt = revoke Role.Owner GrantTarget.Installation
                Target = lastOwner
                EnabledOwners = 1
                Actors = 5
                Current = None
                Expected = Some(Refusal.Authority AuthorityRefusal.LastOwner)
            }
            {
                Name = "second installation owner revoke"
                Caller = manager
                Attempt = revoke Role.Owner GrantTarget.Installation
                Target = secondOwner
                EnabledOwners = 2
                Actors = 5
                Current = None
                Expected = None
            }
            {
                Name = "case grant revoke does not remove installation owner"
                Caller = manager
                Attempt = revoke Role.Owner caseScope
                Target = lastOwner
                EnabledOwners = 1
                Actors = 5
                Current = Some knownCase
                Expected = None
            }
            {
                Name = "missing case precedes last-owner restriction"
                Caller = manager
                Attempt = revoke Role.Owner caseScope
                Target = lastOwner
                EnabledOwners = 1
                Actors = 5
                Current = None
                Expected = Some(Refusal.Access AccessRefusal.ResourceUnavailable)
            }
            {
                Name = "different role revoke does not remove owner"
                Caller = manager
                Attempt = revoke Role.CaseReader GrantTarget.Installation
                Target = lastOwner
                EnabledOwners = 1
                Actors = 5
                Current = None
                Expected = None
            }
            {
                Name = "last owner disable"
                Caller = manager
                Attempt = disable
                Target = lastOwner
                EnabledOwners = 1
                Actors = 5
                Current = None
                Expected = Some(Refusal.Authority AuthorityRefusal.LastOwner)
            }
            {
                Name = "second owner disable"
                Caller = manager
                Attempt = disable
                Target = secondOwner
                EnabledOwners = 2
                Actors = 5
                Current = None
                Expected = None
            }
            {
                Name = "disabled owner is not the last enabled owner"
                Caller = manager
                Attempt = disable
                Target = disabledOwner
                EnabledOwners = 1
                Actors = 5
                Current = None
                Expected = None
            }
            {
                Name = "enable already enabled"
                Caller = manager
                Attempt = enable
                Target = human
                EnabledOwners = 2
                Actors = 5
                Current = None
                Expected = None
            }
            {
                Name = "grant human missing case"
                Caller = manager
                Attempt = grant Role.CaseReader caseScope
                Target = human
                EnabledOwners = 2
                Actors = 5
                Current = None
                Expected = Some(Refusal.Access AccessRefusal.ResourceUnavailable)
            }
            {
                Name = "provision first human owner"
                Caller = manager
                Attempt = Attempt.Provision
                Target = missingHuman
                EnabledOwners = 0
                Actors = 0
                Current = None
                Expected = None
            }
            {
                Name = "provision first service owner"
                Caller = manager
                Attempt = Attempt.Provision
                Target = missingService
                EnabledOwners = 0
                Actors = 0
                Current = None
                Expected = Some(Refusal.Authority AuthorityRefusal.HumanPrincipalRequired)
            }
            {
                Name = "provision existing human installation"
                Caller = manager
                Attempt = Attempt.Provision
                Target = human
                EnabledOwners = 2
                Actors = 5
                Current = None
                Expected = Some(Refusal.Authority AuthorityRefusal.ActorAlreadyRegistered)
            }
            {
                Name = "provision existing service installation identity precedes target rule"
                Caller = manager
                Attempt = Attempt.Provision
                Target = serviceActor
                EnabledOwners = 1
                Actors = 5
                Current = None
                Expected = Some(Refusal.Authority AuthorityRefusal.ActorAlreadyRegistered)
            }
            {
                Name = "management precedes target existence"
                Caller = missingManager
                Attempt = grant Role.Owner caseScope
                Target = missingService
                EnabledOwners = 1
                Actors = 5
                Current = None
                Expected = Some(Refusal.Access AccessRefusal.ResourceUnavailable)
            }
            {
                Name = "service owner grant conveys no management"
                Caller = serviceManager
                Attempt = grant Role.Owner GrantTarget.Installation
                Target = serviceActor
                EnabledOwners = 1
                Actors = 5
                Current = None
                Expected = Some(Refusal.Access AccessRefusal.ResourceUnavailable)
            }

        ]

    for scenario in rows do
        let name = scenario.Name
        let principal, caller = scenario.Caller
        let attempt = scenario.Attempt
        let targetPrincipal, target = scenario.Target
        let owners = scenario.EnabledOwners
        let actors = scenario.Actors
        let current = scenario.Current
        let expected = scenario.Expected
        let view = authorityView caller target None

        let facts =
            { view.Read with
                EnabledHumanOwners = owners
                ActorCount = actors
                TargetCase = current
            }

        let actual =
            match attempt with
            | Attempt.Authored act ->
                reviewAuthority
                    installation
                    principal
                    {
                        OperationId = id 605
                        Target = PrincipalKey.toInput targetPrincipal
                        Act = act
                    }
                    facts
                |> refusal
            | Attempt.Provision ->
                let head =
                    if actors = 0 then
                        { view.Head with Sequence = 0L }
                    else
                        view.Head

                provisionAuthority
                    installation
                    (id 605)
                    targetPrincipal
                    { view with Head = head; Read = facts }
                |> refusal

        Expect.equal actual expected name

[<Tests>]
let tests =
    testList
        "authority refusal order"
        [
            testCase
                "register grant revoke enable disable and provision retain hand-authored priority"
                ordering
        ]
