module ClaimCore.Tests.AccessPolicyTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.AccessPolicyInputs

let private matrix () =
    Expect.isTrue
        (Set.ofList Role.all = (expectedRoles |> List.map (fun (role, _, _) -> role) |> Set.ofList))
        "Exact role inventory"

    Expect.isTrue
        (Set.ofList Capability.all = Set.ofList allCapabilities)
        "Exact capability inventory"

    for role, token, capabilities in expectedRoles do
        Expect.equal (Role.token role) token "Independent role token"
        Expect.equal (Role.parse token) (Some role) "Role parser"

        for principal in [ human; service ] do
            for scope in [ Scope.Installation; Scope.Case caseId ] do
                let current = actor principal [ { Role = role; Scope = scope } ]

                for resource in [ Scope.Installation; Scope.Case caseId; Scope.Case otherCaseId ] do
                    for capability in allCapabilities do
                        let expected =
                            expectedPermission
                                {
                                    Role = role
                                    Capabilities = capabilities
                                }
                                principal
                                scope
                                resource
                                capability

                        Expect.equal
                            (Access.can
                                principal
                                (AccessView.ofActor (Some current))
                                capability
                                resource)
                            expected
                            "Independent role, principal and scope expectation"

    for token in [ "CASE_EDITOR"; "case_handler"; "" ] do
        Expect.isNone (Role.parse token) "Unsupported role names refuse"

let private invalidPrincipals () =
    for value in [ String([| char 0xD800 |]); "a\u0000b"; "a﷐b"; "a￿b" ] do
        Expect.isError
            (PrincipalKey.human "https://issuer.example/" value)
            "Identity text has permanent Unicode bounds"

let private invalidActors () =
    Expect.isNone
        (ActorId.create Guid.Empty)
        "Empty actor identities are rejected before authorization"

    Expect.isNone (CaseId.create Guid.Empty) "Empty case scopes are rejected before authorization"

    let current =
        actor
            human
            [
                {
                    Role = Role.CaseHandler
                    Scope = Scope.Installation
                }
            ]

    for invalid in
        [
            { current with Grants = [] }
            { current with
                Actor = { current.Actor with Enabled = false }
            }
            { current with
                Actor =
                    { current.Actor with
                        Principal = service
                    }
            }
        ] do
        Expect.isFalse
            (Access.can
                human
                (AccessView.ofActor (Some invalid))
                Capability.ReadCase
                (Scope.Case caseId))
            "Invalid actor grants no rights"

    Expect.isFalse
        (Access.can human AccessView.empty Capability.ReadCase (Scope.Case caseId))
        "Absent actor grants no rights"

    let scoped =
        actor
            human
            [
                {
                    Role = Role.Auditor
                    Scope = Scope.Case caseId
                }
            ]

    Expect.isFalse
        (Access.can
            human
            (AccessView.ofActor (Some scoped))
            Capability.ReadAccess
            Scope.Installation)
        "Case-scoped audit does not enumerate the installation"

    invalidPrincipals ()

let private humanOnlyCapabilities () =
    Expect.equal
        (Role.all |> List.filter Access.humanOnlyRole)
        [ Role.Owner; Role.DataSteward ]
        "Role principal restrictions are independent of optional human-only capabilities"

    Expect.isTrue
        (Access.humanOnlyCapability Capability.ManageCaseWork)
        "The capability is human-only"

    Expect.isFalse (Access.humanOnlyRole Role.CaseHandler) "Services retain CASE_HANDLER admission"

    let current =
        actor
            service
            [
                {
                    Role = Role.CaseHandler
                    Scope = Scope.Installation
                }
            ]

    let view = AccessView.ofActor (Some current)
    Expect.isTrue (Access.validActor service view) "Positive service handler control"

    Expect.isTrue
        (Access.can service view Capability.Decide Scope.Installation)
        "Retained service capability"

    Expect.isFalse
        (Access.can service view Capability.ManageCaseWork Scope.Installation)
        "New capability is withheld"

    let forbidden =
        { current with
            Grants =
                {
                    Role = Role.DataSteward
                    Scope = Scope.Installation
                }
                :: current.Grants
        }

    Expect.isFalse
        (Access.validActor service (AccessView.ofActor (Some forbidden)))
        "A real human-only role remains an invalid service grant"

