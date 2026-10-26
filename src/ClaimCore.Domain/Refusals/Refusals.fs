namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type ViolationCode =
    | TextRequired
    | MalformedUnicode
    | SurroundingWhitespace
    | TooLong
    | ControlCharacters
    | FormattingCharacter
    | ReferenceFormat
    | CountryFormat
    | PrincipalFormat
    | CalendarDateRequired
    | ChronologicalOrder
    | FutureDate
    | DecimalFormat
    | CurrencyFormat
    | OperationIdRequired
    | ExpectedRevisionRange
    | PageLimitRange
    | PaymentClearRequired
    | PaymentAcknowledgementRequired

module ViolationCodes =
    // Flat exhaustive declaration: compiler coverage and artifact parity protect this mapping.
    // fsharplint:disable-next-line CyclomaticComplexity
    let vocabulary =
        Vocabulary.define
            "violation"
            [
                ViolationCode.TextRequired
                ViolationCode.MalformedUnicode
                ViolationCode.SurroundingWhitespace
                ViolationCode.TooLong
                ViolationCode.ControlCharacters
                ViolationCode.FormattingCharacter
                ViolationCode.ReferenceFormat
                ViolationCode.CountryFormat
                ViolationCode.PrincipalFormat
                ViolationCode.CalendarDateRequired
                ViolationCode.ChronologicalOrder
                ViolationCode.FutureDate
                ViolationCode.DecimalFormat
                ViolationCode.CurrencyFormat
                ViolationCode.OperationIdRequired
                ViolationCode.ExpectedRevisionRange
                ViolationCode.PageLimitRange
                ViolationCode.PaymentClearRequired
                ViolationCode.PaymentAcknowledgementRequired
            ]
            (function
             | ViolationCode.TextRequired -> "INPUT_TEXT_REQUIRED"
             | ViolationCode.MalformedUnicode -> "INPUT_MALFORMED_UNICODE"
             | ViolationCode.SurroundingWhitespace -> "INPUT_SURROUNDING_WHITESPACE"
             | ViolationCode.TooLong -> "INPUT_TEXT_TOO_LONG"
             | ViolationCode.ControlCharacters -> "INPUT_CONTROL_CHARACTERS"
             | ViolationCode.FormattingCharacter -> "TEXT_FORMATTING_CHARACTER"
             | ViolationCode.ReferenceFormat -> "INPUT_CASE_REFERENCE_FORMAT"
             | ViolationCode.CountryFormat -> "INPUT_COUNTRY_FORMAT"
             | ViolationCode.PrincipalFormat -> "INPUT_PRINCIPAL_FORMAT"
             | ViolationCode.CalendarDateRequired -> "INPUT_CALENDAR_DATE_REQUIRED"
             | ViolationCode.ChronologicalOrder -> "INPUT_DATE_ORDER"
             | ViolationCode.FutureDate -> "INPUT_FUTURE_DATE"
             | ViolationCode.DecimalFormat -> "INPUT_DECIMAL_FORMAT"
             | ViolationCode.CurrencyFormat -> "INPUT_CURRENCY_FORMAT"
             | ViolationCode.OperationIdRequired -> "COMMAND_OPERATION_ID_REQUIRED"
             | ViolationCode.ExpectedRevisionRange -> "COMMAND_EXPECTED_REVISION_RANGE"
             | ViolationCode.PageLimitRange -> "QUERY_PAGE_LIMIT_RANGE"
             | ViolationCode.PaymentClearRequired -> "CORRECTION_PAYMENT_CLEAR_REQUIRED"
             | ViolationCode.PaymentAcknowledgementRequired ->
                 "CORRECTION_PAYMENT_ACKNOWLEDGEMENT_REQUIRED")

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

    // Flat exhaustive declaration: compiler coverage and artifact parity protect this mapping.
    // fsharplint:disable-next-line CyclomaticComplexity
    let ofViolation =
        function
        | Violation.TextRequired -> ViolationCode.TextRequired
        | Violation.MalformedUnicode -> ViolationCode.MalformedUnicode
        | Violation.SurroundingWhitespace -> ViolationCode.SurroundingWhitespace
        | Violation.TooLong _ -> ViolationCode.TooLong
        | Violation.ControlCharacters -> ViolationCode.ControlCharacters
        | Violation.FormattingCharacter -> ViolationCode.FormattingCharacter
        | Violation.ReferenceFormat -> ViolationCode.ReferenceFormat
        | Violation.CountryFormat -> ViolationCode.CountryFormat
        | Violation.PrincipalFormat -> ViolationCode.PrincipalFormat
        | Violation.CalendarDateRequired -> ViolationCode.CalendarDateRequired
        | Violation.ChronologicalOrder -> ViolationCode.ChronologicalOrder
        | Violation.FutureDate -> ViolationCode.FutureDate
        | Violation.DecimalFormat _ -> ViolationCode.DecimalFormat
        | Violation.CurrencyFormat -> ViolationCode.CurrencyFormat
        | Violation.OperationIdRequired -> ViolationCode.OperationIdRequired
        | Violation.ExpectedRevisionRange -> ViolationCode.ExpectedRevisionRange
        | Violation.PageLimitRange _ -> ViolationCode.PageLimitRange
        | Violation.PaymentClearRequired -> ViolationCode.PaymentClearRequired
        | Violation.PaymentAcknowledgementRequired -> ViolationCode.PaymentAcknowledgementRequired

    // Flat exhaustive declaration: compiler coverage and artifact parity protect this mapping.
    // fsharplint:disable-next-line CyclomaticComplexity
    let parameters =
        function
        | ViolationCode.TooLong -> [ DiagnosticArgument.MaximumCharacters ]
        | ViolationCode.DecimalFormat ->
            [
                DiagnosticArgument.MaximumIntegerDigits
                DiagnosticArgument.MaximumFractionalDigits
            ]
        | ViolationCode.PageLimitRange -> [ DiagnosticArgument.MaximumPageSize ]
        | ViolationCode.TextRequired
        | ViolationCode.MalformedUnicode
        | ViolationCode.SurroundingWhitespace
        | ViolationCode.ControlCharacters
        | ViolationCode.FormattingCharacter
        | ViolationCode.ReferenceFormat
        | ViolationCode.CountryFormat
        | ViolationCode.PrincipalFormat
        | ViolationCode.CalendarDateRequired
        | ViolationCode.ChronologicalOrder
        | ViolationCode.FutureDate
        | ViolationCode.CurrencyFormat
        | ViolationCode.OperationIdRequired
        | ViolationCode.ExpectedRevisionRange
        | ViolationCode.PaymentClearRequired
        | ViolationCode.PaymentAcknowledgementRequired -> []

