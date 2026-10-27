namespace ClaimCore.Domain

open System

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

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CorrectionGroupAction =
    let vocabulary =
        Vocabulary.ofUnion<CorrectionGroupAction> "correction" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

    let forGroup =
        function
        | RecordGroup.Registration -> [ CorrectionGroupAction.Keep; CorrectionGroupAction.Replace ]
        | RecordGroup.Decision
        | RecordGroup.Payment -> all

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
