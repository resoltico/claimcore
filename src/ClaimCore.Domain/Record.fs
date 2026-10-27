namespace ClaimCore.Domain

open System

[<RequireQualifiedAccess>]
type RecordGroup =
    | Registration
    | Decision
    | Payment

[<RequireQualifiedAccess>]
type CorrectionGroupAction =
    | Keep
    | Replace
    | Clear

[<RequireQualifiedAccess>]
type ActShape =
    | NoValues
    | RegistrationValues
    | DecisionValues
    | PaymentValues
    | ReasonText
    | CorrectionGroups

[<RequireQualifiedAccess>]
type RevisionInput =
    | New
    | Expected

/// Closed operation vocabulary; names and payload kinds belong to the core, not a renderer.
[<RequireQualifiedAccess>]
type CaseActKind =
    | Open
    | AmendRegistration
    | Correct
    | Decide
    | WithdrawDecision
    | RecordPayment
    | ClearPayment
    | Close
    | Reopen
    | Void
    | Reinstate
    | Erase

    static member shape(kind) =
        match kind with
        | CaseActKind.Open
        | CaseActKind.AmendRegistration -> ActShape.RegistrationValues
        | CaseActKind.Correct -> ActShape.CorrectionGroups
        | CaseActKind.Decide -> ActShape.DecisionValues
        | CaseActKind.RecordPayment -> ActShape.PaymentValues
        | CaseActKind.Void
        | CaseActKind.Reinstate -> ActShape.ReasonText
        | CaseActKind.WithdrawDecision
        | CaseActKind.ClearPayment
        | CaseActKind.Close
        | CaseActKind.Reopen
        | CaseActKind.Erase -> ActShape.NoValues

    static member revision(kind) =
        match kind with
        | CaseActKind.Open -> RevisionInput.New
        | CaseActKind.AmendRegistration
        | CaseActKind.Correct
        | CaseActKind.Decide
        | CaseActKind.WithdrawDecision
        | CaseActKind.RecordPayment
        | CaseActKind.ClearPayment
        | CaseActKind.Close
        | CaseActKind.Reopen
        | CaseActKind.Void
        | CaseActKind.Reinstate
        | CaseActKind.Erase -> RevisionInput.Expected

    static member prefill(kind) =
        match kind with
        | CaseActKind.AmendRegistration
        | CaseActKind.Correct
        | CaseActKind.Decide -> true
        | CaseActKind.Open
        | CaseActKind.WithdrawDecision
        | CaseActKind.RecordPayment
        | CaseActKind.ClearPayment
        | CaseActKind.Close
        | CaseActKind.Reopen
        | CaseActKind.Void
        | CaseActKind.Reinstate
        | CaseActKind.Erase -> false


[<RequireQualifiedAccess>]
type BusinessField =
    | IncidentDate
    | IncidentNotificationDate
    | IncidentCountry
    | ClaimantName
    | InsurerName
    | ClaimedAmount
    | ClaimedCurrency
    | CaseReference
    | PaymentDecisionDate
    | PayableAmount
    | PayableCurrency
    | PaymentDate
    | Status

[<RequireQualifiedAccess>]
type InputTarget =
    | Field of BusinessField
    | OperationId
    | ExpectedRevision
    | Group of RecordGroup
    | Reason
    | PrincipalIssuer
    | PrincipalSubject
    | Role
    | Limit
    | Cursor

[<RequireQualifiedAccess>]
type AuthorityKind =
    | ProvisionOwner
    | Register
    | Grant
    | Revoke
    | Enable
    | Disable

[<RequireQualifiedAccess>]
type ActSubject =
    | Case
    | Authority

/// The complete act vocabulary combines the disjoint case and authority subjects.
[<RequireQualifiedAccess>]
type ActKind =
    | Case of CaseActKind
    | Authority of AuthorityKind

[<RequireQualifiedAccess>]
type OperationEndpoint =
    | Preview
    | Execute

[<RequireQualifiedAccess>]
type QueryEndpoint =
    | CaseGet
    | CaseList
    | CaseHistory
    | Observe
    | Resolve
    | AuthorityList
    | AuthorityGrants
    | Context

