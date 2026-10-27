namespace ClaimCore.Domain

open System

[<RequireQualifiedAccess>]
type CorrectionGroupAction =
    | Keep
    | Replace
    | Clear

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

[<RequireQualifiedAccess>]
type Amendment<'value> =
    | Keep
    | Replace of 'value

[<RequireQualifiedAccess>]
type Change<'value> =
    | Keep
    | Replace of 'value
    | Clear

type Correction<'registration, 'decision, 'payment> =
    {
        Registration: Amendment<'registration>
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

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type CaseActSlot<'registration, 'decision, 'payment, 'reason> =
    | Bare of CaseAct<'registration, 'decision, 'payment, 'reason>
    | Registration of
        inject: ('registration -> CaseAct<'registration, 'decision, 'payment, 'reason>) *
        project: (CaseAct<'registration, 'decision, 'payment, 'reason> -> 'registration option)
    | Decision of
        inject: ('decision -> CaseAct<'registration, 'decision, 'payment, 'reason>) *
        project: (CaseAct<'registration, 'decision, 'payment, 'reason> -> 'decision option)
    | Payment of
        inject: ('payment -> CaseAct<'registration, 'decision, 'payment, 'reason>) *
        project: (CaseAct<'registration, 'decision, 'payment, 'reason> -> 'payment option)
    | Reason of
        inject: ('reason -> CaseAct<'registration, 'decision, 'payment, 'reason>) *
        project: (CaseAct<'registration, 'decision, 'payment, 'reason> -> 'reason option)
    | Correction of
        inject:
            (Correction<'registration, 'decision, 'payment>
                -> CaseAct<'registration, 'decision, 'payment, 'reason>) *
        project:
            (CaseAct<'registration, 'decision, 'payment, 'reason>
                -> Correction<'registration, 'decision, 'payment> option)

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

    let private amend transform =
        function
        | Amendment.Keep -> Ok Amendment.Keep
        | Amendment.Replace value -> transform value |> Result.map Amendment.Replace

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
            amend registration value.Registration
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

    let private projectOpen =
        function
        | CaseAct.Open value -> Some value
        | _ -> None

    let private projectAmendRegistration =
        function
        | CaseAct.AmendRegistration value -> Some value
        | _ -> None

    let private projectCorrect =
        function
        | CaseAct.Correct value -> Some value
        | _ -> None

    let private projectDecide =
        function
        | CaseAct.Decide value -> Some value
        | _ -> None

    let private projectRecordPayment =
        function
        | CaseAct.RecordPayment value -> Some value
        | _ -> None

    let private projectVoid =
        function
        | CaseAct.Void value -> Some value
        | _ -> None

    let private projectReinstate =
        function
        | CaseAct.Reinstate value -> Some value
        | _ -> None

    let slot<'registration, 'decision, 'payment, 'reason>
        kind
        : CaseActSlot<'registration, 'decision, 'payment, 'reason> =
        match kind with
        | CaseActKind.Open -> CaseActSlot.Registration(CaseAct.Open, projectOpen)
        | CaseActKind.AmendRegistration ->
            CaseActSlot.Registration(CaseAct.AmendRegistration, projectAmendRegistration)
        | CaseActKind.Correct -> CaseActSlot.Correction(CaseAct.Correct, projectCorrect)
        | CaseActKind.Decide -> CaseActSlot.Decision(CaseAct.Decide, projectDecide)
        | CaseActKind.WithdrawDecision -> CaseActSlot.Bare CaseAct.WithdrawDecision
        | CaseActKind.RecordPayment ->
            CaseActSlot.Payment(CaseAct.RecordPayment, projectRecordPayment)
        | CaseActKind.ClearPayment -> CaseActSlot.Bare CaseAct.ClearPayment
        | CaseActKind.Close -> CaseActSlot.Bare CaseAct.Close
        | CaseActKind.Reopen -> CaseActSlot.Bare CaseAct.Reopen
        | CaseActKind.Void -> CaseActSlot.Reason(CaseAct.Void, projectVoid)
        | CaseActKind.Reinstate -> CaseActSlot.Reason(CaseAct.Reinstate, projectReinstate)
        | CaseActKind.Erase -> CaseActSlot.Bare CaseAct.Erase

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

module Amendment =
    let private action =
        function
        | Amendment.Keep -> CorrectionGroupAction.Keep
        | Amendment.Replace _ -> CorrectionGroupAction.Replace

    let actions =
        Microsoft.FSharp.Reflection.FSharpType.GetUnionCases typeof<Amendment<unit>>
        |> Array.map (fun case ->
            let payloads = Array.create (case.GetFields().Length) (box ())

            (Microsoft.FSharp.Reflection.FSharpValue.MakeUnion(case, payloads) |> nonNull)
            :?> Amendment<unit>
            |> action)
        |> Array.toList

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