[<RequireQualifiedAccess>]
type RefusalCode =
    | InvalidInput of ViolationCode
    | RevisionConflict
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
    | OperationResolved
    | IdempotencyConflict
    | ResourceUnavailable
    | ActNotPermitted
    | LastOwner
    | ActorAlreadyRegistered
    | ActorUnknown
    | HumanPrincipalRequired
    | InvalidCursor

module RefusalCodes =
    // Flat exhaustive declaration: compiler coverage and artifact parity protect this mapping.
    // fsharplint:disable-next-line CyclomaticComplexity
    let vocabulary =
        Vocabulary.define
            "refusal"
            ((ViolationCodes.all |> List.map RefusalCode.InvalidInput)
             @ [
                 RefusalCode.RevisionConflict
                 RefusalCode.CaseAlreadyExists
                 RefusalCode.CaseErased
                 RefusalCode.CaseVoided
                 RefusalCode.AlreadyVoided
                 RefusalCode.NotVoided
                 RefusalCode.CaseClosed
                 RefusalCode.AlreadyClosed
                 RefusalCode.AlreadyOpened
                 RefusalCode.AmendmentRequiresUndecided
                 RefusalCode.CorrectionNoChanges
                 RefusalCode.CorrectionRequiresExistingValue
                 RefusalCode.DecisionRequired
                 RefusalCode.PaymentAlreadyRecorded
                 RefusalCode.PaymentNotRecorded
                 RefusalCode.DecisionAlreadyPaid
                 RefusalCode.ZeroDecisionCannotBePaid
                 RefusalCode.OperationResolved
                 RefusalCode.IdempotencyConflict
                 RefusalCode.ResourceUnavailable
                 RefusalCode.ActNotPermitted
                 RefusalCode.LastOwner
                 RefusalCode.ActorAlreadyRegistered
                 RefusalCode.ActorUnknown
                 RefusalCode.HumanPrincipalRequired
                 RefusalCode.InvalidCursor
             ])
            (function
             | RefusalCode.InvalidInput code -> ViolationCodes.token code
             | RefusalCode.RevisionConflict -> "CASE_REVISION_CONFLICT"
             | RefusalCode.CaseAlreadyExists -> "CASE_ALREADY_EXISTS"
             | RefusalCode.CaseErased -> "CASE_ERASED"
             | RefusalCode.CaseVoided -> "CASE_VOIDED"
             | RefusalCode.AlreadyVoided -> "ALREADY_VOIDED"
             | RefusalCode.NotVoided -> "NOT_VOIDED"
             | RefusalCode.CaseClosed -> "CASE_CLOSED"
             | RefusalCode.AlreadyClosed -> "CASE_ALREADY_CLOSED"
             | RefusalCode.AlreadyOpened -> "CASE_ALREADY_OPENED"
             | RefusalCode.AmendmentRequiresUndecided -> "CASE_AMENDMENT_REQUIRES_UNDECIDED"
             | RefusalCode.CorrectionNoChanges -> "CORRECTION_NO_CHANGES"
             | RefusalCode.CorrectionRequiresExistingValue -> "CORRECTION_EXISTING_VALUE_REQUIRED"
             | RefusalCode.DecisionRequired -> "CASE_DECISION_REQUIRED"
             | RefusalCode.PaymentAlreadyRecorded -> "CASE_PAYMENT_ALREADY_RECORDED"
             | RefusalCode.PaymentNotRecorded -> "CASE_PAYMENT_NOT_RECORDED"
             | RefusalCode.DecisionAlreadyPaid -> "CASE_DECISION_ALREADY_PAID"
             | RefusalCode.ZeroDecisionCannotBePaid -> "CASE_ZERO_DECISION_CANNOT_BE_PAID"
             | RefusalCode.OperationResolved -> "OPERATION_RESOLVED"
             | RefusalCode.IdempotencyConflict -> "OPERATION_CONTENT_CONFLICT"
             | RefusalCode.ResourceUnavailable -> "ACCESS_RESOURCE_UNAVAILABLE"
             | RefusalCode.ActNotPermitted -> "ACT_NOT_PERMITTED"
             | RefusalCode.LastOwner -> "LAST_OWNER"
             | RefusalCode.ActorAlreadyRegistered -> "ACTOR_ALREADY_REGISTERED"
             | RefusalCode.ActorUnknown -> "ACTOR_UNKNOWN"
             | RefusalCode.HumanPrincipalRequired -> "HUMAN_PRINCIPAL_REQUIRED"
             | RefusalCode.InvalidCursor -> "QUERY_CURSOR_INVALID")

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

    // Flat exhaustive declaration: compiler coverage and artifact parity protect this mapping.
    // fsharplint:disable-next-line CyclomaticComplexity
    let action =
        function
        | RefusalCode.InvalidInput _ -> RecommendedAction.CorrectInput
        | RefusalCode.RevisionConflict -> RecommendedAction.ReadCurrent
        | RefusalCode.CaseAlreadyExists -> RecommendedAction.NoneRequired
        | RefusalCode.CaseErased -> RecommendedAction.NoneRequired
        | RefusalCode.CaseVoided -> RecommendedAction.NoneRequired
        | RefusalCode.AlreadyVoided -> RecommendedAction.NoneRequired
        | RefusalCode.NotVoided -> RecommendedAction.NoneRequired
        | RefusalCode.CaseClosed -> RecommendedAction.NoneRequired
        | RefusalCode.AlreadyClosed -> RecommendedAction.NoneRequired
        | RefusalCode.AlreadyOpened -> RecommendedAction.NoneRequired
        | RefusalCode.AmendmentRequiresUndecided -> RecommendedAction.NoneRequired
        | RefusalCode.CorrectionNoChanges -> RecommendedAction.NoneRequired
        | RefusalCode.CorrectionRequiresExistingValue -> RecommendedAction.NoneRequired
        | RefusalCode.DecisionRequired -> RecommendedAction.NoneRequired
        | RefusalCode.PaymentAlreadyRecorded -> RecommendedAction.NoneRequired
        | RefusalCode.PaymentNotRecorded -> RecommendedAction.NoneRequired
        | RefusalCode.DecisionAlreadyPaid -> RecommendedAction.NoneRequired
        | RefusalCode.ZeroDecisionCannotBePaid -> RecommendedAction.NoneRequired
        | RefusalCode.OperationResolved -> RecommendedAction.ReadCurrent
        | RefusalCode.IdempotencyConflict -> RecommendedAction.StopAndInvestigate
        | RefusalCode.ResourceUnavailable -> RecommendedAction.NoneRequired
        | RefusalCode.ActNotPermitted -> RecommendedAction.NoneRequired
        | RefusalCode.LastOwner -> RecommendedAction.NoneRequired
        | RefusalCode.ActorAlreadyRegistered -> RecommendedAction.NoneRequired
        | RefusalCode.ActorUnknown -> RecommendedAction.NoneRequired
        | RefusalCode.HumanPrincipalRequired -> RecommendedAction.NoneRequired
        | RefusalCode.InvalidCursor -> RecommendedAction.ReadCurrent

    // Flat exhaustive declaration: compiler coverage and artifact parity protect this mapping.
    // fsharplint:disable-next-line CyclomaticComplexity
    let parameters =
        function
        | RefusalCode.InvalidInput code -> ViolationCodes.parameters code
        | RefusalCode.RevisionConflict
        | RefusalCode.CaseAlreadyExists
        | RefusalCode.CaseErased
        | RefusalCode.CaseVoided
        | RefusalCode.AlreadyVoided
        | RefusalCode.NotVoided
        | RefusalCode.CaseClosed
        | RefusalCode.AlreadyClosed
        | RefusalCode.AlreadyOpened
        | RefusalCode.AmendmentRequiresUndecided
        | RefusalCode.CorrectionNoChanges
        | RefusalCode.CorrectionRequiresExistingValue
        | RefusalCode.DecisionRequired
        | RefusalCode.PaymentAlreadyRecorded
        | RefusalCode.PaymentNotRecorded
        | RefusalCode.DecisionAlreadyPaid
        | RefusalCode.ZeroDecisionCannotBePaid
        | RefusalCode.OperationResolved
        | RefusalCode.IdempotencyConflict
        | RefusalCode.ResourceUnavailable
        | RefusalCode.ActNotPermitted
        | RefusalCode.LastOwner
        | RefusalCode.ActorAlreadyRegistered
        | RefusalCode.ActorUnknown
        | RefusalCode.HumanPrincipalRequired
        | RefusalCode.InvalidCursor -> []

