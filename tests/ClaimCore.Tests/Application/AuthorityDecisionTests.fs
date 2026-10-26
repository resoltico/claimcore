module ClaimCore.Tests.AuthorityDecisionTests

open Expecto
open ClaimCore.Contract
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
            AdmittedAuthorityAct.Disable
            AdmittedAuthorityAct.Revoke(Role.Owner, GrantTarget.Installation)
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
            admitted 101 service (AdmittedAuthorityAct.Grant(role, GrantTarget.Installation))

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
    let request = admitted 103 other AdmittedAuthorityAct.Register
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
        { request with
            Act = AdmittedAuthorityAct.Disable
        }

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
        (StoredSubject.Authority(id 104, AuthorityKind.ProvisionOwner))
        "Bootstrap records its distinct durable kind"

    let target = actor owner (id 104) [ Role.Owner ] []
    let view = authorityView target target (Some(LedgerEntries.stored entry))

    Expect.equal
        (provisionAuthority installation (id 104) owner view)
        (Verdict.Replay receipt)
        "Bootstrap exact replay is idempotent"

let private reviewAndFences () =
    let request = admitted 105 other AdmittedAuthorityAct.Register
    let view = authorityView worker AuthorityView.empty None

    match AuthorityExecution.review installation owner request view.Read with
    | Verdict.Proceed(_, AuthorityProjection.Register _) -> ()
    | _ -> failtest "Review must be a pure proposal"

    Expect.equal
        (executeAuthority installation owner request { view with IsFenced = true })
        (Verdict.Refuse Refusal.OperationResolved)
        "Operational fence precedes append"

    Expect.equal
        (executeAuthority
            installation
            owner
            request
            { view with
                Head =
                    { head with
                        Sequence = Revision.Maximum
                    }
            })
        (Verdict.Refuse Refusal.LedgerExhausted)
        "Capacity refuses fresh work"

    let registered = admitted 106 owner AdmittedAuthorityAct.Register

    let full =
        { authorityView worker worker None with
            Head =
                { head with
                    Sequence = Revision.Maximum
                }
        }

    Expect.equal
        (AuthorityExecution.review installation owner registered full.Read)
        (Verdict.Refuse Refusal.ActorAlreadyRegistered)
        "Preview applies target rules without a head"

    Expect.equal
        (executeAuthority installation owner registered full)
        (Verdict.Refuse Refusal.LedgerExhausted)
        "Execution capacity precedes target projection refusal"

    let scoped = actor owner (id 1) [] [ id 77, [ Role.Owner ] ]

    Expect.isEmpty
        (Advice.installationOffers owner scoped)
        "A case grant cannot offer installation administration"

    Expect.isEmpty
        (Advice.installationOffers service (actor service (id 2) [ Role.Owner ] []))
        "Invalid service owner grants convey no offers"

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
            testCase
                "review fences capacity and installation offers use current authority"
                reviewAndFences
        ]