[<RequireQualifiedAccess>]
type ActorEndpoint =
    | Operation of OperationEndpoint
    | Query of QueryEndpoint

type RegistrationInput =
    {
        IncidentDate: string
        IncidentNotificationDate: string
        IncidentCountry: string
        ClaimantName: string
        InsurerName: string
        ClaimedAmount: string
        ClaimedCurrency: string
    }

type DecisionInput =
    {
        PaymentDecisionDate: string
        PayableAmount: string
        PayableCurrency: string
    }

type PaymentInput = { PaymentDate: string }

/// Accepted registration values retain their validated structure.
type Registration =
    {
        IncidentDate: CalendarDate
        IncidentNotificationDate: CalendarDate
        IncidentCountry: CountryCode
        ClaimantName: PartyName
        InsurerName: PartyName
        ClaimedAmount: Money
    }

type Decision =
    {
        PaymentDecisionDate: CalendarDate
        PayableAmount: Money
    }

type Payment = { PaymentDate: CalendarDate }

[<RequireQualifiedAccess>]
type Change<'value> =
    | Keep
    | Replace of 'value
    | Clear

type Correction<'registration, 'decision, 'payment> =
    {
        Registration: Change<'registration>
        Decision: Change<'decision>
        Payment: Change<'payment>
    }

[<RequireQualifiedAccess>]
type CaseAct<'registration, 'decision, 'payment, 'reason> =
    | Open of 'registration
    | AmendRegistration of 'registration
    | Correct of Correction<'registration, 'decision, 'payment>
    | Decide of 'decision
    | WithdrawDecision
    | RecordPayment of 'payment
    | ClearPayment
    | Close
    | Reopen
    | Void of 'reason
    | Reinstate of 'reason
    | Erase

type AuthoredCaseAct = CaseAct<RegistrationInput, DecisionInput, PaymentInput, string>
type AuthoredCorrection = Correction<RegistrationInput, DecisionInput, PaymentInput>
type ValidCaseAct = CaseAct<Registration, Decision, Payment, Reason>
type ValidCorrection = Correction<Registration, Decision, Payment>

[<RequireQualifiedAccess>]
type ActPayload<'registration, 'decision, 'payment, 'reason> =
    | Empty
    | Registration of 'registration
    | Decision of 'decision
    | Payment of 'payment
    | Reason of 'reason
    | Correction of Correction<'registration, 'decision, 'payment>

module ActPayload =
    let registration =
        function
        | ActPayload.Registration value -> Some value
        | _ -> None

    let decision =
        function
        | ActPayload.Decision value -> Some value
        | _ -> None

    let payment =
        function
        | ActPayload.Payment value -> Some value
        | _ -> None

    let reason =
        function
        | ActPayload.Reason value -> Some value
        | _ -> None

    let correction =
        function
        | ActPayload.Correction value -> Some value
        | _ -> None

