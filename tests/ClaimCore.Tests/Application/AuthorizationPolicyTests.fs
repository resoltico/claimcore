module ClaimCore.Tests.AuthorizationPolicyTests

open ClaimCore.TestSupport

open ClaimCore.Wire
open ClaimCore.Presentation

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Tests.Fixtures
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

let private caseActCapabilities () =
    let cases =
        [
            AuthoredCaseAct.Open registration, Capability.Register
            AuthoredCaseAct.AmendRegistration(Inputs.registrationUpdate registration),
            Capability.Register
            AuthoredCaseAct.Decide decision, Capability.Decide
            (AuthoredCaseAct.Bare BareAct.WithdrawDecision), Capability.Decide
            AuthoredCaseAct.RecordPayment(
                ClaimCore.TestSupport.Inputs.paymentFrom
                    [ BusinessField.PaymentDate, ("2026-08-20") ]
            ),
            Capability.RecordPayment
            (AuthoredCaseAct.RemovePayment(System.Guid.Parse "20000000-0000-7000-8000-000000000001")),
            Capability.RecordPayment
            (AuthoredCaseAct.Bare BareAct.Close), Capability.Close
            (AuthoredCaseAct.Bare BareAct.Reopen), Capability.Close
            AuthoredCaseAct.Void "Reason", Capability.Steward
            AuthoredCaseAct.Reinstate "Reason", Capability.Steward
            (AuthoredCaseAct.Bare BareAct.Erase), Capability.Steward
        ]

    for act, capability in cases do
        Expect.equal
            (Access.requiredCapabilities (CaseFixtures.act act))
            [ capability ]
            "Independent act capability"

let private correctionMasks =
    [
        false, false, []
        true, false, [ Capability.Register ]
        false, true, [ Capability.Decide ]
        true, true, [ Capability.Register; Capability.Decide ]
    ]

let private corrections () =
    for registrationChanged, decisionChanged, expected in correctionMasks do
        let registration =
            if registrationChanged then
                Amendment.Update(Inputs.registrationUpdate registration)
            else
                Amendment.Keep

        let decision =
            if decisionChanged then
                Change.Update(Inputs.decisionUpdate decision)
            else
                Change.Keep

        let corrected =
            {
                AuthoredCorrection.Registration = registration
                Decision = decision
            }

        Expect.equal
            (Access.requiredCapabilities (CaseFixtures.act (AuthoredCaseAct.Correct corrected)))
            expected
            "Each of the two asserted groups requires its own capability"

        let cleared =
            { corrected with
                Decision = if decisionChanged then Change.Clear else Change.Keep
            }

        Expect.equal
            (Access.requiredCapabilities (CaseFixtures.act (AuthoredCaseAct.Correct cleared)))
            expected
            "Clearing selects decision access"

let private paymentActions () =
    let handler =
        actor
            human
            [
                {
                    Role = Role.CaseHandler
                    Scope = Scope.Installation
                }
            ]

    let recorder =
        actor
            human
            [
                {
                    Role = Role.PaymentRecorder
                    Scope = Scope.Installation
                }
            ]

    for act in
        [
            AuthoredCaseAct.RecordPayment Inputs.payment
            AuthoredCaseAct.RemovePayment(System.Guid.Parse "20000000-0000-7000-8000-000000000001")
        ] do
        let validated = CaseFixtures.act act

        Expect.isFalse
            (Access.canAct human (AccessView.ofActor (Some handler)) (Scope.Case caseId) validated)
            "Case handling does not grant payment access"

        Expect.isTrue
            (Access.canAct human (AccessView.ofActor (Some recorder)) (Scope.Case caseId) validated)
            "Payment recording access covers entry removal"

let private scopedAdvice () =
    let installation = Guid.Parse "90000000-0000-7000-8000-000000000001"
    let snapshot = Case.snapshot (paid ())

    let target = snapshot.CaseId

    let record: StoredCase =
        {
            CaseId = target
            Snapshot = snapshot
            Index = CaseIndex.create DateTimeOffset.UnixEpoch snapshot
            SnapshotBytes = ClaimCore.RecordFormat.CaseRecord.encodeSnapshot snapshot
        }

    for role, expectedGroups, expectedHistory in
        [
            Role.CaseRegistrar, [ "registration" ], HistoryAccess.Summary
            Role.PaymentRecorder, [], HistoryAccess.Summary
            Role.CaseHandler, [ "registration"; "decision" ], HistoryAccess.Full
        ] do
        let access =
            actor
                human
                [
                    {
                        Role = role
                        Scope = Scope.Case target
                    }
                ]

        let advice =
            Queries.caseRecord
                (DecisionFixtures.services installation DecisionFixtures.calendar human)
                (AccessView.ofActor (Some access))
                (Inputs.workView record.Snapshot.State)
                (Inputs.stageDisplay record.Snapshot.State)
                Inputs.workBasis.ObservedAt
                ClaimCore.TestSupport.ResponseExamples.caseRead.RecordedBy
                []
                record
            |> accepted

        Expect.equal
            advice.Snapshot.Revision
            snapshot.Revision
            "The snapshot owns the expected revision"

        Expect.equal advice.Advice.History expectedHistory "History reflects the actual grant"

        if expectedGroups.IsEmpty then
            Expect.isNone advice.Advice.Correction "Payment access exposes no correction group"

            Expect.isFalse
                (List.contains CaseActKind.Correct advice.Advice.Acts)
                "Recorders remove entries instead of correcting them"

            Expect.contains
                advice.Advice.Acts
                CaseActKind.RemovePayment
                "Recorder access offers entry removal"
        else
            let correction =
                advice.Advice.Correction
                |> Option.defaultWith (fun () -> failtest "Correct needs choices")

            Expect.contains advice.Advice.Acts CaseActKind.Correct "Correct is explicitly offered"

            for group, values in
                [ "registration", correction.Registration; "decision", correction.Decision ] do
                Expect.equal
                    (values <> [ CorrectionGroupAction.Keep ])
                    (List.contains group expectedGroups)
                    "Unauthorized groups remain KEEP"

