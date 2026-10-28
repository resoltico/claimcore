module ClaimCore.Tests.AuthorityDecisionTests

open Expecto
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.Tests.DecisionFixtures

let private lastOwner () =
    let target = actor owner (id 1) [ Role.Owner ] []
    let view = authorityView worker target None

    let last =
        { view with
            Read =
                { view.Read with
                    EnabledHumanOwners = 1
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
            (Verdict.Refuse Refusal.LastOwner)
            "The last enabled human owner remains protected"

let private humanOnly () =
    let target = actor service (id 2) [] []

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
            (Verdict.Refuse Refusal.HumanPrincipalRequired)
            "Service principals cannot receive human-only grants"

    let view = authorityView AuthorityView.empty AuthorityView.empty None

    let genesis =
        { view with
            Head = { head with Sequence = 0L }
            Read =
                { view.Read with
                    ActorCount = 0
                    EnabledHumanOwners = 0
                }
        }

    Expect.equal
        (provisionAuthority installation (id 102) service genesis)
        (Verdict.Refuse Refusal.HumanPrincipalRequired)
        "Bootstrap also requires a human"

let private replay () =
    let request = admitted 103 other AuthorityAct.Register
    let view = authorityView worker AuthorityView.empty None

    let entry, receipt =
        match executeAuthority installation owner request view with
        | Verdict.Proceed(entry, AuthorityProjection.Register(target, principal), receipt) ->
            Expect.equal target request.OperationId "Registration owns the target identity"
            Expect.equal principal other "Exact authored principal"
            entry, receipt
        | _ -> failtest "Expected actor registration"

    let target = actor other request.OperationId [] []
    let existing = authorityView worker target (Some(LedgerEntries.stored entry))

    Expect.equal
        (executeAuthority installation owner request existing)
        (Verdict.Replay receipt)
        "Executor target and position survive replay"

    let changed =
        AuthorityRequests.input request.OperationId request.Target AuthorityAct.Disable
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
                    Actor = actor owner (id 1) [ Role.CaseReader ] []
                }
        }

    Expect.equal
        (executeAuthority installation owner request revoked)
        (Verdict.Refuse Refusal.ResourceUnavailable)
        "Current management precedes replay"

let private provision () =
    let initial = authorityView AuthorityView.empty AuthorityView.empty None

    let initial =
        { initial with
            Head = { head with Sequence = 0L }
            Read =
                { initial.Read with
                    ActorCount = 0
                    EnabledHumanOwners = 0
                }
        }

    let entry, receipt =
        match provisionAuthority installation (id 104) owner initial with
        | Verdict.Proceed(entry, AuthorityProjection.ProvisionOwner(target, principal), receipt) ->
            Expect.equal (target, principal) (id 104, owner) "Bootstrap has explicit identity"
            entry, receipt
        | _ -> failtest "Expected explicit bootstrap"

    Expect.equal
        entry.Subject
        (EntryTarget.Authority(id 104, AuthorityKind.ProvisionOwner))
        "Bootstrap records its distinct durable kind"

    let target = actor owner (id 104) [ Role.Owner ] []
    let view = authorityView target target (Some(LedgerEntries.stored entry))

    Expect.equal
        (provisionAuthority installation (id 104) owner view)
        (Verdict.Replay receipt)
        "Bootstrap exact replay is idempotent"

let private reviewAndFences () =
    let request = admitted 105 other AuthorityAct.Register
    let view = authorityView worker AuthorityView.empty None

    match
        AuthorityExecution.review
            (DecisionFixtures.services installation calendar owner)
            request
            view.Read
    with
    | Verdict.Proceed(_, AuthorityProjection.Register _) -> ()
    | _ -> failtest "Review must be a pure proposal"

    Expect.equal
        (executeAuthority installation owner request { view with IsFenced = true })
        (Verdict.Refuse Refusal.OperationResolved)
        "Operational fence precedes append"

    let scoped = actor owner (id 1) [] [ id 77, [ Role.Owner ] ]

    Expect.isEmpty
        (Advice.installationOffers owner scoped)
        "A case grant cannot offer installation administration"

    Expect.isEmpty
        (Advice.installationOffers service (actor service (id 2) [ Role.Owner ] []))
        "Invalid service owner grants convey no offers"

    let installationOwner = actor owner (id 1) [ Role.Owner ] []

    Expect.equal
        (Advice.installationOffers owner installationOwner)
        [
            InstallationOffer.AuthorityAct AuthorityKind.Register
            InstallationOffer.AuthorityAct AuthorityKind.Grant
            InstallationOffer.AuthorityAct AuthorityKind.Revoke
            InstallationOffer.AuthorityAct AuthorityKind.Enable
            InstallationOffer.AuthorityAct AuthorityKind.Disable
        ]
        "Installation offers contain only ordinary operations; owner provisioning uses its dedicated bootstrap boundary"

[<Tests>]
let tests =
    testList
        "pure authority decisions"
        [
            testCase "last-owner protection is a pure locked-view decision" lastOwner
            testCase "human-only grants and bootstrap refuse service principals" humanOnly
            testCase
                "authority replay preserves attribution conflicts and current permissions"
                replay
            testCase "owner provisioning has explicit idempotent identity" provision
            testCase "review fences and installation offers use current authority" reviewAndFences
        ]