[<Tests>]
let tests =
    testList
        "independent actor authorization policy"
        [
            testCase
                "all role, capability, principal and scope combinations match independent expectations"
                matrix
            testCase "invalid actors and installation access boundaries default deny" invalidActors
            testCase
                "human-only capabilities do not invalidate admitted service roles"
                humanOnlyCapabilities
            testCase "case task declarations retain independently specified capabilities" (fun () ->
                for token, capability in
                    [
                        "case.open", Capability.Register
                        "case.amendRegistration", Capability.Register
                        "case.decide", Capability.Decide
                        "case.withdrawDecision", Capability.Decide
                        "case.recordPayment", Capability.RecordPayment
                        "case.removePayment", Capability.RecordPayment
                        "case.close", Capability.Close
                        "case.reopen", Capability.Close
                        "case.void", Capability.Steward
                        "case.reinstate", Capability.Steward
                        "case.erase", Capability.Steward
                    ] do
                    let declaration =
                        ClaimCore.Domain.Tasks.Registry.findDeclaration (TaskId.ofLiteral token)
                        |> Option.get

                    Expect.equal
                        declaration.Capabilities
                        [ capability ]
                        "Literal capability contract")
            testCase
                "readable denied tasks disclose capability while unreadable or missing targets remain unavailable"
                (fun () ->
                    let state = ClaimCore.Tests.TaskBuilders.family ()

                    let readable =
                        actor
                            human
                            [
                                {
                                    Role = Role.CaseReader
                                    Scope = Scope.Installation
                                }
                            ]
                        |> Some
                        |> AccessView.ofActor

                    let original = ClaimCore.Tests.TaskBuilders.context 3

                    let context =
                        { original with
                            Decision =
                                { original.Decision with
                                    Principal = human
                                }
                            Stamp =
                                { original.Stamp with
                                    ActorId = readable.Actor.Value.ActorId
                                }
                        }

                    let environment access =
                        { ClaimCore.Tests.TaskBuilders.environment with
                            RequesterPrincipal = human
                            Requester = access
                        }

                    let decide access state =
                        ClaimCore.Domain.Tasks.Case.Void.decide
                            context
                            (environment access)
                            state
                            (ClaimCore.Tests.TaskBuilders.caseBinding (
                                ClaimCore.Tests.TaskBuilders.family ()
                            ))
                            (ClaimCore.Tests.TaskBuilders.inputs
                                [
                                    ClaimCore.Tests.TaskBuilders.text
                                        InputTarget.Reason
                                        "Recorded in error"
                                ])

                    Expect.equal
                        (decide readable (Some state))
                        (Error(
                            Rejection.Refused(
                                Refusal.Access(AccessRefusal.NotAuthorized Capability.Steward)
                            )
                        ))
                        "Readable denial"

                    let disabled =
                        { readable with
                            Actor =
                                readable.Actor
                                |> Option.map (fun actor -> { actor with Enabled = false })
                        }

                    for access, target in
                        [
                            AccessView.empty, Some state
                            { readable with Installation = [] }, Some state
                            disabled, Some state
                            readable, None
                        ] do
                        Expect.equal
                            (decide access target)
                            (Error(
                                Rejection.Refused(Refusal.Access AccessRefusal.ResourceUnavailable)
                            ))
                            "No existence or permission leak")
        ]

[<Tests>]
let workPolicy =
    testCase
        "work offers and admission share current access without disclosing unreadable cases"
        (fun () ->
            let snapshot =
                ClaimCore.Tests.TaskBuilders.family ()
                |> ClaimCore.Domain.Aggregates.CaseFamily.snapshot
                |> Option.get

            let handler =
                actor
                    human
                    [
                        {
                            Role = Role.CaseHandler
                            Scope = Scope.Installation
                        }
                    ]
                |> fun value -> AccessView.ofActor (Some value)

            let reader =
                actor
                    human
                    [
                        {
                            Role = Role.CaseReader
                            Scope = Scope.Installation
                        }
                    ]
                |> fun value -> AccessView.ofActor (Some value)

            let disabled =
                { handler with
                    Actor =
                        handler.Actor |> Option.map (fun value -> { value with Enabled = false })
                }

            Expect.equal
                (CaseWorkPolicy.authorize human reader snapshot.CaseId)
                (Error(Refusal.Access(AccessRefusal.NotAuthorized Capability.ManageCaseWork)))
                "Readable case names the missing capability"

            for access in [ AccessView.empty; disabled ] do
                Expect.equal
                    (CaseWorkPolicy.authorize human access snapshot.CaseId)
                    (Error(Refusal.Access AccessRefusal.ResourceUnavailable))
                    "Unreadable or disabled state is not disclosed"

                Expect.isFalse
                    (CaseWorkPolicy.canAssign human access false snapshot)
                    "No assignment offer"

            Expect.isTrue
                (CaseWorkPolicy.canAssign human handler false snapshot)
                "Open active case"

            Expect.isFalse (CaseWorkPolicy.canAssign human handler true snapshot) "Maintenance"

            Expect.isTrue
                (Access.eligibleAssignee human handler snapshot.CaseId)
                "Enabled human handler is eligible"

            for access in [ reader; disabled; AccessView.empty ] do
                Expect.isFalse
                    (Access.eligibleAssignee human access snapshot.CaseId)
                    "Read access alone, disabled and absent access cannot receive work"

            Expect.isFalse
                (Access.eligibleAssignee service handler snapshot.CaseId)
                "A service cannot receive responsibility"


            let closed =
                { snapshot with
                    State =
                        snapshot.State
                        |> CaseState.map (fun fields ->
                            { fields with
                                Status = CaseStatus.Closed
                            })
                }

            Expect.isTrue
                (CaseWorkPolicy.canEdit human handler false closed)
                "Closed case permits summary edits"

            Expect.isFalse
                (CaseWorkPolicy.canAssign human handler false closed)
                "Closed case forbids assignment"

            let erased =
                { snapshot with
                    State = CaseState.Erased
                }

            Expect.isFalse
                (CaseWorkPolicy.canEdit human handler false erased)
                "Erased case forbids edits")

[<Tests>]
let accessExplanation =
    testCase "scoped overlapping and dormant authorization sources stay explicit" (fun () ->
        let other = human
        let caseId = caseId

        let target =
            actor
                other
                [
                    {
                        Role = Role.CaseRegistrar
                        Scope = Scope.Installation
                    }
                    {
                        Role = Role.CaseReader
                        Scope = Scope.Case caseId
                    }
                ]
            |> Some
            |> AccessView.ofActor

        let explained = Access.explain other target (Scope.Case caseId)

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
            (Access.explain other disabled (Scope.Case caseId))
            (expected |> List.map (fun value -> { value with Effective = false }))
            "Disabled actors retain dormant sources without effective rights"

        let noScoped = { target with Cases = Map.empty }

        Expect.equal
            (Access.explain other noScoped (Scope.Case caseId))
            (expected |> List.map (fun value -> { value with DirectRoles = [] }))
            "Removing one overlapping grant does not claim inherited access disappeared"

    )