module CaseAct =
    let kind =
        function
        | CaseAct.Open _ -> CaseActKind.Open
        | CaseAct.AmendRegistration _ -> CaseActKind.AmendRegistration
        | CaseAct.Correct _ -> CaseActKind.Correct
        | CaseAct.Decide _ -> CaseActKind.Decide
        | CaseAct.WithdrawDecision -> CaseActKind.WithdrawDecision
        | CaseAct.RecordPayment _ -> CaseActKind.RecordPayment
        | CaseAct.ClearPayment -> CaseActKind.ClearPayment
        | CaseAct.Close -> CaseActKind.Close
        | CaseAct.Reopen -> CaseActKind.Reopen
        | CaseAct.Void _ -> CaseActKind.Void
        | CaseAct.Reinstate _ -> CaseActKind.Reinstate
        | CaseAct.Erase -> CaseActKind.Erase

    let private change transform =
        function
        | Change.Keep -> Ok Change.Keep
        | Change.Clear -> Ok Change.Clear
        | Change.Replace value -> transform value |> Result.map Change.Replace

    let traverse registration decision payment reason =
        function
        | CaseAct.Open value -> registration value |> Result.map CaseAct.Open
        | CaseAct.AmendRegistration value ->
            registration value |> Result.map CaseAct.AmendRegistration
        | CaseAct.Decide value -> decision value |> Result.map CaseAct.Decide
        | CaseAct.RecordPayment value -> payment value |> Result.map CaseAct.RecordPayment
        | CaseAct.Void value -> reason value |> Result.map CaseAct.Void
        | CaseAct.Reinstate value -> reason value |> Result.map CaseAct.Reinstate
        | CaseAct.Correct value ->
            change registration value.Registration
            |> Result.bind (fun r ->
                change decision value.Decision
                |> Result.bind (fun d ->
                    change payment value.Payment
                    |> Result.map (fun p ->
                        CaseAct.Correct
                            {
                                Registration = r
                                Decision = d
                                Payment = p
                            })))
        | CaseAct.WithdrawDecision -> Ok CaseAct.WithdrawDecision
        | CaseAct.ClearPayment -> Ok CaseAct.ClearPayment
        | CaseAct.Close -> Ok CaseAct.Close
        | CaseAct.Reopen -> Ok CaseAct.Reopen
        | CaseAct.Erase -> Ok CaseAct.Erase

    let map registration decision payment reason act =
        traverse (registration >> Ok) (decision >> Ok) (payment >> Ok) (reason >> Ok) act
        |> Result.defaultWith (fun () -> invalidOp "A total act mapping cannot fail.")

    let payload =
        function
        | CaseAct.Open value
        | CaseAct.AmendRegistration value -> ActPayload.Registration value
        | CaseAct.Correct value -> ActPayload.Correction value
        | CaseAct.Decide value -> ActPayload.Decision value
        | CaseAct.RecordPayment value -> ActPayload.Payment value
        | CaseAct.Void value
        | CaseAct.Reinstate value -> ActPayload.Reason value
        | CaseAct.WithdrawDecision
        | CaseAct.ClearPayment
        | CaseAct.Close
        | CaseAct.Reopen
        | CaseAct.Erase -> ActPayload.Empty

    // Exhaustive structural reconstruction; every valid kind/payload pairing is explicit.
    // fsharplint:disable-next-line CyclomaticComplexity
    let ofPayload kind payload =
        match kind, payload with
        | CaseActKind.Open, ActPayload.Registration value -> CaseAct.Open value
        | CaseActKind.AmendRegistration, ActPayload.Registration value ->
            CaseAct.AmendRegistration value
        | CaseActKind.Correct, ActPayload.Correction value -> CaseAct.Correct value
        | CaseActKind.Decide, ActPayload.Decision value -> CaseAct.Decide value
        | CaseActKind.RecordPayment, ActPayload.Payment value -> CaseAct.RecordPayment value
        | CaseActKind.Void, ActPayload.Reason value -> CaseAct.Void value
        | CaseActKind.Reinstate, ActPayload.Reason value -> CaseAct.Reinstate value
        | CaseActKind.WithdrawDecision, ActPayload.Empty -> CaseAct.WithdrawDecision
        | CaseActKind.ClearPayment, ActPayload.Empty -> CaseAct.ClearPayment
        | CaseActKind.Close, ActPayload.Empty -> CaseAct.Close
        | CaseActKind.Reopen, ActPayload.Empty -> CaseAct.Reopen
        | CaseActKind.Erase, ActPayload.Empty -> CaseAct.Erase
        | _ -> invalidArg (nameof payload) "The payload must match the act's declared shape."

    let registration (value: Registration) : RegistrationInput =
        {
            IncidentDate = CalendarDate.format value.IncidentDate
            IncidentNotificationDate = CalendarDate.format value.IncidentNotificationDate
            IncidentCountry = CountryCode.format value.IncidentCountry
            ClaimantName = PartyName.format value.ClaimantName
            InsurerName = PartyName.format value.InsurerName
            ClaimedAmount = Money.format value.ClaimedAmount
            ClaimedCurrency = Money.currency value.ClaimedAmount
        }

    let decision (value: Decision) : DecisionInput =
        {
            PaymentDecisionDate = CalendarDate.format value.PaymentDecisionDate
            PayableAmount = Money.format value.PayableAmount
            PayableCurrency = Money.currency value.PayableAmount
        }

    let private payment (value: Payment) : PaymentInput =
        {
            PaymentDate = CalendarDate.format value.PaymentDate
        }

    let authored (act: ValidCaseAct) : AuthoredCaseAct =
        map registration decision payment Reason.format act

