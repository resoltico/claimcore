module ClaimCore.Tests.AuthorizationPolicyTests

open ClaimCore.TestSupport

open ClaimCore.Protocol

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Tests.Fixtures
open ClaimCore.Tests.AuthorityPolicyInputs


let private matrix () =
    Expect.equal (Role.all |> List.map Role.token) (Inventory.tokens "roles") "Inventoried roles"

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
                            (Authority.can
                                principal
                                (AuthorityView.ofActor (Some current))
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
            (Authority.can
                human
                (AuthorityView.ofActor (Some invalid))
                Capability.ReadCase
                (Scope.Case caseId))
            "Invalid actor grants no rights"

    Expect.isFalse
        (Authority.can human AuthorityView.empty Capability.ReadCase (Scope.Case caseId))
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
        (Authority.can
            human
            (AuthorityView.ofActor (Some scoped))
            Capability.ReadAuthority
            Scope.Installation)
        "Case-scoped audit does not enumerate the installation"

    invalidPrincipals ()

let private caseActCapabilities () =
    let cases =
        [
            AuthoredCaseAct.Open registration, Capability.Register
            AuthoredCaseAct.AmendRegistration registration, Capability.Register
            AuthoredCaseAct.Decide decision, Capability.Decide
            (AuthoredCaseAct.Bare BareAct.WithdrawDecision), Capability.Decide
            AuthoredCaseAct.RecordPayment(
                ClaimCore.TestSupport.Inputs.paymentFrom
                    [ BusinessField.PaymentDate, ("2026-08-20") ]
            ),
            Capability.RecordPayment
            (AuthoredCaseAct.Bare BareAct.ClearPayment), Capability.RecordPayment
            (AuthoredCaseAct.Bare BareAct.Close), Capability.Close
            (AuthoredCaseAct.Bare BareAct.Reopen), Capability.Close
            AuthoredCaseAct.Void "Reason", Capability.Steward
            AuthoredCaseAct.Reinstate "Reason", Capability.Steward
            (AuthoredCaseAct.Bare BareAct.Erase), Capability.Steward
        ]

    for act, capability in cases do
        Expect.equal
            (Authority.requiredCapabilities (CaseFixtures.act act))
            [ capability ]
            "Independent act capability"

let private correctionMasks =
    [
        false, false, false, []
        true, false, false, [ Capability.Register ]
        false, true, false, [ Capability.Decide ]
        false, false, true, [ Capability.RecordPayment ]
        true, true, false, [ Capability.Register; Capability.Decide ]
        true, false, true, [ Capability.Register; Capability.RecordPayment ]
        false, true, true, [ Capability.Decide; Capability.RecordPayment ]
        true, true, true, [ Capability.Register; Capability.Decide; Capability.RecordPayment ]
    ]

let private replacementCorrection
    registrationChanged
    decisionChanged
    paymentChanged
    : AuthoredCorrection =
    {
        Registration =
            (if registrationChanged then
                 Amendment.Replace registration
             else
                 Amendment.Keep)
        Decision =
            (if decisionChanged then
                 Change.Replace decision
             else
                 Change.Keep)
        Payment =
            (if paymentChanged then
                 Change.Replace(
                     ClaimCore.TestSupport.Inputs.paymentFrom
                         [ BusinessField.PaymentDate, ("2026-08-20") ]
                 )
             else
                 Change.Keep)
    }

let private corrections () =
    for registrationChanged, decisionChanged, paymentChanged, expected in correctionMasks do
        let correction =
            replacementCorrection registrationChanged decisionChanged paymentChanged

        let act = (AuthoredCaseAct.Correct correction)

        Expect.equal
            (Authority.requiredCapabilities (CaseFixtures.act act))
            expected
            "Seven selected masks and all-KEEP"

        let clearedDecision = if decisionChanged then Change.Clear else Change.Keep

        let clearedPayment = if paymentChanged then Change.Clear else Change.Keep

        let cleared =
            { correction with
                Decision = clearedDecision
                Payment = clearedPayment
            }

        Expect.equal
            (Authority.requiredCapabilities (CaseFixtures.act (AuthoredCaseAct.Correct cleared)))
            expected
            "CLEAR selects the same capability"

let private reaffirmations () =
    let handler =
        actor
            human
            [
                {
                    Role = Role.CaseHandler
                    Scope = Scope.Installation
                }
            ]

    let paymentReaffirmation =
        (AuthoredCaseAct.Correct
            {
                Registration = Amendment.Keep
                Decision = Change.Keep
                Payment =
                    Change.Replace(
                        ClaimCore.TestSupport.Inputs.paymentFrom
                            [ BusinessField.PaymentDate, ("2026-08-20") ]
                    )
            })

    Expect.isFalse
        (Authority.canAct
            human
            (AuthorityView.ofActor (Some handler))
            (Scope.Case caseId)
            (CaseFixtures.act paymentReaffirmation))
        "An unchanged payment assertion still requires payment authority"

    let keep =
        (AuthoredCaseAct.Correct
            {
                Registration = Amendment.Keep
                Decision = Change.Keep
                Payment = Change.Keep
            })

    Expect.isError
        (CaseFixtures.decide
            today
            (actRequest (Revision.value (Case.snapshot (paid ())).Revision) keep)
            (Some(paid ())))
        "All-KEEP never creates a revision"

let private scopedAdvice () =
    let installation = Guid.Parse "90000000-0000-4000-8000-000000000001"
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
            Role.CaseRegistrar, [ "registration" ], [ "SUMMARY" ]
            Role.PaymentRecorder, [ "payment" ], [ "SUMMARY" ]
            Role.CaseHandler, [ "registration"; "decision" ], [ "SUMMARY"; "FULL" ]
        ] do
        let authority =
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
                (AuthorityView.ofActor (Some authority))
                record
            |> accepted

        let correction =
            advice.Advice.Correction
            |> Option.defaultWith (fun () -> failtest "Correct needs choices")

        Expect.contains advice.Advice.Acts CaseActKind.Correct "Correction is explicitly offered"

        Expect.equal
            advice.Snapshot.Revision
            snapshot.Revision
            "The snapshot owns the expected revision"

        let history =
            advice.Advice.History
            |> List.map (function
                | HistoryDetail.Summary -> "SUMMARY"
                | HistoryDetail.Full -> "FULL")

        Expect.equal history expectedHistory "History reflects the actual case grant"

        for group, values in
            [
                "registration", correction.Registration
                "decision", correction.Decision
                "payment", correction.Payment
            ] do
            Expect.equal
                (values <> [ CorrectionGroupAction.Keep ])
                (List.contains group expectedGroups)
                "Unauthorized correction groups remain required KEEP"

