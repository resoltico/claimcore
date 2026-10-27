namespace ClaimCore.Protocol

open ClaimCore.Domain

open ClaimCore.Protocol
open ClaimCore.Json


module RequestCodecs =
    open Codec

    let revision = integer 0L JsonValue.MaximumInteger
    let sequence = integer 1L JsonValue.MaximumInteger

    let tokens values =
        values |> List.map (fun value -> value, value) |> enumeration

    let registration = Record.registration.Codec text
    let decision = Record.decision.Codec text
    let payment = Record.payment.Codec text

    let private correctionVariants group variants =
        CorrectionActions.forGroup group
        |> List.map (fun action ->
            variants |> List.find (fun make -> make.Tag = CorrectionActions.token action))

    let registrationGroup =
        tagged
            "RegistrationGroup"
            (correctionVariants
                RecordGroup.Registration
                [
                    unitVariant
                        (CorrectionActions.token CorrectionGroupAction.Keep)
                        RegistrationGroup.Keep
                    variant
                        (CorrectionActions.token CorrectionGroupAction.Replace)
                        RegistrationGroup.Replace
                        (function
                        | RegistrationGroup.Replace value -> Some value
                        | _ -> None)
                        (record id |> field "values" id registration)
                ])

    let decisionGroup =
        tagged
            "DecisionGroup"
            (correctionVariants
                RecordGroup.Decision
                [
                    unitVariant
                        (CorrectionActions.token CorrectionGroupAction.Keep)
                        DecisionGroup.Keep
                    variant
                        (CorrectionActions.token CorrectionGroupAction.Replace)
                        DecisionGroup.Replace
                        (function
                        | DecisionGroup.Replace value -> Some value
                        | _ -> None)
                        (record id |> field "values" id decision)
                    unitVariant
                        (CorrectionActions.token CorrectionGroupAction.Clear)
                        DecisionGroup.Clear
                ])

    let paymentGroup =
        tagged
            "PaymentGroup"
            (correctionVariants
                RecordGroup.Payment
                [
                    unitVariant
                        (CorrectionActions.token CorrectionGroupAction.Keep)
                        PaymentGroup.Keep
                    variant
                        (CorrectionActions.token CorrectionGroupAction.Replace)
                        PaymentGroup.Replace
                        (function
                        | PaymentGroup.Replace value -> Some value
                        | _ -> None)
                        (record id |> field "values" id payment)
                    unitVariant
                        (CorrectionActions.token CorrectionGroupAction.Clear)
                        PaymentGroup.Clear
                ])

    let correction =
        (record (fun v0 v1 v2 ->
            {
                CorrectionInput.Registration = v0
                Decision = v1
                Payment = v2
            })
        : Fields<CorrectionInput, _>)
        |> field "registration" _.Registration (registrationGroup)
        |> field "decision" _.Decision (decisionGroup)
        |> field "payment" _.Payment (paymentGroup)
        |> build
        |> named "Correction"

    let caseAct = Record.caseCodec text correction

    let caseRequestFields =
        (record (fun v0 v1 v2 v3 ->
            {
                UnvalidatedCaseRequest.OperationId = v0
                CaseReference = v1
                ExpectedRevision = v2
                Act = v3
            })
        : Fields<UnvalidatedCaseRequest, _>)
        |> field "operationId" _.OperationId (uuid)
        |> field (Record.key BusinessField.CaseReference) _.CaseReference (text)
        |> field "expectedRevision" _.ExpectedRevision (revision)
        |> field "act" _.Act (caseAct)

    let caseRequest = caseRequestFields |> build |> named "CaseRequest"

    let principal =
        (record (fun v0 v1 v2 ->
            {
                Principal.Kind = v0
                Issuer = v1
                Subject = v2
            })
        : Fields<Principal, _>)
        |> field "kind" _.Kind (tokens PrincipalKinds.all)
        |> field "issuer" _.Issuer (text)
        |> field "subject" _.Subject (text)
        |> build
        |> named "Principal"

    let scope =
        tagged
            "Scope"
            [
                unitVariant "INSTALLATION" GrantTarget.Installation
                variant
                    "CASE"
                    GrantTarget.CaseReference
                    (function
                    | GrantTarget.CaseReference value -> Some value
                    | _ -> None)
                    (record id |> field (Record.key BusinessField.CaseReference) id text)
            ]

    let authorityActWith role =
        Record.authorityVariants role scope |> tagged "AuthorityAct"

    let authorityAct =
        authorityActWith (tokens (RoleTokens.all |> List.map RoleTokens.token))

    let authorityRequestFields =
        (record (fun v0 v1 v2 ->
            {
                AuthorityRequest.OperationId = v0
                Target = v1
                Act = v2
            })
        : Fields<AuthorityRequest, _>)
        |> field "operationId" _.OperationId (uuid)
        |> field "target" _.Target (principal)
        |> field "act" _.Act (authorityAct)

    let authorityRequest = authorityRequestFields |> build |> named "AuthorityRequest"

    let private caseVariant =
        variant
            OperationSubjects.Case
            OperationInput.Case
            (function
            | OperationInput.Case value -> Some value
            | _ -> None)
            caseRequestFields

    let private authorityVariant =
        variant
            OperationSubjects.Authority
            OperationInput.Authority
            (function
            | OperationInput.Authority value -> Some value
            | _ -> None)
            authorityRequestFields

    let operationInput = tagged "OperationInput" [ caseVariant; authorityVariant ]

    let readProfile =
        {
            ReadProfile.Text = text
            Cursor = boundedText 1 512
            Limit = integer 1L (int64 Paging.MaximumPageSize) |> map int int64
            Detail = VocabularyCodec.create HistoryDetails.vocabulary
        }

    let pageRequest = Record.caseList.Codec readProfile
    let caseGetRequest = Record.caseGet.Codec readProfile
    let historyRequest = Record.caseHistory.Codec readProfile
    let grantsRequest = Record.authorityGrants.Codec readProfile
    let observeRequest = Record.observe.Codec readProfile
    let empty = Record.context.Codec readProfile