type UnvalidatedCaseRequest =
    {
        OperationId: Guid
        CaseReference: string
        ExpectedRevision: int64
        Act: AuthoredCaseAct
    }

/// Unvalidated subject facts; Application admits the canonical principal key.
type Principal =
    {
        Kind: string
        Issuer: string
        Subject: string
    }

/// Caller-authored scope; accepted grants resolve case references to case IDs.
[<RequireQualifiedAccess>]
type GrantTarget =
    | Installation
    | CaseReference of string

type GrantChange = { Role: string; Scope: GrantTarget }

[<RequireQualifiedAccess>]
type AuthorityAct<'grant> =
    | Register
    | Grant of 'grant
    | Revoke of 'grant
    | Enable
    | Disable
    | ProvisionOwner

type AuthoredAuthorityAct = AuthorityAct<GrantChange>

type AuthorityRequest =
    {
        OperationId: Guid
        Target: Principal
        Act: AuthoredAuthorityAct
    }

[<RequireQualifiedAccess>]
type AuthorityShape =
    | TargetOnly
    | GrantValues

module AuthorityAct =
    let kind =
        function
        | AuthorityAct.Register -> AuthorityKind.Register
        | AuthorityAct.Grant _ -> AuthorityKind.Grant
        | AuthorityAct.Revoke _ -> AuthorityKind.Revoke
        | AuthorityAct.Enable -> AuthorityKind.Enable
        | AuthorityAct.Disable -> AuthorityKind.Disable
        | AuthorityAct.ProvisionOwner -> AuthorityKind.ProvisionOwner

    let shape =
        function
        | AuthorityKind.Grant
        | AuthorityKind.Revoke -> AuthorityShape.GrantValues
        | AuthorityKind.Register
        | AuthorityKind.Enable
        | AuthorityKind.Disable
        | AuthorityKind.ProvisionOwner -> AuthorityShape.TargetOnly

    let traverse grant =
        function
        | AuthorityAct.Grant value -> grant value |> Result.map AuthorityAct.Grant
        | AuthorityAct.Revoke value -> grant value |> Result.map AuthorityAct.Revoke
        | AuthorityAct.Register -> Ok AuthorityAct.Register
        | AuthorityAct.Enable -> Ok AuthorityAct.Enable
        | AuthorityAct.Disable -> Ok AuthorityAct.Disable
        | AuthorityAct.ProvisionOwner -> Ok AuthorityAct.ProvisionOwner

    let map grant act =
        traverse (grant >> Ok) act
        |> Result.defaultWith (fun () -> invalidOp "A total authority mapping cannot fail.")

    let payload =
        function
        | AuthorityAct.Grant grant
        | AuthorityAct.Revoke grant -> Some grant
        | AuthorityAct.Register
        | AuthorityAct.Enable
        | AuthorityAct.Disable
        | AuthorityAct.ProvisionOwner -> None

    let ofPayload kind grant =
        match kind, grant with
        | AuthorityKind.Grant, Some grant -> AuthorityAct.Grant grant
        | AuthorityKind.Revoke, Some grant -> AuthorityAct.Revoke grant
        | AuthorityKind.Register, None -> AuthorityAct.Register
        | AuthorityKind.Enable, None -> AuthorityAct.Enable
        | AuthorityKind.Disable, None -> AuthorityAct.Disable
        | AuthorityKind.ProvisionOwner, None -> AuthorityAct.ProvisionOwner
        | _ -> invalidArg (nameof grant) "The grant must match the authority act shape."

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module RecordGroup =
    let vocabulary = Vocabulary.ofUnion<RecordGroup> "group" TokenCasing.CamelCase None

    let token = Vocabulary.token vocabulary

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CorrectionGroupAction =
    let vocabulary =
        Vocabulary.ofUnion<CorrectionGroupAction> "token" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

    let forGroup =
        function
        | RecordGroup.Registration -> [ CorrectionGroupAction.Keep; CorrectionGroupAction.Replace ]
        | RecordGroup.Decision
        | RecordGroup.Payment -> all

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CaseStatus =
    let vocabulary =
        Vocabulary.ofUnion<CaseStatus> "token" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CaseActKind =
    let isStewardship =
        function
        | CaseActKind.Void
        | CaseActKind.Reinstate
        | CaseActKind.Erase -> true
        | CaseActKind.Open
        | CaseActKind.AmendRegistration
        | CaseActKind.Correct
        | CaseActKind.Decide
        | CaseActKind.WithdrawDecision
        | CaseActKind.RecordPayment
        | CaseActKind.ClearPayment
        | CaseActKind.Close
        | CaseActKind.Reopen -> false

    let vocabulary =
        Vocabulary.ofUnion<CaseActKind> "act" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module AuthorityKind =
    let vocabulary =
        Vocabulary.ofUnion<AuthorityKind> "act" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ActSubject =
    let vocabulary =
        Vocabulary.ofUnion<ActSubject> "subject" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ActKind =
    let subject =
        function
        | ActKind.Case _ -> ActSubject.Case
        | ActKind.Authority _ -> ActSubject.Authority

    let vocabulary =
        [
            yield!
                Vocabulary.entries CaseActKind.vocabulary
                |> List.map (fun (value, token) -> ActKind.Case value, token)
            yield!
                Vocabulary.entries AuthorityKind.vocabulary
                |> List.map (fun (value, token) -> ActKind.Authority value, token)
        ]
        |> Vocabulary.ofEntries "act"

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ActorEndpoint =
    let vocabulary =
        Vocabulary.define
            "endpoint"
            [
                (ActorEndpoint.Operation OperationEndpoint.Preview)
                (ActorEndpoint.Operation OperationEndpoint.Execute)
                (ActorEndpoint.Query QueryEndpoint.CaseGet)
                (ActorEndpoint.Query QueryEndpoint.CaseList)
                (ActorEndpoint.Query QueryEndpoint.CaseHistory)
                (ActorEndpoint.Query QueryEndpoint.Observe)
                (ActorEndpoint.Query QueryEndpoint.Resolve)
                (ActorEndpoint.Query QueryEndpoint.AuthorityList)
                (ActorEndpoint.Query QueryEndpoint.AuthorityGrants)
                (ActorEndpoint.Query QueryEndpoint.Context)
            ]
            (function
             | (ActorEndpoint.Operation OperationEndpoint.Preview) -> "operation.preview"
             | (ActorEndpoint.Operation OperationEndpoint.Execute) -> "operation.execute"
             | (ActorEndpoint.Query QueryEndpoint.CaseGet) -> "case.get"
             | (ActorEndpoint.Query QueryEndpoint.CaseList) -> "case.list"
             | (ActorEndpoint.Query QueryEndpoint.CaseHistory) -> "case.history"
             | (ActorEndpoint.Query QueryEndpoint.Observe) -> "operation.observe"
             | (ActorEndpoint.Query QueryEndpoint.Resolve) -> "operation.resolve"
             | (ActorEndpoint.Query QueryEndpoint.AuthorityList) -> "authority.list"
             | (ActorEndpoint.Query QueryEndpoint.AuthorityGrants) -> "authority.grants"
             | (ActorEndpoint.Query QueryEndpoint.Context) -> "context")

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary


/// Accepted facts share the authored group types; a decision is complete or absent.
type CaseFields =
    {
        CaseReference: CaseReference
        Status: CaseStatus
        Registration: Registration
        Decision: Decision option
        Payment: Payment option
    }

[<RequireQualifiedAccess>]
type Lifecycle =
    | Active
    | Voided
    | Erased

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Lifecycle =
    let vocabulary =
        Vocabulary.ofUnion<Lifecycle> "token" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

/// Lifecycle is independent of the payload carried by a full view or summary.
[<RequireQualifiedAccess>]
type CaseState<'fields> =
    | Active of 'fields
    | Voided of 'fields
    | Erased

module CaseState =
    let map mapping =
        function
        | CaseState.Active fields -> CaseState.Active(mapping fields)
        | CaseState.Voided fields -> CaseState.Voided(mapping fields)
        | CaseState.Erased -> CaseState.Erased

    let fields =
        function
        | CaseState.Active fields
        | CaseState.Voided fields -> Some fields
        | CaseState.Erased -> None

    let lifecycle =
        function
        | CaseState.Active _ -> Lifecycle.Active
        | CaseState.Voided _ -> Lifecycle.Voided
        | CaseState.Erased -> Lifecycle.Erased

    let token state = lifecycle state |> Lifecycle.token

