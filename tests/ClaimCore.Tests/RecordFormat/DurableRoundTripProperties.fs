module ClaimCore.Tests.DurableRoundTripProperties

open System
open System.Globalization
open System.Text.Json
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.Tests.PropertyHarness

let private text =
    Gen.choice
        [
            Gen.string (Range.constant 1 80) Gen.alpha
            Gen.item [ "Žąsis"; "東京"; "😀"; "é"; "مرحبا"; "quoted\"value"; "path\\value" ]
        ]

[<NoEquality; NoComparison>]
type private PermanentValueSample =
    {
        OperationId: Guid
        Reference: string
        Revision: int64
        Registration: RegistrationInput
        Decision: DecisionInput
        Payment: PaymentInput
        Reason: string
    }

let private sample =
    gen {
        let! id = Gen.guid
        let! reference = Gen.string (Range.constant 1 80) Gen.alphaNum
        let! revision = Gen.int64 (Range.constant 1L 9007199254740991L)
        let! day = Gen.int32 (Range.constant 0 (DateOnly.MaxValue.DayNumber - 40))
        let! gap = Gen.int32 (Range.constant 0 10)
        let! decisionGap = Gen.int32 (Range.constant 1 10)
        let! paymentGap = Gen.int32 (Range.constant 1 10)
        let! country = Gen.string (Range.constant 2 2) Gen.upper
        let! currency = Gen.string (Range.constant 3 3) Gen.upper
        let! claimant = text
        let! insurer = text
        let! reason = text
        let! whole = Gen.int32 (Range.constant 1 1000000000)
        let! fraction = Gen.int32 (Range.constant 0 9999)

        let date offset =
            DateOnly
                .FromDayNumber(day + offset)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

        let amount =
            whole.ToString(CultureInfo.InvariantCulture)
            + "."
            + fraction.ToString("D4", CultureInfo.InvariantCulture)

        let registration: RegistrationInput =
            (ClaimCore.TestSupport.Inputs.registrationFrom
                [
                    BusinessField.IncidentDate, (date 0)
                    BusinessField.IncidentNotificationDate, (date gap)
                    BusinessField.IncidentCountry, (country)
                    BusinessField.ClaimantName, (claimant)
                    BusinessField.InsurerName, (insurer)
                    BusinessField.ClaimedAmount, (amount)
                    BusinessField.ClaimedCurrency, (currency)
                ])

        let decision: DecisionInput =
            (ClaimCore.TestSupport.Inputs.decisionFrom
                [
                    BusinessField.PaymentDecisionDate, (date (gap + decisionGap))
                    BusinessField.PayableAmount, (amount)
                    BusinessField.PayableCurrency, (currency)
                ])

        let payment: PaymentInput =
            (ClaimCore.TestSupport.Inputs.paymentFrom
                [ BusinessField.PaymentDate, (date (gap + decisionGap + paymentGap)) ])

        let id =
            if id = Guid.Empty then
                Guid.Parse "20000000-0000-4000-8000-000000000001"
            else
                id

        return
            {
                OperationId = id
                Reference = reference
                Revision = revision
                Registration = registration
                Decision = decision
                Payment = payment
                Reason = reason
            }
    }

let private tokenAt path expected (bytes: byte array) =
    use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))

    let value =
        path
        |> List.fold
            (fun (node: JsonElement) (key: string) -> node.GetProperty key)
            document.RootElement

    value.GetString() = expected

let private caseActs = Inputs.caseActs

let private principalKinds =
    Inventory.tokens "principalKinds"
    |> List.map (fun token ->
        Vocabulary.tryParse PrincipalKind.vocabulary token |> Option.get, token)

let private statuses = [ CaseStatus.Opened, "OPENED"; CaseStatus.Closed, "CLOSED" ]

let private roles =
    Inventory.tokens "roles"
    |> List.map (fun token -> Role.parse token |> Option.get, token)

let private scopes reference =
    [
        RecordedGrantScope.Installation, "INSTALLATION"
        RecordedGrantScope.Case(
            (CaseFixtures.caseId (Guid.Parse "d0000000-0000-4000-8000-000000000001")),
            CaseFixtures.reference reference
        ),
        "CASE"
    ]

let private authorityActs reference =
    [
        for role, _ in roles do
            for scope, _ in scopes reference do
                yield
                    {
                        RecordedGrant.Role = role
                        Scope = scope
                    }
    ]
    |> Inputs.authorityActs

let private inventories () =
    let registration, decision, payment =
        Inputs.registration, Inputs.decision, Inputs.payment

    let acts = caseActs registration decision payment "reason"
    Expect.equal acts.Length (Inventory.tokens "caseActs").Length "Inventoried case kinds"

    Expect.equal
        (acts |> List.map (fst >> CaseAct.kind) |> Set.ofList)
        (Set.ofList CaseActKind.all)
        "Case inventory"

    Expect.equal
        (statuses |> List.map fst |> Set.ofList)
        (Set.ofList CaseStatus.all)
        "Status inventory"

    Expect.equal statuses.Length 2 "Both statuses occur every trial"

    Expect.equal
        (principalKinds |> List.map fst |> Set.ofList)
        (Vocabulary.all PrincipalKind.vocabulary |> Set.ofList)
        "Principal inventory"

    Expect.equal (roles |> List.map fst |> Set.ofList) (Set.ofList Role.all) "Role inventory"
    Expect.equal roles.Length (Inventory.tokens "roles").Length "Every role occurs every trial"

    Expect.equal
        (scopes "ref"
         |> List.map (fun (scope, _) ->
             let case, _ =
                 Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(
                     scope,
                     typeof<RecordedGrantScope>
                 )

             case.Name)
         |> Set.ofList)
        (Microsoft.FSharp.Reflection.FSharpType.GetUnionCases typeof<RecordedGrantScope>
         |> Array.map _.Name
         |> Set.ofArray)
        "The authored scope inventory covers every durable scope case"

    Expect.equal
        (authorityActs "ref" |> List.map (fst >> AuthorityAct.kind) |> Set.ofList)
        (Set.ofList AuthorityKind.all)
        "Authority inventory"

