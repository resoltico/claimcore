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
            "register missing human", manager, register, missingHuman, 2, 5, None, None
            "register existing human",
            manager,
            register,
            human,
            2,
            5,
            None,
            Some Refusal.ActorAlreadyRegistered
            "register missing service", manager, register, missingService, 2, 5, None, None
            "register existing service",
            manager,
            register,
            serviceActor,
            2,
            5,
            None,
            Some Refusal.ActorAlreadyRegistered
            "grant missing actor precedes role restriction",
            manager,
            grant "OWNER" GrantTarget.Installation,
            missingService,
            1,
            5,
            None,
            Some Refusal.ActorUnknown
            "revoke missing actor precedes missing case",
            manager,
            revoke "OWNER" caseScope,
            missingHuman,
            1,
            5,
            None,
            Some Refusal.ActorUnknown
            "enable missing actor",
            manager,
            enable,
            missingHuman,
            2,
            5,
            None,
            Some Refusal.ActorUnknown
            "disable missing actor",
            manager,
            disable,
            missingHuman,
            2,
            5,
            None,
            Some Refusal.ActorUnknown
            "grant human owner",
            manager,
            grant "OWNER" GrantTarget.Installation,
            human,
            2,
            5,
            None,
            None
            "grant service owner",
            manager,
            grant "OWNER" GrantTarget.Installation,
            serviceActor,
            1,
            5,
            None,
            Some Refusal.HumanPrincipalRequired
            "revoke service owner role even without a grant",
            manager,
            revoke "OWNER" GrantTarget.Installation,
            serviceActor,
            1,
            5,
            None,
            Some Refusal.HumanPrincipalRequired
            "missing case precedes human-only role",
            manager,
            grant "DATA_STEWARD" caseScope,
            serviceActor,
            1,
            5,
            None,
            Some Refusal.ResourceUnavailable
            "resolved case still requires human steward",
            manager,
            grant "DATA_STEWARD" caseScope,
            serviceActor,
            1,
            5,
            Some knownCase,
            Some Refusal.HumanPrincipalRequired
            "service case handler",
            manager,
            grant "CASE_HANDLER" caseScope,
            serviceActor,
            1,
            5,
            Some knownCase,
            None
            "last installation owner revoke",
            manager,
            revoke "OWNER" GrantTarget.Installation,
            lastOwner,
            1,
            5,
            None,
            Some Refusal.LastOwner
            "second installation owner revoke",
            manager,
            revoke "OWNER" GrantTarget.Installation,
            secondOwner,
            2,
            5,
            None,
            None
            "case grant revoke does not remove installation owner",
            manager,
            revoke "OWNER" caseScope,
            lastOwner,
            1,
            5,
            Some knownCase,
            None
            "missing case precedes last-owner restriction",
            manager,
            revoke "OWNER" caseScope,
            lastOwner,
            1,
            5,
            None,
            Some Refusal.ResourceUnavailable
            "different role revoke does not remove owner",
            manager,
            revoke "CASE_READER" GrantTarget.Installation,
            lastOwner,
            1,
            5,
            None,
            None
            "last owner disable", manager, disable, lastOwner, 1, 5, None, Some Refusal.LastOwner
            "second owner disable", manager, disable, secondOwner, 2, 5, None, None
            "disabled owner is not the last enabled owner",
            manager,
            disable,
            disabledOwner,
            1,
            5,
            None,
            None
            "enable already enabled", manager, enable, human, 2, 5, None, None
            "grant human missing case",
            manager,
            grant "CASE_READER" caseScope,
            human,
            2,
            5,
            None,
            Some Refusal.ResourceUnavailable
            "provision first human owner",
            manager,
            Attempt.Provision,
            missingHuman,
            0,
            0,
            None,
            None
            "provision first service owner",
            manager,
            Attempt.Provision,
            missingService,
            0,
            0,
            None,
            Some Refusal.HumanPrincipalRequired
            "provision existing human installation",
            manager,
            Attempt.Provision,
            human,
            2,
            5,
            None,
            Some Refusal.ActorAlreadyRegistered
            "provision existing service installation identity precedes target rule",
            manager,
            Attempt.Provision,
            serviceActor,
            1,
            5,
            None,
            Some Refusal.ActorAlreadyRegistered
            "management precedes target existence",
            missingManager,
            grant "OWNER" caseScope,
            missingService,
            1,
            5,
            None,
            Some Refusal.ResourceUnavailable
            "service owner grant conveys no management",
            serviceManager,
            grant "OWNER" GrantTarget.Installation,
            serviceActor,
            1,
            5,
            None,
            Some Refusal.ResourceUnavailable
        ]

    for name,
        (principal, caller),
        attempt,
        (targetPrincipal, target),
        owners,
        actors,
        current,
        expected in rows do
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
                        Target = PrincipalKey.describe targetPrincipal
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
