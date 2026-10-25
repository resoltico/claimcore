namespace ClaimCore.Contract

[<RequireQualifiedAccess>]
type Violation =
    | TextRequired
    | MalformedUnicode
    | SurroundingWhitespace
    | TooLong of maximumCharacters: int
    | ControlCharacters
    | FormattingCharacter
    | ReferenceFormat
    | CountryFormat
    | PrincipalFormat
    | CalendarDateRequired
    | ChronologicalOrder
    | FutureDate
    | DecimalFormat of integerDigits: int * fractionDigits: int
    | CurrencyFormat
    | OperationIdRequired
    | ExpectedRevisionRange
    | PageLimitRange of maximumPageSize: int
    | PaymentClearRequired
    | PaymentAcknowledgementRequired

[<RequireQualifiedAccess>]
type Refusal =
    | InvalidInput of InputTarget * Violation
    | RevisionConflict of actualRevision: int64
    | CaseAlreadyExists
    | CaseErased
    | CaseVoided
    | AlreadyVoided
    | NotVoided
    | CaseClosed
    | AlreadyClosed
    | AlreadyOpened
    | AmendmentRequiresUndecided
    | CorrectionNoChanges
    | CorrectionRequiresExistingValue
    | DecisionRequired
    | PaymentAlreadyRecorded
    | PaymentNotRecorded
    | DecisionAlreadyPaid
    | ZeroDecisionCannotBePaid
    | RevisionExhausted
    | LedgerExhausted
    | OperationResolved
    | IdempotencyConflict
    | ResourceUnavailable
    | ActNotPermitted
    | LastOwner
    | ActorAlreadyRegistered
    | ActorUnknown
    | HumanPrincipalRequired
    | InvalidCursor

type RecommendedAction =
    | CorrectInput
    | ReadCurrent
    | RetrySafe
    | ReplayExact
    | Reauthenticate
    | StopAndInvestigate
    | NoneRequired

type RefusalRow =
    {
        Code: string
        Action: RecommendedAction
    }