module Refusals =
    // Flat exhaustive declaration: compiler coverage and artifact parity protect this mapping.
    // fsharplint:disable-next-line CyclomaticComplexity
    let code =
        function
        | Refusal.InvalidInput(_, violation) ->
            RefusalCode.InvalidInput(ViolationCodes.ofViolation violation)
        | Refusal.RevisionConflict _ -> RefusalCode.RevisionConflict
        | Refusal.CaseAlreadyExists -> RefusalCode.CaseAlreadyExists
        | Refusal.CaseErased -> RefusalCode.CaseErased
        | Refusal.CaseVoided -> RefusalCode.CaseVoided
        | Refusal.AlreadyVoided -> RefusalCode.AlreadyVoided
        | Refusal.NotVoided -> RefusalCode.NotVoided
        | Refusal.CaseClosed -> RefusalCode.CaseClosed
        | Refusal.AlreadyClosed -> RefusalCode.AlreadyClosed
        | Refusal.AlreadyOpened -> RefusalCode.AlreadyOpened
        | Refusal.AmendmentRequiresUndecided -> RefusalCode.AmendmentRequiresUndecided
        | Refusal.CorrectionNoChanges -> RefusalCode.CorrectionNoChanges
        | Refusal.CorrectionRequiresExistingValue -> RefusalCode.CorrectionRequiresExistingValue
        | Refusal.DecisionRequired -> RefusalCode.DecisionRequired
        | Refusal.PaymentAlreadyRecorded -> RefusalCode.PaymentAlreadyRecorded
        | Refusal.PaymentNotRecorded -> RefusalCode.PaymentNotRecorded
        | Refusal.DecisionAlreadyPaid -> RefusalCode.DecisionAlreadyPaid
        | Refusal.ZeroDecisionCannotBePaid -> RefusalCode.ZeroDecisionCannotBePaid
        | Refusal.OperationResolved -> RefusalCode.OperationResolved
        | Refusal.IdempotencyConflict -> RefusalCode.IdempotencyConflict
        | Refusal.ResourceUnavailable -> RefusalCode.ResourceUnavailable
        | Refusal.ActNotPermitted -> RefusalCode.ActNotPermitted
        | Refusal.LastOwner -> RefusalCode.LastOwner
        | Refusal.ActorAlreadyRegistered -> RefusalCode.ActorAlreadyRegistered
        | Refusal.ActorUnknown -> RefusalCode.ActorUnknown
        | Refusal.HumanPrincipalRequired -> RefusalCode.HumanPrincipalRequired
        | Refusal.InvalidCursor -> RefusalCode.InvalidCursor

    let row reason =
        let kind = code reason

        {
            Code = RefusalCodes.token kind
            Action = RefusalCodes.action kind
        }

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

    // Flat exhaustive declaration: compiler coverage and artifact parity protect this mapping.
    // fsharplint:disable-next-line CyclomaticComplexity
    let parameters =
        function
        | Refusal.InvalidInput(_, Violation.TooLong maximum) -> [ "maximumCharacters", maximum ]
        | Refusal.InvalidInput(_, Violation.DecimalFormat(integer, fraction)) ->
            [ "maximumIntegerDigits", integer; "maximumFractionalDigits", fraction ]
        | Refusal.InvalidInput(_, Violation.PageLimitRange maximum) ->
            [ "maximumPageSize", maximum ]
        | _ -> []

    let codes = RefusalCodes.all |> List.map RefusalCodes.token

    let definitions =
        RefusalCodes.all
        |> List.map (fun code ->
            {
                Id = RefusalCodes.token code
                Parameters =
                    RefusalCodes.parameters code |> List.map DiagnosticArguments.definition
            })

    let fromDiagnostic code target revision limits =
        let required name =
            Option.defaultWith (fun () -> invalidArg name "Missing validated diagnostic parameter.")

        // Flat exhaustive declaration: compiler coverage and artifact parity protect this mapping.
        // fsharplint:disable-next-line CyclomaticComplexity
        let violation =
            function
            | ViolationCode.TextRequired -> Violation.TextRequired
            | ViolationCode.MalformedUnicode -> Violation.MalformedUnicode
            | ViolationCode.SurroundingWhitespace -> Violation.SurroundingWhitespace
            | ViolationCode.TooLong -> Violation.TooLong(Map.find "maximumCharacters" limits)
            | ViolationCode.ControlCharacters -> Violation.ControlCharacters
            | ViolationCode.FormattingCharacter -> Violation.FormattingCharacter
            | ViolationCode.ReferenceFormat -> Violation.ReferenceFormat
            | ViolationCode.CountryFormat -> Violation.CountryFormat
            | ViolationCode.PrincipalFormat -> Violation.PrincipalFormat
            | ViolationCode.CalendarDateRequired -> Violation.CalendarDateRequired
            | ViolationCode.ChronologicalOrder -> Violation.ChronologicalOrder
            | ViolationCode.FutureDate -> Violation.FutureDate
            | ViolationCode.DecimalFormat ->
                Violation.DecimalFormat(
                    Map.find "maximumIntegerDigits" limits,
                    Map.find "maximumFractionalDigits" limits
                )
            | ViolationCode.CurrencyFormat -> Violation.CurrencyFormat
            | ViolationCode.OperationIdRequired -> Violation.OperationIdRequired
            | ViolationCode.ExpectedRevisionRange -> Violation.ExpectedRevisionRange
            | ViolationCode.PageLimitRange ->
                Violation.PageLimitRange(Map.find "maximumPageSize" limits)
            | ViolationCode.PaymentClearRequired -> Violation.PaymentClearRequired
            | ViolationCode.PaymentAcknowledgementRequired ->
                Violation.PaymentAcknowledgementRequired

        match RefusalCodes.parse code |> required (nameof code) with
        | RefusalCode.InvalidInput kind ->
            Refusal.InvalidInput(required (nameof target) target, violation kind)
        | RefusalCode.RevisionConflict ->
            Refusal.RevisionConflict(required (nameof revision) revision)
        | RefusalCode.CaseAlreadyExists -> Refusal.CaseAlreadyExists
        | RefusalCode.CaseErased -> Refusal.CaseErased
        | RefusalCode.CaseVoided -> Refusal.CaseVoided
        | RefusalCode.AlreadyVoided -> Refusal.AlreadyVoided
        | RefusalCode.NotVoided -> Refusal.NotVoided
        | RefusalCode.CaseClosed -> Refusal.CaseClosed
        | RefusalCode.AlreadyClosed -> Refusal.AlreadyClosed
        | RefusalCode.AlreadyOpened -> Refusal.AlreadyOpened
        | RefusalCode.AmendmentRequiresUndecided -> Refusal.AmendmentRequiresUndecided
        | RefusalCode.CorrectionNoChanges -> Refusal.CorrectionNoChanges
        | RefusalCode.CorrectionRequiresExistingValue -> Refusal.CorrectionRequiresExistingValue
        | RefusalCode.DecisionRequired -> Refusal.DecisionRequired
        | RefusalCode.PaymentAlreadyRecorded -> Refusal.PaymentAlreadyRecorded
        | RefusalCode.PaymentNotRecorded -> Refusal.PaymentNotRecorded
        | RefusalCode.DecisionAlreadyPaid -> Refusal.DecisionAlreadyPaid
        | RefusalCode.ZeroDecisionCannotBePaid -> Refusal.ZeroDecisionCannotBePaid
        | RefusalCode.OperationResolved -> Refusal.OperationResolved
        | RefusalCode.IdempotencyConflict -> Refusal.IdempotencyConflict
        | RefusalCode.ResourceUnavailable -> Refusal.ResourceUnavailable
        | RefusalCode.ActNotPermitted -> Refusal.ActNotPermitted
        | RefusalCode.LastOwner -> Refusal.LastOwner
        | RefusalCode.ActorAlreadyRegistered -> Refusal.ActorAlreadyRegistered
        | RefusalCode.ActorUnknown -> Refusal.ActorUnknown
        | RefusalCode.HumanPrincipalRequired -> Refusal.HumanPrincipalRequired
        | RefusalCode.InvalidCursor -> Refusal.InvalidCursor