let private caseProperty =
    property {
        let! values = sample
        let id = values.OperationId
        let reference = values.Reference
        let revision = values.Revision
        let registration = values.Registration
        let decision = values.Decision
        let payment = values.Payment
        let reason = values.Reason

        return
            caseActs registration decision payment reason
            |> List.forall (fun (act, token) ->
                let request: UnvalidatedCaseRequest =
                    {
                        OperationId = id
                        CaseReference = reference
                        ExpectedRevision = revision
                        Act = act
                    }

                let expected = CaseFixtures.command request
                let bytes = RequestRecord.encode expected

                RequestRecord.decode Int32.MaxValue bytes = Ok expected
                && expected.Authored = request
                && tokenAt [ "act"; "kind" ] token bytes)
    }

let private snapshotProperty =
    property {
        let! values = sample
        let reference = values.Reference
        let revision = values.Revision
        let registration = values.Registration
        let decision = values.Decision
        let payment = values.Payment
        let reference = Inputs.reference reference

        return
            [
                for status, statusToken in statuses do
                    for decision, payment in
                        [
                            None, None
                            Some(Inputs.admittedDecision decision), None
                            Some(Inputs.admittedDecision decision),
                            Some(Inputs.admittedPayment payment)
                        ] do
                        let fields: CaseFields =
                            {
                                Work = CaseWork.empty
                                CaseReference = reference
                                Status = status
                                Registration = Inputs.admittedRegistration registration
                                Progress =
                                    ClaimCore.TestSupport.CaseFixtures.progress (decision) (payment)
                            }

                        for state, lifecycle in
                            [ CaseState.Active fields, "ACTIVE"; CaseState.Voided fields, "VOIDED" ] do
                            let expected: CaseSnapshot =
                                {
                                    CaseId =
                                        CaseFixtures.caseId (
                                            System.Guid.Parse "20000000-0000-4000-8000-000000000001"
                                        )
                                    Reference = reference
                                    Revision = (CaseFixtures.revision revision)
                                    State = state
                                }

                            let bytes = CaseRecord.encodeSnapshot expected

                            yield
                                CaseRecord.decodeSnapshot bytes = Ok expected
                                && tokenAt [ "state"; "kind" ] lifecycle bytes
                                && tokenAt [ "state"; "fields"; "status" ] statusToken bytes
            ]
            |> List.forall id
    }

let private grantTokens act bytes =
    match act with
    | AuthorityAct.Grant grant
    | AuthorityAct.Revoke grant ->
        let scope =
            match grant.Scope with
            | RecordedGrantScope.Installation -> "INSTALLATION"
            | RecordedGrantScope.Case _ -> "CASE"

        tokenAt
            [ "act"; "role" ]
            (roles |> List.find (fun (role, _) -> role = grant.Role) |> snd)
            bytes
        && tokenAt [ "act"; "scope"; "kind" ] scope bytes
    | _ -> true

let private authorityProperty =
    property {
        let! values = sample
        let id = values.OperationId
        let reference = values.Reference
        let subject = values.Reason

        return
            [
                for act, token in authorityActs reference do
                    // Provisioning is human-only; every other durable shape preserves both principal kinds.
                    let kinds =
                        if token = "PROVISION_OWNER" then
                            [ PrincipalKind.Human, "HUMAN" ]
                        else
                            principalKinds

                    for kind, kindToken in kinds do
                        let expected: AuthorityRequestRecord =
                            {
                                OperationId = id
                                Target =
                                    {
                                        Kind = kind
                                        Issuer = "https://issuer-" + id.ToString("N") + ".example/"
                                        Subject = subject
                                    }
                                Act = act
                            }

                        let bytes = AuthorityRecord.encode expected

                        yield
                            AuthorityRecord.decode Int32.MaxValue bytes = Ok expected
                            && tokenAt [ "act"; "kind" ] token bytes
                            && tokenAt [ "target"; "kind" ] kindToken bytes
                            && grantTokens act bytes
            ]
            |> List.forall (fun value -> value)
    }

[<Tests>]
let tests =
    testList
        "generated permanent inventories"
        [
            testCase
                "inventories explicitly cover all durable kinds, statuses, roles and scopes"
                inventories
            testCase "every case kind round trips varied permanent values on every trial" (fun () ->
                run "permanent case inventory" caseProperty)
            testCase
                "every status round trips every live lifecycle and progress shape on every trial"
                (fun () -> run "permanent snapshot inventory" snapshotProperty)
            testCase "every authority kind, role and scope round trips on every trial" (fun () ->
                run "permanent authority inventory" authorityProperty)
        ]