module Refusals =
    let private policy code action = { Code = code; Action = action }

    let private textCode =
        function
        | Violation.TextRequired -> "INPUT_TEXT_REQUIRED"
        | Violation.MalformedUnicode -> "INPUT_MALFORMED_UNICODE"
        | Violation.SurroundingWhitespace -> "INPUT_SURROUNDING_WHITESPACE"
        | Violation.TooLong _ -> "INPUT_TEXT_TOO_LONG"
        | Violation.ControlCharacters -> "INPUT_CONTROL_CHARACTERS"
        | Violation.FormattingCharacter -> "TEXT_FORMATTING_CHARACTER"
        | Violation.ReferenceFormat -> "INPUT_CASE_REFERENCE_FORMAT"
        | Violation.CountryFormat -> "INPUT_COUNTRY_FORMAT"
        | Violation.PrincipalFormat -> "INPUT_PRINCIPAL_FORMAT"
        | _ -> invalidArg "violation" "Expected this scalar family."

    let private scalarCode =
        function
        | Violation.CalendarDateRequired -> "INPUT_CALENDAR_DATE_REQUIRED"
        | Violation.ChronologicalOrder -> "INPUT_DATE_ORDER"
        | Violation.FutureDate -> "INPUT_FUTURE_DATE"
        | Violation.DecimalFormat _ -> "INPUT_DECIMAL_FORMAT"
        | Violation.CurrencyFormat -> "INPUT_CURRENCY_FORMAT"
        | _ -> invalidArg "violation" "Expected this scalar family."

    let private violationCode =
        function
        | Violation.OperationIdRequired -> "COMMAND_OPERATION_ID_REQUIRED"
        | Violation.ExpectedRevisionRange -> "COMMAND_EXPECTED_REVISION_RANGE"
        | Violation.PageLimitRange _ -> "QUERY_PAGE_LIMIT_RANGE"
        | Violation.PaymentClearRequired -> "CORRECTION_PAYMENT_CLEAR_REQUIRED"
        | Violation.PaymentAcknowledgementRequired -> "CORRECTION_PAYMENT_ACKNOWLEDGEMENT_REQUIRED"
        | (Violation.TextRequired | Violation.MalformedUnicode | Violation.SurroundingWhitespace | Violation.TooLong _ | Violation.ControlCharacters | Violation.FormattingCharacter | Violation.ReferenceFormat | Violation.CountryFormat | Violation.PrincipalFormat) as value ->
            textCode value
        | (Violation.CalendarDateRequired | Violation.ChronologicalOrder | Violation.FutureDate | Violation.DecimalFormat _ | Violation.CurrencyFormat) as value ->
            scalarCode value

    let private state =
        function
        | Refusal.CaseAlreadyExists -> policy "CASE_ALREADY_EXISTS" RecommendedAction.NoneRequired
        | Refusal.CaseErased -> policy "CASE_ERASED" RecommendedAction.NoneRequired
        | Refusal.CaseVoided -> policy "CASE_VOIDED" RecommendedAction.NoneRequired
        | Refusal.AlreadyVoided -> policy "ALREADY_VOIDED" RecommendedAction.NoneRequired
        | Refusal.NotVoided -> policy "NOT_VOIDED" RecommendedAction.NoneRequired
        | Refusal.CaseClosed -> policy "CASE_CLOSED" RecommendedAction.NoneRequired
        | Refusal.AlreadyClosed -> policy "CASE_ALREADY_CLOSED" RecommendedAction.NoneRequired
        | Refusal.AlreadyOpened -> policy "CASE_ALREADY_OPENED" RecommendedAction.NoneRequired
        | _ -> invalidArg "refusal" "Expected this refusal family."

    let private progress =
        function
        | Refusal.AmendmentRequiresUndecided ->
            policy "CASE_AMENDMENT_REQUIRES_UNDECIDED" RecommendedAction.NoneRequired
        | Refusal.CorrectionNoChanges ->
            policy "CORRECTION_NO_CHANGES" RecommendedAction.NoneRequired
        | Refusal.CorrectionRequiresExistingValue ->
            policy "CORRECTION_EXISTING_VALUE_REQUIRED" RecommendedAction.NoneRequired
        | Refusal.DecisionRequired -> policy "CASE_DECISION_REQUIRED" RecommendedAction.NoneRequired
        | Refusal.PaymentAlreadyRecorded ->
            policy "CASE_PAYMENT_ALREADY_RECORDED" RecommendedAction.NoneRequired
        | Refusal.PaymentNotRecorded ->
            policy "CASE_PAYMENT_NOT_RECORDED" RecommendedAction.NoneRequired
        | Refusal.DecisionAlreadyPaid ->
            policy "CASE_DECISION_ALREADY_PAID" RecommendedAction.NoneRequired
        | Refusal.ZeroDecisionCannotBePaid ->
            policy "CASE_ZERO_DECISION_CANNOT_BE_PAID" RecommendedAction.NoneRequired
        | _ -> invalidArg "refusal" "Expected this refusal family."

    let private authority =
        function
        | Refusal.ResourceUnavailable ->
            policy "ACCESS_RESOURCE_UNAVAILABLE" RecommendedAction.NoneRequired
        | Refusal.ActNotPermitted -> policy "ACT_NOT_PERMITTED" RecommendedAction.NoneRequired
        | Refusal.LastOwner -> policy "LAST_OWNER" RecommendedAction.NoneRequired
        | Refusal.ActorAlreadyRegistered ->
            policy "ACTOR_ALREADY_REGISTERED" RecommendedAction.NoneRequired
        | Refusal.ActorUnknown -> policy "ACTOR_UNKNOWN" RecommendedAction.NoneRequired
        | Refusal.HumanPrincipalRequired ->
            policy "HUMAN_PRINCIPAL_REQUIRED" RecommendedAction.NoneRequired
        | _ -> invalidArg "refusal" "Expected this refusal family."

    let row =
        function
        | Refusal.InvalidInput(_, violation) ->
            policy (violationCode violation) RecommendedAction.CorrectInput
        | Refusal.RevisionConflict _ ->
            policy "CASE_REVISION_CONFLICT" RecommendedAction.ReadCurrent
        | Refusal.RevisionExhausted -> policy "REVISION_EXHAUSTED" RecommendedAction.NoneRequired
        | Refusal.LedgerExhausted ->
            policy "LEDGER_SEQUENCE_EXHAUSTED" RecommendedAction.StopAndInvestigate
        | Refusal.OperationResolved -> policy "OPERATION_RESOLVED" RecommendedAction.ReadCurrent
        | Refusal.IdempotencyConflict ->
            policy "OPERATION_CONTENT_CONFLICT" RecommendedAction.StopAndInvestigate
        | Refusal.InvalidCursor -> policy "QUERY_CURSOR_INVALID" RecommendedAction.ReadCurrent
        | (Refusal.CaseAlreadyExists | Refusal.CaseErased | Refusal.CaseVoided | Refusal.AlreadyVoided | Refusal.NotVoided | Refusal.CaseClosed | Refusal.AlreadyClosed | Refusal.AlreadyOpened) as value ->
            state value
        | (Refusal.AmendmentRequiresUndecided | Refusal.CorrectionNoChanges | Refusal.CorrectionRequiresExistingValue | Refusal.DecisionRequired | Refusal.PaymentAlreadyRecorded | Refusal.PaymentNotRecorded | Refusal.DecisionAlreadyPaid | Refusal.ZeroDecisionCannotBePaid) as value ->
            progress value
        | (Refusal.ResourceUnavailable | Refusal.ActNotPermitted | Refusal.LastOwner | Refusal.ActorAlreadyRegistered | Refusal.ActorUnknown | Refusal.HumanPrincipalRequired) as value ->
            authority value

    let field =
        function
        | Refusal.InvalidInput(target, _) -> Some(InputTargets.token target)
        | Refusal.InvalidCursor -> Some "cursor"
        | Refusal.OperationResolved -> Some "operationId"
        | _ -> None

    let actualRevision =
        function
        | Refusal.RevisionConflict revision -> Some revision
        | _ -> None

    let parameters =
        function
        | Refusal.InvalidInput(_, Violation.TooLong maximum) -> [ "maximumCharacters", maximum ]
        | Refusal.InvalidInput(_, Violation.DecimalFormat(integer, fraction)) ->
            [ "maximumIntegerDigits", integer; "maximumFractionalDigits", fraction ]
        | Refusal.InvalidInput(_, Violation.PageLimitRange maximum) ->
            [ "maximumPageSize", maximum ]
        | _ -> []

    let all =
        [
            Refusal.InvalidInput(
                InputTarget.Field BusinessField.ClaimantName,
                Violation.TextRequired
            )
            Refusal.InvalidInput(
                InputTarget.Field BusinessField.ClaimantName,
                Violation.MalformedUnicode
            )
            Refusal.InvalidInput(
                InputTarget.Field BusinessField.ClaimantName,
                Violation.SurroundingWhitespace
            )
            Refusal.InvalidInput(
                InputTarget.Field BusinessField.ClaimantName,
                Violation.TooLong 200
            )
            Refusal.InvalidInput(
                InputTarget.Field BusinessField.ClaimantName,
                Violation.ControlCharacters
            )
            Refusal.InvalidInput(
                InputTarget.Field BusinessField.ClaimantName,
                Violation.FormattingCharacter
            )
            Refusal.InvalidInput(
                InputTarget.Field BusinessField.ClaimantName,
                Violation.ReferenceFormat
            )
            Refusal.InvalidInput(
                InputTarget.Field BusinessField.ClaimantName,
                Violation.CountryFormat
            )
            Refusal.InvalidInput(InputTarget.PrincipalIssuer, Violation.PrincipalFormat)
            Refusal.InvalidInput(
                InputTarget.Field BusinessField.ClaimantName,
                Violation.CalendarDateRequired
            )
            Refusal.InvalidInput(
                InputTarget.Field BusinessField.ClaimantName,
                Violation.ChronologicalOrder
            )
            Refusal.InvalidInput(InputTarget.Field BusinessField.ClaimantName, Violation.FutureDate)
            Refusal.InvalidInput(
                InputTarget.Field BusinessField.ClaimantName,
                Violation.DecimalFormat(18, 4)
            )
            Refusal.InvalidInput(
                InputTarget.Field BusinessField.ClaimantName,
                Violation.CurrencyFormat
            )
            Refusal.InvalidInput(InputTarget.OperationId, Violation.OperationIdRequired)
            Refusal.InvalidInput(InputTarget.ExpectedRevision, Violation.ExpectedRevisionRange)
            Refusal.InvalidInput(InputTarget.Limit, Violation.PageLimitRange 50)
            Refusal.InvalidInput(InputTarget.PaymentGroup, Violation.PaymentClearRequired)
            Refusal.InvalidInput(InputTarget.PaymentGroup, Violation.PaymentAcknowledgementRequired)
            Refusal.RevisionConflict 1L
            Refusal.CaseAlreadyExists
            Refusal.CaseErased
            Refusal.CaseVoided
            Refusal.AlreadyVoided
            Refusal.NotVoided
            Refusal.CaseClosed
            Refusal.AlreadyClosed
            Refusal.AlreadyOpened
            Refusal.AmendmentRequiresUndecided
            Refusal.CorrectionNoChanges
            Refusal.CorrectionRequiresExistingValue
            Refusal.DecisionRequired
            Refusal.PaymentAlreadyRecorded
            Refusal.PaymentNotRecorded
            Refusal.DecisionAlreadyPaid
            Refusal.ZeroDecisionCannotBePaid
            Refusal.RevisionExhausted
            Refusal.LedgerExhausted
            Refusal.OperationResolved
            Refusal.IdempotencyConflict
            Refusal.ResourceUnavailable
            Refusal.ActNotPermitted
            Refusal.LastOwner
            Refusal.ActorAlreadyRegistered
            Refusal.ActorUnknown
            Refusal.HumanPrincipalRequired
            Refusal.InvalidCursor
        ]
        |> List.map (fun value -> value, row value)

    let codes = all |> List.map (fun (_, row) -> row.Code) |> List.distinct

    let fromDiagnostic code target revision limits =
        let exemplar, _ = all |> List.find (fun (_, row) -> row.Code = code)

        match exemplar with
        | Refusal.InvalidInput(_, Violation.TooLong _) ->
            Refusal.InvalidInput(
                Option.get target,
                Violation.TooLong(Map.find "maximumCharacters" limits)
            )
        | Refusal.InvalidInput(_, Violation.DecimalFormat _) ->
            Refusal.InvalidInput(
                Option.get target,
                Violation.DecimalFormat(
                    Map.find "maximumIntegerDigits" limits,
                    Map.find "maximumFractionalDigits" limits
                )
            )
        | Refusal.InvalidInput(_, Violation.PageLimitRange _) ->
            Refusal.InvalidInput(
                Option.get target,
                Violation.PageLimitRange(Map.find "maximumPageSize" limits)
            )
        | Refusal.InvalidInput(_, violation) -> Refusal.InvalidInput(Option.get target, violation)
        | Refusal.RevisionConflict _ -> Refusal.RevisionConflict(Option.get revision)
        | value -> value

    let definitions =
        all
        |> List.map (fun (reason, row) ->
            {
                Id = row.Code
                Parameters =
                    parameters reason
                    |> List.map (fun (name, _) ->
                        DiagnosticArguments.tryParse name
                        |> Option.get
                        |> DiagnosticArguments.definition)
            })
        |> List.distinctBy _.Id

module RecommendedActions =
    let all =
        [
            RecommendedAction.CorrectInput, "CORRECT_INPUT"
            RecommendedAction.ReadCurrent, "READ_CURRENT"
            RecommendedAction.RetrySafe, "RETRY_SAFE"
            RecommendedAction.ReplayExact, "REPLAY_EXACT"
            RecommendedAction.Reauthenticate, "REAUTHENTICATE"
            RecommendedAction.StopAndInvestigate, "STOP_AND_INVESTIGATE"
            RecommendedAction.NoneRequired, "NONE_REQUIRED"
        ]

    let token action =
        all |> List.find (fst >> (=) action) |> snd

type Refusal with
    member this.Field = Refusals.field this
    member this.ActualRevision = Refusals.actualRevision this
    member this.Action = (Refusals.row this).Action