/// A disclosure cannot carry an erased state.
[<RequireQualifiedAccess>]
type LiveCase =
    | Active of CaseFields
    | Voided of CaseFields

module LiveCase =
    let toState =
        function
        | LiveCase.Active fields -> CaseState.Active fields
        | LiveCase.Voided fields -> CaseState.Voided fields

    let ofState =
        function
        | CaseState.Active fields -> Some(LiveCase.Active fields)
        | CaseState.Voided fields -> Some(LiveCase.Voided fields)
        | CaseState.Erased -> None

type CaseSnapshot =
    {
        Reference: CaseReference
        Revision: int64
        State: CaseState<CaseFields>
    }

[<RequireQualifiedAccess>]
type FieldValueKind =
    | CalendarDate
    | Country
    | PartyName
    | Amount
    | Currency
    | Reference
    | Status

type FieldDeclaration =
    {
        Field: BusinessField
        Key: string
        Kind: FieldValueKind
        Group: RecordGroup option
        AllowsAbsence: bool
        RetainedOnErasure: bool
    }

[<RequireQualifiedAccess>]
type HistoryDetail =
    | Summary
    | Full

type PageRequest =
    {
        AfterCursor: string option
        Limit: int
    }

type HistoryRequest =
    {
        CaseReference: string
        AfterCursor: string option
        Limit: int
        Detail: HistoryDetail
    }

type GrantsRequest =
    {
        ActorId: Guid
        AfterCursor: string option
        Limit: int
    }

type CaseGetRequest = { CaseReference: string }

type ObserveRequest = { OperationId: Guid }

type ResolveRequest = { OperationId: Guid }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module HistoryDetail =
    let vocabulary =
        Vocabulary.ofUnion<HistoryDetail> "historyDetail" TokenCasing.ScreamingSnake None

[<RequireQualifiedAccess>]
type ReadInput =
    | CaseOperand
    | OperationOperand
    | ActorOperand
    | Page
    | Detail

