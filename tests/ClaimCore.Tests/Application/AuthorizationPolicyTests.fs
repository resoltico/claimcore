module ClaimCore.Tests.AuthorizationPolicyTests

open ClaimCore.TestSupport

open ClaimCore.Contract

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Tests.Fixtures

let private human =
    PrincipalKey.human "https://issuer.example/" "worker" |> accepted

let private service =
    PrincipalKey.service "https://issuer.example/" "worker" |> accepted

let private caseId = Guid.Parse "c0000000-0000-4000-8000-000000000001"
let private otherCaseId = Guid.Parse "c0000000-0000-4000-8000-000000000002"

let private actor principal grants : ActorAuthority =
    {
        ActorId = Guid.Parse "a0000000-0000-4000-8000-000000000001"
        Principal = principal
        Enabled = true
        Grants = grants
    }

let private expectedRoles =
    [
        Role.Owner, "OWNER", [ Capability.ManageAuthority; Capability.ReadAuthority ]
        Role.CaseReader, "CASE_READER", [ Capability.ReadCase; Capability.ReadHistory ]
        Role.CaseRegistrar,
        "CASE_REGISTRAR",
        [ Capability.ReadCase; Capability.ReadHistory; Capability.Register ]
        Role.CaseHandler,
        "CASE_HANDLER",
        [
            Capability.ReadCase
            Capability.ReadHistory
            Capability.ReadHistoryFull
            Capability.Register
            Capability.Decide
            Capability.Close
        ]
        Role.PaymentRecorder,
        "PAYMENT_RECORDER",
        [ Capability.ReadCase; Capability.ReadHistory; Capability.RecordPayment ]
        Role.DataSteward,
        "DATA_STEWARD",
        [ Capability.ReadCase; Capability.ReadHistory; Capability.Steward ]
        Role.Auditor,
        "AUDITOR",
        [
            Capability.ReadCase
            Capability.ReadHistory
            Capability.ReadHistoryFull
            Capability.ReadAuthority
        ]
    ]

let private allCapabilities =
    [
        Capability.ReadCase
        Capability.ReadHistory
        Capability.ReadHistoryFull
        Capability.Register
        Capability.Decide
        Capability.RecordPayment
        Capability.Close
        Capability.Steward
        Capability.ManageAuthority
        Capability.ReadAuthority
    ]

let private expectedPermission role capabilities principal grant resource capability =
    let humanRole = role = Role.Owner || role = Role.DataSteward

    let installationOnly =
        capability = Capability.ManageAuthority || capability = Capability.ReadAuthority

    let scope =
        match grant, resource with
        | GrantScope.Installation, ResourceScope.Installation -> true
        | GrantScope.Installation, ResourceScope.Case id -> id <> Guid.Empty && not installationOnly
        | GrantScope.Case granted, ResourceScope.Case requested ->
            granted = requested && requested <> Guid.Empty && not installationOnly
        | _ -> false

    List.contains capability capabilities
    && scope
    && (not humanRole || principal = human)

let private matrix () =
    Expect.equal RoleTokens.all.Length 7 "Seven roles"

    Expect.isTrue
        (Set.ofList RoleTokens.all =
            (expectedRoles |> List.map (fun (role, _, _) -> role) |> Set.ofList))
        "Exact role inventory"

    Expect.isTrue
        (Set.ofList Capabilities.all = Set.ofList allCapabilities)
        "Exact capability inventory"

    for role, token, capabilities in expectedRoles do
        Expect.equal (RoleTokens.token role) token "Independent role token"
        Expect.equal (RoleTokens.parse token) (Some role) "Role parser"

        for principal in [ human; service ] do
            for scope in [ GrantScope.Installation; GrantScope.Case caseId ] do
                let current = actor principal [ { Role = role; Scope = scope } ]

                for resource in
                    [
                        ResourceScope.Installation
                        ResourceScope.Case caseId
                        ResourceScope.Case otherCaseId
                        ResourceScope.Case Guid.Empty
                    ] do
                    for capability in allCapabilities do
                        let expected =
                            expectedPermission role capabilities principal scope resource capability

                        Expect.equal
                            (ActorAuthorization.can
                                principal
                                (AuthorityView.ofActor (Some current))
                                capability
                                resource)
                            expected
                            "Independent role, principal and scope expectation"

    for token in [ "CASE_EDITOR"; "case_handler"; "" ] do
        Expect.isNone (RoleTokens.parse token) "Unsupported role names refuse"