let private absentCorrectionValues () =
    let record = opened ()

    Expect.equal
        (Case.correctionActions record RecordGroup.Decision)
        [ CorrectionGroupAction.Keep ]
        "A correction cannot introduce a missing decision"

    Expect.equal
        (Case.correctionActions record RecordGroup.Payment)
        []
        "Payments are not a correction group"

    Expect.equal
        (Case.correctionActions (decided ()) RecordGroup.Payment)
        []
        "Decision presence does not permit introducing a payment through correction"

let private noUsableCorrectionOffer () =
    let installation = Guid.Parse "90000000-0000-7000-8000-000000000001"

    let snapshot =
        opened ()
        |> apply (AuthoredCaseAct.Bare BareAct.Close)
        |> accepted
        |> Case.snapshot

    let target = snapshot.CaseId

    let record: StoredCase =
        {
            CaseId = target
            Snapshot = snapshot
            Index = CaseIndex.create DateTimeOffset.UnixEpoch snapshot
            SnapshotBytes = ClaimCore.RecordFormat.CaseRecord.encodeSnapshot snapshot
        }

    let access =
        actor
            human
            [
                {
                    Role = Role.PaymentRecorder
                    Scope = Scope.Case target
                }
            ]

    let advice =
        Queries.caseRecord
            (DecisionFixtures.services installation DecisionFixtures.calendar human)
            (AccessView.ofActor (Some access))
            (Inputs.workView record.Snapshot.State)
            (Inputs.stageDisplay record.Snapshot.State)
            Inputs.workBasis.ObservedAt
            ClaimCore.TestSupport.ResponseExamples.caseRead.RecordedBy
            []
            record
        |> accepted

    Expect.isFalse
        (List.contains CaseActKind.Correct advice.Advice.Acts)
        "An all-KEEP form is never offered"

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
        "actor authorization policy"
        [
            testCase
                "role admission and human-only capabilities remain independent with a forbidden-role control"
                humanOnlyCapabilities
            testCase
                "all role, capability, principal and scope combinations match independent expectations"
                matrix
            testCase
                "invalid actors and installation access boundaries are default deny"
                invalidActors
            testCase
                "ordinary business and stewardship acts have independent required capabilities"
                caseActCapabilities
            testCase "correction choices require every selected group capability" corrections
            testCase
                "payment entry actions require recorder access and all-KEEP refuses"
                paymentActions
            testCase "case scoped advice fixes unauthorized correction groups at KEEP" scopedAdvice
            testCase "correction choice advice shares unary execution guards" absentCorrectionValues
            testCase
                "advice omits an unusable correction whose every choice is KEEP"
                noUsableCorrectionOffer
        ]

[<Tests>]
let denialDisclosure =
    testCase
        "readable denied acts name the capability while other targets remain unavailable"
        (fun () ->
            let _, _, stored, _ =
                ClaimCore.Tests.DecisionFixtures.created 892 "DENIAL-DISCLOSURE"

            let input: UnvalidatedCaseRequest =
                {
                    OperationId = Guid.Parse "70000000-0000-7000-8000-000000000893"
                    CaseReference = "DENIAL-DISCLOSURE"
                    ExpectedRevision = 1L
                    Act = CaseAct.Void "Recorded in error"
                }

            let command = CaseFixtures.command input

            let readable =
                ClaimCore.Tests.DecisionFixtures.actor
                    ClaimCore.Tests.DecisionFixtures.owner
                    (CaseFixtures.actorId (Guid.Parse "70000000-0000-7000-8000-000000000894"))
                    [ Role.CaseReader ]
                    []

            let permission view target =
                CaseExecution.permission ClaimCore.Tests.DecisionFixtures.owner view command target

            Expect.equal
                (permission readable (Some stored))
                (Error(Refusal.Access(AccessRefusal.NotAuthorized Capability.Steward)))
                "A caller who can read the case gets its missing act capability"

            let unreadable = { readable with Installation = [] }

            let disabled =
                { readable with
                    Actor =
                        readable.Actor |> Option.map (fun actor -> { actor with Enabled = false })
                }

            for actual in
                [
                    permission unreadable (Some stored)
                    permission disabled (Some stored)
                    permission AccessView.empty (Some stored)
                    permission readable None
                ] do
                Expect.equal
                    actual
                    (Error(Refusal.Access AccessRefusal.ResourceUnavailable))
                    "No existence, account status or required capability is disclosed")

[<Tests>]
let workPolicy =
    testCase
        "work offers and admission share current access without disclosing unreadable cases"
        (fun () ->
            let snapshot = opened () |> Case.snapshot

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
                (CaseWorkPolicy.eligibleAssignee human handler snapshot.CaseId)
                "Enabled human handler is eligible"

            for access in [ reader; disabled; AccessView.empty ] do
                Expect.isFalse
                    (CaseWorkPolicy.eligibleAssignee human access snapshot.CaseId)
                    "Read access alone, disabled and absent access cannot receive work"

            Expect.isFalse
                (CaseWorkPolicy.eligibleAssignee service handler snapshot.CaseId)
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