type ReadDeclaration =
    {
        Endpoint: QueryEndpoint
        Inputs: ReadInput list
    }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module BusinessField =
    let vocabulary =
        Vocabulary.ofUnion<BusinessField> "field" TokenCasing.CamelCase None

    let key = Vocabulary.token vocabulary

    // Scalar meaning is an exhaustive domain declaration; compiler coverage protects every field.
    // fsharplint:disable-next-line CyclomaticComplexity
    let valueKind =
        function
        | BusinessField.IncidentDate
        | BusinessField.IncidentNotificationDate
        | BusinessField.PaymentDecisionDate
        | BusinessField.PaymentDate -> FieldValueKind.CalendarDate
        | BusinessField.IncidentCountry -> FieldValueKind.Country
        | BusinessField.ClaimantName
        | BusinessField.InsurerName -> FieldValueKind.PartyName
        | BusinessField.ClaimedAmount
        | BusinessField.PayableAmount -> FieldValueKind.Amount
        | BusinessField.ClaimedCurrency
        | BusinessField.PayableCurrency -> FieldValueKind.Currency
        | BusinessField.CaseReference -> FieldValueKind.Reference
        | BusinessField.Status -> FieldValueKind.Status

    // This exhaustive field projection formats data; it selects no business policy.
    // fsharplint:disable-next-line CyclomaticComplexity
    let value (fields: CaseFields) =
        function
        | BusinessField.CaseReference -> Some(CaseReference.format fields.CaseReference)
        | BusinessField.Status -> Some(CaseStatus.token fields.Status)
        | BusinessField.IncidentDate -> Some(CalendarDate.format fields.Registration.IncidentDate)
        | BusinessField.IncidentNotificationDate ->
            Some(CalendarDate.format fields.Registration.IncidentNotificationDate)
        | BusinessField.IncidentCountry ->
            Some(CountryCode.format fields.Registration.IncidentCountry)
        | BusinessField.ClaimantName -> Some(PartyName.format fields.Registration.ClaimantName)
        | BusinessField.InsurerName -> Some(PartyName.format fields.Registration.InsurerName)
        | BusinessField.ClaimedAmount -> Some(Money.format fields.Registration.ClaimedAmount)
        | BusinessField.ClaimedCurrency -> Some(Money.currency fields.Registration.ClaimedAmount)
        | BusinessField.PaymentDecisionDate ->
            fields.Decision
            |> Option.map (fun value -> CalendarDate.format value.PaymentDecisionDate)
        | BusinessField.PayableAmount ->
            fields.Decision |> Option.map (fun value -> Money.format value.PayableAmount)
        | BusinessField.PayableCurrency ->
            fields.Decision |> Option.map (fun value -> Money.currency value.PayableAmount)
        | BusinessField.PaymentDate ->
            fields.Payment
            |> Option.map (fun value -> CalendarDate.format value.PaymentDate)

    let values fields =
        Vocabulary.all vocabulary |> List.map (fun field -> field, value fields field)

/// Domain declarations carry structure; each boundary chooses its own names and codecs.
module Record =
    let fieldsOf =
        function
        | RecordGroup.Registration ->
            [
                BusinessField.IncidentDate
                BusinessField.IncidentNotificationDate
                BusinessField.IncidentCountry
                BusinessField.ClaimantName
                BusinessField.InsurerName
                BusinessField.ClaimedAmount
                BusinessField.ClaimedCurrency
            ]
        | RecordGroup.Decision ->
            [
                BusinessField.PaymentDecisionDate
                BusinessField.PayableAmount
                BusinessField.PayableCurrency
            ]
        | RecordGroup.Payment -> [ BusinessField.PaymentDate ]

    let private declared group allowsAbsence field =
        {
            Field = field
            Key = BusinessField.key field
            Kind = BusinessField.valueKind field
            Group = group
            AllowsAbsence = allowsAbsence
            RetainedOnErasure = field = BusinessField.CaseReference
        }

    let fields =
        (fieldsOf RecordGroup.Registration
         |> List.map (declared (Some RecordGroup.Registration) false))
        @ [ declared None false BusinessField.CaseReference ]
        @ (fieldsOf RecordGroup.Decision
           |> List.map (declared (Some RecordGroup.Decision) true))
        @ (fieldsOf RecordGroup.Payment
           |> List.map (declared (Some RecordGroup.Payment) true))
        @ [ declared None false BusinessField.Status ]

    let reads =
        [
            {
                Endpoint = QueryEndpoint.CaseGet
                Inputs = [ ReadInput.CaseOperand ]
            }
            {
                Endpoint = QueryEndpoint.CaseList
                Inputs = [ ReadInput.Page ]
            }
            {
                Endpoint = QueryEndpoint.CaseHistory
                Inputs = [ ReadInput.CaseOperand; ReadInput.Detail; ReadInput.Page ]
            }
            {
                Endpoint = QueryEndpoint.Observe
                Inputs = [ ReadInput.OperationOperand ]
            }
            {
                Endpoint = QueryEndpoint.Resolve
                Inputs = [ ReadInput.OperationOperand ]
            }
            {
                Endpoint = QueryEndpoint.AuthorityList
                Inputs = [ ReadInput.Page ]
            }
            {
                Endpoint = QueryEndpoint.AuthorityGrants
                Inputs = [ ReadInput.ActorOperand; ReadInput.Page ]
            }
            {
                Endpoint = QueryEndpoint.Context
                Inputs = []
            }
        ]