let private invalidPrincipals () =
    for value in [ String([| char 0xD800 |]); "a\u0000b"; "a﷐b"; "a￿b" ] do
        Expect.isError
            (PrincipalKey.human "https://issuer.example/" value)
            "Identity text has permanent Unicode bounds"

let private invalidActors () =
    let current =
        actor
            human
            [
                {
                    Role = Role.CaseHandler
                    Scope = GrantScope.Installation
                }
            ]

    for invalid in
        [
            { current with ActorId = Guid.Empty }
            { current with Grants = [] }
            { current with Enabled = false }
            { current with Principal = service }
        ] do
        Expect.isFalse
            (ActorAuthorization.can
                human
                (AuthorityView.ofActor (Some invalid))
                Capability.ReadCase
                (ResourceScope.Case caseId))
            "Invalid actor grants no rights"

    Expect.isFalse
        (ActorAuthorization.can
            human
            AuthorityView.empty
            Capability.ReadCase
            (ResourceScope.Case caseId))
        "Absent actor grants no rights"

    let scoped =
        actor
            human
            [
                {
                    Role = Role.Auditor
                    Scope = GrantScope.Case caseId
                }
            ]

    Expect.isFalse
        (ActorAuthorization.can
            human
            (AuthorityView.ofActor (Some scoped))
            Capability.ReadAuthority
            ResourceScope.Installation)
        "Case-scoped audit does not enumerate the installation"

    invalidPrincipals ()

let private ordinaryActs () =
    let cases =
        [
            UnvalidatedCaseAct.Open registration, Capability.Register
            UnvalidatedCaseAct.AmendRegistration registration, Capability.Register
            UnvalidatedCaseAct.Decide decision, Capability.Decide
            UnvalidatedCaseAct.WithdrawDecision, Capability.Decide
            UnvalidatedCaseAct.RecordPayment { PaymentDate = "2026-08-20" },
            Capability.RecordPayment
            UnvalidatedCaseAct.ClearPayment, Capability.RecordPayment
            UnvalidatedCaseAct.Close, Capability.Close
            UnvalidatedCaseAct.Reopen, Capability.Close
            UnvalidatedCaseAct.Void "Reason", Capability.Steward
            UnvalidatedCaseAct.Reinstate "Reason", Capability.Steward
            UnvalidatedCaseAct.Erase, Capability.Steward
        ]

    for act, capability in cases do
        Expect.equal
            (ActorAuthorization.requiredCapabilities (CaseFixtures.act act))
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
    : CorrectionInput =
    {
        Registration =
            (if registrationChanged then
                 RegistrationGroup.Replace registration
             else
                 RegistrationGroup.Keep)
        Decision =
            (if decisionChanged then
                 DecisionGroup.Replace decision
             else
                 DecisionGroup.Keep)
        Payment =
            (if paymentChanged then
                 PaymentGroup.Replace { PaymentDate = "2026-08-20" }
             else
                 PaymentGroup.Keep)
    }

let private corrections () =
    for registrationChanged, decisionChanged, paymentChanged, expected in correctionMasks do
        let correction =
            replacementCorrection registrationChanged decisionChanged paymentChanged

        let act = (UnvalidatedCaseAct.Correct correction)

        Expect.equal
            (ActorAuthorization.requiredCapabilities (CaseFixtures.act act))
            expected
            "Seven selected masks and all-KEEP"

        let clearedDecision =
            if decisionChanged then
                DecisionGroup.Clear
            else
                DecisionGroup.Keep

        let clearedPayment =
            if paymentChanged then
                PaymentGroup.Clear
            else
                PaymentGroup.Keep

        let cleared =
            { correction with
                Decision = clearedDecision
                Payment = clearedPayment
            }

        Expect.equal
            (ActorAuthorization.requiredCapabilities (
                CaseFixtures.act (UnvalidatedCaseAct.Correct cleared)
            ))
            expected
            "CLEAR selects the same capability"