let private absentCorrectionValues () =
    let record = opened ()

    Expect.equal
        (Case.correctionActions record RecordGroup.Decision)
        [ CorrectionGroupAction.Keep ]
        "A correction cannot introduce a missing decision"

    Expect.equal
        (Case.correctionActions record RecordGroup.Payment)
        [ CorrectionGroupAction.Keep ]
        "A correction cannot introduce a missing payment"

    Expect.equal
        (Case.correctionActions (decided ()) RecordGroup.Payment)
        [ CorrectionGroupAction.Keep ]
        "Decision presence does not permit introducing a payment through correction"

let private noUsableCorrectionOffer () =
    let installation = Guid.Parse "90000000-0000-4000-8000-000000000001"

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

    let authority =
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
            (AuthorityView.ofActor (Some authority))
            record
        |> accepted

    Expect.isFalse
        (List.contains CaseActKind.Correct advice.Advice.Acts)
        "An all-KEEP form is never offered"

let private humanOnlyCapabilities () =
    let derived policy =
        Role.all
        |> List.filter (fun role -> policy role |> List.exists Authority.humanOnlyCapability)

    Expect.equal (derived Authority.policy) humanRoles "Current human-only roles"

    Expect.equal
        (Role.all |> List.filter Authority.humanOnlyRole)
        humanRoles
        "Production roles derive from capabilities"

    let withSteward role =
        let granted = Authority.policy role

        if role = Role.CaseReader then
            Capability.Steward :: granted
        else
            granted

    Expect.equal
        (derived withSteward)
        (Role.all
         |> List.filter (fun role -> role = Role.CaseReader || List.contains role humanRoles))
        "Negative control: granting Steward changes the principal restriction"

[<Tests>]
let tests =
    testList
        "actor authorization policy"
        [
            testCase
                "human-only roles derive from capabilities with a Steward-grant negative control"
                humanOnlyCapabilities
            testCase
                "all role, capability, principal and scope combinations match independent expectations"
                matrix
            testCase
                "invalid actors and installation authority boundaries are default deny"
                invalidActors
            testCase
                "ordinary business and stewardship acts have independent required capabilities"
                caseActCapabilities
            testCase
                "correction group choices require every selected capability including reaffirmation"
                corrections
            testCase "reaffirmations require their capability and all-KEEP refuses" reaffirmations
            testCase "case scoped advice fixes unauthorized correction groups at KEEP" scopedAdvice
            testCase "correction choice advice shares unary execution guards" absentCorrectionValues
            testCase
                "advice omits an unusable correction whose every choice is KEEP"
                noUsableCorrectionOffer
        ]
