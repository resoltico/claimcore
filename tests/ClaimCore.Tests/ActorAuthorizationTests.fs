module ClaimCore.Tests.ActorAuthorizationTests

open System
open Expecto
open ClaimCore.Application
open ClaimCore.Domain

let private issuer = "https://issuer.example.test/realms/claimcore"
let internal caseId = Guid.Parse("11111111-1111-4111-8111-111111111111")

let private human subject =
    PrincipalKey.human issuer subject
    |> Result.defaultWith (fun _ -> failtest "Synthetic human principal rejected")

let internal actor grants =
    {
        ActorId = Guid.Parse("22222222-2222-4222-8222-222222222222")
        Principal = human "person-1"
        Enabled = true
        GrantRevision = 5L
        Grants = grants
    }

let internal grant role scope = { Role = role; Scope = scope }

let private defaultDeny () =
    let empty = actor []
    let owner = actor [ grant Role.Owner GrantScope.Installation ]

    Expect.equal
        (ActorAuthorization.authorize
            empty.Principal
            empty
            EndpointAction.GetCase
            (ResourceScope.Case caseId))
        AuthorizationDecision.Unavailable
        "No grant denies disclosure"

    Expect.equal
        (ActorAuthorization.authorize
            owner.Principal
            owner
            EndpointAction.GetCase
            (ResourceScope.Case caseId))
        AuthorizationDecision.Unavailable
        "Owner role does not ambiently disclose claimant data"

    Expect.equal
        (ActorAuthorization.authorize
            owner.Principal
            owner
            EndpointAction.ManageGrants
            ResourceScope.Installation)
        (AuthorizationDecision.Available(owner.ActorId, owner.GrantRevision))
        "Owner may manage explicit grants"

    Expect.equal
        (ActorAuthorization.authorize
            (human "person-2")
            owner
            EndpointAction.ManageGrants
            ResourceScope.Installation)
        AuthorizationDecision.Unavailable
        "A loaded grant snapshot cannot be used for a different principal"

let private scopedEditorMatrix (editor: ActorAuthority) otherCase =
    Expect.isTrue
        (ActorAuthorization.can
            editor.Principal
            editor
            EndpointAction.HistoryFull
            (ResourceScope.Case caseId))
        "An editor can review full history for its granted case"

    Expect.isFalse
        (ActorAuthorization.can
            editor.Principal
            editor
            EndpointAction.HistoryFull
            (ResourceScope.Case otherCase))
        "Editor history remains case-scoped"

let private scopedMatrix () =
    let reader = actor [ grant Role.CaseReader (GrantScope.Case caseId) ]
    let editor = actor [ grant Role.CaseEditor (GrantScope.Case caseId) ]
    let otherCase = Guid.Parse("33333333-3333-4333-8333-333333333333")

    Expect.isTrue
        (ActorAuthorization.can
            reader.Principal
            reader
            EndpointAction.GetCase
            (ResourceScope.Case caseId))
        "Case reader can read its case"

    Expect.isFalse
        (ActorAuthorization.can
            reader.Principal
            reader
            EndpointAction.GetCase
            (ResourceScope.Case otherCase))
        "Case grant cannot read another case"

    Expect.isFalse
        (ActorAuthorization.can
            reader.Principal
            reader
            EndpointAction.ExecuteCommand
            (ResourceScope.Case caseId))
        "Read grant cannot mutate"

    Expect.isFalse
        (ActorAuthorization.can
            reader.Principal
            reader
            EndpointAction.ListCases
            ResourceScope.Installation)
        "Case grant does not authorize an installation-wide list"

    Expect.isFalse
        (ActorAuthorization.can
            reader.Principal
            reader
            EndpointAction.HistoryFull
            (ResourceScope.Case caseId))
        "A reader does not receive claimant-bearing historical values"

    scopedEditorMatrix editor otherCase

let private nondisclosure () =
    let reader = actor [ grant Role.CaseReader (GrantScope.Case caseId) ]
    let hidden = Guid.Parse("33333333-3333-4333-8333-333333333333")

    Expect.equal
        (ActorAuthorization.authorizeLookup reader.Principal reader EndpointAction.GetCase None)
        (ActorAuthorization.authorizeLookup
            reader.Principal
            reader
            EndpointAction.GetCase
            (Some hidden))
        "Absent and inaccessible case share one public refusal"

    Expect.equal
        (ActorAuthorization.authorizeLookup
            reader.Principal
            reader
            EndpointAction.GetCase
            (Some caseId))
        (AuthorizationDecision.Available(reader.ActorId, reader.GrantRevision))
        "Authorized case remains available"

let private revocationRace () =
    let editor = actor [ grant Role.CaseEditor (GrantScope.Case caseId) ]
    let disabled = { editor with Enabled = false }

    let revoked =
        { editor with
            GrantRevision = 6L
            Grants = []
        }

    let target = ResourceScope.Case caseId

    Expect.equal
        (ActorAuthorization.authorizeAtRevision
            editor.Principal
            editor
            4L
            EndpointAction.ExecuteCommand
            target)
        AuthorizationDecision.Unavailable
        "Prepared authority cannot use an older grant revision"

    Expect.equal
        (ActorAuthorization.authorizeAtRevision
            revoked.Principal
            revoked
            5L
            EndpointAction.ExecuteCommand
            target)
        AuthorizationDecision.Unavailable
        "A revoked grant cannot commit using the old revision"

    Expect.equal
        (ActorAuthorization.authorize
            disabled.Principal
            disabled
            EndpointAction.ExecuteCommand
            target)
        AuthorizationDecision.Unavailable
        "Disabled actors are denied"

let private realDataActivationOwners () =
    let owner = actor [ grant Role.Owner GrantScope.Installation ]
    let steward = actor [ grant Role.DataSteward GrantScope.Installation ]

    let servicePrincipal =
        PrincipalKey.service issuer "synthetic-service"
        |> Result.defaultWith (fun _ -> failtest "Synthetic service principal rejected")

    let service =
        { owner with
            Principal = servicePrincipal
        }

    for action in
        [
            EndpointAction.ReviewRealDataActivation
            EndpointAction.ApproveRealDataActivation
        ] do
        Expect.equal
            (ActorAuthorization.authorize owner.Principal owner action ResourceScope.Installation)
            (AuthorizationDecision.Available(owner.ActorId, owner.GrantRevision))
            "Only an installation owner can review or approve the published activation plan"

        Expect.equal
            (ActorAuthorization.authorize
                steward.Principal
                steward
                action
                ResourceScope.Installation)
            AuthorizationDecision.Unavailable
            "Data steward alone cannot approve real-data activation"

        Expect.equal
            (ActorAuthorization.authorize
                service.Principal
                service
                action
                ResourceScope.Installation)
            AuthorizationDecision.Unavailable
            "A service principal cannot substitute for a human owner"

        Expect.equal
            (ActorAuthorization.authorize owner.Principal owner action (ResourceScope.Case caseId))
            AuthorizationDecision.Unavailable
            "Activation authority is installation-scoped, never case-scoped"

let tests =
    testList
        "actor and grant authorization"
        [
            testCase "[CC-AUTH-001] grants default-deny and owner does not read claims" defaultDeny
            testCase "[CC-AUTH-001] case grants stay scoped" scopedMatrix
            testCase
                "[CC-AUTH-001] absent and inaccessible cases disclose identically"
                nondisclosure
            testCase
                "[CC-AUTH-001] mutation authority rejects stale grants and disabled actors"
                revocationRace
            testCase
                "[CC-AUTH-001] real-data activation review and approval require a human installation owner"
                realDataActivationOwners
        ]