let private reaffirmations () =
    let handler =
        actor
            human
            [
                {
                    Role = Role.CaseHandler
                    Scope = GrantScope.Installation
                }
            ]

    let paymentReaffirmation =
        (UnvalidatedCaseAct.Correct
            {
                Registration = RegistrationGroup.Keep
                Decision = DecisionGroup.Keep
                Payment = PaymentGroup.Replace { PaymentDate = "2026-08-20" }
            })

    Expect.isFalse
        (ActorAuthorization.canAct
            human
            (AuthorityView.ofActor (Some handler))
            (ResourceScope.Case caseId)
            (CaseFixtures.act paymentReaffirmation))
        "An unchanged payment assertion still requires payment authority"

    let keep =
        (UnvalidatedCaseAct.Correct
            {
                Registration = RegistrationGroup.Keep
                Decision = DecisionGroup.Keep
                Payment = PaymentGroup.Keep
            })

    Expect.isError
        (CaseFixtures.decide
            today
            (actRequest ((Case.snapshot (paid ())).Revision) keep)
            (Some(paid ())))
        "All-KEEP never creates a revision"

let private scopedAdvice () =
    let installation = Guid.Parse "90000000-0000-4000-8000-000000000001"
    let snapshot = Case.snapshot (paid ())

    let target =
        ClaimCore.RecordFormat.CaseReferenceId.encode
            installation
            (CaseReference.format snapshot.Reference)

    let record: StoredCase = { CaseId = target; Snapshot = snapshot }

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
                        Scope = GrantScope.Case target
                    }
                ]

        let advice =
            Queries.caseRecord installation human (AuthorityView.ofActor (Some authority)) record
            |> accepted

        let expected, correction =
            advice.Advice.Acts
            |> List.pick (function
                | CaseActOffer.Correct(expected, choices) -> Some(expected, choices)
                | _ -> None)

        Expect.equal expected snapshot.Revision "Revision bound in snapshot"

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
        opened () |> apply UnvalidatedCaseAct.Close |> accepted |> Case.snapshot

    let target =
        ClaimCore.RecordFormat.CaseReferenceId.encode
            installation
            (CaseReference.format snapshot.Reference)

    let record: StoredCase = { CaseId = target; Snapshot = snapshot }

    let authority =
        actor
            human
            [
                {
                    Role = Role.PaymentRecorder
                    Scope = GrantScope.Case target
                }
            ]

    let advice =
        Queries.caseRecord installation human (AuthorityView.ofActor (Some authority)) record
        |> accepted

    Expect.isFalse
        (advice.Advice.Acts
         |> List.exists (function
             | CaseActOffer.Correct _ -> true
             | _ -> false))
        "An all-KEEP form is never offered"

let private humanOnlyCapabilities () =
    let derived policy =
        RoleTokens.all
        |> List.filter (fun role ->
            policy role |> List.exists ActorAuthorization.humanOnlyCapability)

    Expect.equal
        (derived ActorAuthorization.capabilities)
        [ Role.Owner; Role.DataSteward ]
        "Current human-only roles"

    Expect.equal
        (RoleTokens.all |> List.filter ActorAuthorization.humanOnlyRole)
        [ Role.Owner; Role.DataSteward ]
        "Production roles derive from capabilities"

    let withSteward role =
        let granted = ActorAuthorization.capabilities role

        if role = Role.CaseReader then
            Capability.Steward :: granted
        else
            granted

    Expect.equal
        (derived withSteward)
        [ Role.Owner; Role.CaseReader; Role.DataSteward ]
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
                ordinaryActs
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
