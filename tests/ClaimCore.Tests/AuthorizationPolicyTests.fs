module ClaimCore.Tests.AuthorizationPolicyTests

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
    Expect.equal Roles.all.Length 7 "Seven roles"

    Expect.isTrue
        (Set.ofList Roles.all = (expectedRoles |> List.map (fun (role, _, _) -> role) |> Set.ofList))
        "Exact role inventory"

    Expect.isTrue
        (Set.ofList Capabilities.all = Set.ofList allCapabilities)
        "Exact capability inventory"

    for role, token, capabilities in expectedRoles do
        Expect.equal (Roles.token role) token "Independent role token"
        Expect.equal (Roles.tryParse token) (Some role) "Role parser"

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
                            (ActorAuthorization.can principal (Some current) capability resource)
                            expected
                            "Independent role, principal and scope expectation"

    for token in [ "CASE_EDITOR"; "case_handler"; "" ] do
        Expect.isNone (Roles.tryParse token) "Unsupported role names refuse"

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
                (Some invalid)
                Capability.ReadCase
                (ResourceScope.Case caseId))
            "Invalid actor grants no rights"

    Expect.isFalse
        (ActorAuthorization.can human None Capability.ReadCase (ResourceScope.Case caseId))
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
            (Some scoped)
            Capability.ReadAuthority
            ResourceScope.Installation)
        "Case-scoped audit does not enumerate the installation"

    for value in [ String([| char 0xD800 |]); "a\u0000b"; "a\ufdd0b"; "a\uffffb" ] do
        Expect.isError
            (PrincipalKey.human "https://issuer.example/" value)
            "Identity text has permanent Unicode bounds"

let private ordinaryActs () =
    let cases =
        [
            CaseAct.Business(BusinessCommand.Open registration), Capability.Register
            CaseAct.Business(BusinessCommand.AmendRegistration registration), Capability.Register
            CaseAct.Business(BusinessCommand.Decide decision), Capability.Decide
            CaseAct.Business BusinessCommand.WithdrawDecision, Capability.Decide
            CaseAct.Business(BusinessCommand.RecordPayment "2026-08-20"), Capability.RecordPayment
            CaseAct.Business BusinessCommand.ClearPayment, Capability.RecordPayment
            CaseAct.Business BusinessCommand.Close, Capability.Close
            CaseAct.Business BusinessCommand.Reopen, Capability.Close
            CaseAct.Stewardship(StewardshipAct.Void "Reason"), Capability.Steward
            CaseAct.Stewardship(StewardshipAct.Reinstate "Reason"), Capability.Steward
            CaseAct.Stewardship StewardshipAct.Erase, Capability.Steward
        ]

    for act, capability in cases do
        Expect.equal
            (ActorAuthorization.requiredCapabilities act)
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

let private replacementCorrection registrationChanged decisionChanged paymentChanged =
    {
        Registration =
            (if registrationChanged then
                 RegistrationCorrection.Replace registration
             else
                 RegistrationCorrection.Keep)
        Decision =
            (if decisionChanged then
                 DecisionCorrection.Replace decision
             else
                 DecisionCorrection.Keep)
        Payment =
            (if paymentChanged then
                 PaymentCorrection.Replace "2026-08-20"
             else
                 PaymentCorrection.Keep)
    }

let private corrections () =
    for registrationChanged, decisionChanged, paymentChanged, expected in correctionMasks do
        let correction =
            replacementCorrection registrationChanged decisionChanged paymentChanged

        let act = CaseAct.Business(BusinessCommand.CorrectCase correction)

        Expect.equal
            (ActorAuthorization.requiredCapabilities act)
            expected
            "Seven selected masks and all-KEEP"

        let clearedDecision =
            if decisionChanged then
                DecisionCorrection.Clear
            else
                DecisionCorrection.Keep

        let clearedPayment =
            if paymentChanged then
                PaymentCorrection.Clear
            else
                PaymentCorrection.Keep

        let cleared =
            { correction with
                Decision = clearedDecision
                Payment = clearedPayment
            }

        Expect.equal
            (ActorAuthorization.requiredCapabilities (
                CaseAct.Business(BusinessCommand.CorrectCase cleared)
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
        CaseAct.Business(
            BusinessCommand.CorrectCase
                {
                    Registration = RegistrationCorrection.Keep
                    Decision = DecisionCorrection.Keep
                    Payment = PaymentCorrection.Replace "2026-08-20"
                }
        )

    Expect.isFalse
        (ActorAuthorization.canAct
            human
            (Some handler)
            (ResourceScope.Case caseId)
            paymentReaffirmation)
        "An unchanged payment assertion still requires payment authority"

    let keep =
        CaseAct.Business(
            BusinessCommand.CorrectCase
                {
                    Registration = RegistrationCorrection.Keep
                    Decision = DecisionCorrection.Keep
                    Payment = PaymentCorrection.Keep
                }
        )

    Expect.isError
        (Case.decide today (actRequest ((Case.snapshot (paid ())).Revision) keep) (Some(paid ())))
        "All-KEEP never creates a revision"

[<Tests>]
let tests =
    testList
        "actor authorization policy"
        [
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
        ]