module RecommendedActions =
    // Flat exhaustive declaration: compiler coverage and artifact parity protect this mapping.
    // fsharplint:disable-next-line CyclomaticComplexity
    let vocabulary =
        Vocabulary.define
            "action"
            [
                RecommendedAction.CorrectInput
                RecommendedAction.ReadCurrent
                RecommendedAction.RetrySafe
                RecommendedAction.ReplayExact
                RecommendedAction.Reauthenticate
                RecommendedAction.StopAndInvestigate
                RecommendedAction.NoneRequired
            ]
            (function
             | RecommendedAction.CorrectInput -> "CORRECT_INPUT"
             | RecommendedAction.ReadCurrent -> "READ_CURRENT"
             | RecommendedAction.RetrySafe -> "RETRY_SAFE"
             | RecommendedAction.ReplayExact -> "REPLAY_EXACT"
             | RecommendedAction.Reauthenticate -> "REAUTHENTICATE"
             | RecommendedAction.StopAndInvestigate -> "STOP_AND_INVESTIGATE"
             | RecommendedAction.NoneRequired -> "NONE_REQUIRED")

    let all = Vocabulary.entries vocabulary
    let token = Vocabulary.token vocabulary


[<AutoOpen>]
module RefusalProperties =
    type Refusal with
        member this.Field = Refusals.field this
        member this.ActualRevision = Refusals.actualRevision this
        member this.Action = (Refusals.row this).Action
