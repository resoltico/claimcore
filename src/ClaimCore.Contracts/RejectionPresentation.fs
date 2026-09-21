namespace ClaimCore.Contracts

open System.Globalization
open ClaimCore.Application

/// The existing English presentation belongs to the outward adapter, never to core meaning.
/// A future catalog selects by identity and typed arguments; no English-to-key lookup is permitted.
module RejectionPresentation =
    let private integer (value: int) =
        value.ToString(CultureInfo.InvariantCulture)

    let private text =
        [
            RejectionDiagnosticId.TextRequired, "A non-blank value is required."
            RejectionDiagnosticId.MalformedUnicode, "Malformed Unicode is not accepted."
            RejectionDiagnosticId.SurroundingWhitespace,
            "Leading or trailing whitespace is not accepted."
            RejectionDiagnosticId.ControlCharacters, "Control characters are not accepted."
        ]

    let private scalar =
        [
            RejectionDiagnosticId.CalendarDateRequired,
            "Use one valid calendar date in YYYY-MM-DD format."
            RejectionDiagnosticId.ChronologicalOrder,
            "The dates are in an invalid chronological order."
            RejectionDiagnosticId.FutureDate,
            "A future date cannot record an event that has already occurred."
            RejectionDiagnosticId.CurrencyFormat,
            "Use a three-letter uppercase currency identifier."
            RejectionDiagnosticId.AmountNotRepresentable,
            "The decimal amount cannot be represented exactly."
        ]

    let private command =
        [
            RejectionDiagnosticId.EmptyOperationId, "Use a non-empty UUID."
            RejectionDiagnosticId.ExpectedVersionOutOfRange,
            "Use a non-negative version below Int64.MaxValue."
            RejectionDiagnosticId.StoredVersionOutOfRange,
            "Stored versions must be positive and below Int64.MaxValue."
            RejectionDiagnosticId.RevisionExhausted,
            "The case revision cannot advance below Int64.MaxValue."
            RejectionDiagnosticId.CaseReferenceMismatch,
            "The loaded case does not match this command."
            RejectionDiagnosticId.StateTransitionMismatch,
            "The state guard and payment transition disagree."
            RejectionDiagnosticId.ExactInputsRequired,
            "Supply each declared command input exactly once, with no extra fields."
            RejectionDiagnosticId.GroupedCorrectionRequired,
            "Supply all three tagged correction groups."
        ]

    let private correction =
        [
            RejectionDiagnosticId.CompleteDecisionRequired,
            "Decision date, payable amount and payable currency must be present together or all absent."
            RejectionDiagnosticId.PaymentClearRequired,
            "Clearing a paid decision requires clearing the payment record."
            RejectionDiagnosticId.PaymentAcknowledgementRequired,
            "Replacing a paid decision requires explicit payment reaffirmation or clearing the payment record."
            RejectionDiagnosticId.PaymentDecisionRequired,
            "A payment record requires a complete payment decision."
            RejectionDiagnosticId.ReplacementFieldsRequired,
            "Use REPLACE with every declared field."
            RejectionDiagnosticId.RegistrationCannotBeCleared, "Registration cannot be cleared."
        ]

    let private case =
        [
            RejectionDiagnosticId.CaseNotFound, "The case reference was not found."
            RejectionDiagnosticId.CaseAlreadyExists, "The case reference already exists."
            RejectionDiagnosticId.VersionConflict,
            "Read the current case before making a changed request."
            RejectionDiagnosticId.CaseClosed, "Reopen the case before changing its facts."
            RejectionDiagnosticId.AlreadyClosed, "The case is already closed."
            RejectionDiagnosticId.AlreadyOpened, "The case is already open."
        ]

    let private progress =
        [
            RejectionDiagnosticId.AmendmentRequiresUndecided,
            "Withdraw an unpaid decision before amending registration."
            RejectionDiagnosticId.CorrectionNoChanges,
            "Choose at least one factual correction that changes the current case."
            RejectionDiagnosticId.CorrectionRequiresExistingValue,
            "A correction can only replace or clear a value already recorded on this case."
            RejectionDiagnosticId.DecisionRequired, "Record a payment decision first."
            RejectionDiagnosticId.PaymentAlreadyRecorded, "ClaimCore records one full payment."
            RejectionDiagnosticId.PaymentNotRecorded, "There is no payment record to clear."
            RejectionDiagnosticId.DecisionAlreadyPaid,
            "A recorded payment prevents changing its decision."
            RejectionDiagnosticId.ZeroDecisionCannotBePaid, "A zero decision is not a payment."
        ]

    let private operation =
        [
            RejectionDiagnosticId.InvalidHistoryCursor,
            "Use one opaque history cursor returned by ClaimCore."
            RejectionDiagnosticId.IdempotencyConflict,
            "This operation ID belongs to different command content. Do not reuse it."
            RejectionDiagnosticId.OperationRevoked,
            "This exact operation was durably revoked before execution."
            RejectionDiagnosticId.RecoveryAttemptLimitReached,
            "This operation has reached its recovery attempt limit. Read the current case and author a new operation only after review."
        ]

    let private messages =
        text @ scalar @ command @ correction @ case @ progress @ operation

    let render (rejection: Rejection) =
        let diagnostic = RejectionDiagnostics.describe rejection

        match RejectionDiagnostics.parameters diagnostic with
        | DiagnosticParameters.MinimumCharacters minimum ->
            $"The value must contain at least {integer minimum} Unicode characters."
        | DiagnosticParameters.MaximumCharacters maximum ->
            $"The value must not exceed {integer maximum} Unicode characters."
        | DiagnosticParameters.DecimalDigits(integral, fractional) ->
            $"Use non-negative decimal text: up to {integer integral} integer digits and {integer fractional} fractional digits; no sign or exponent."
        | DiagnosticParameters.MaximumPageSize maximum ->
            $"Use a value from 1 through {integer maximum}."
        | DiagnosticParameters.None ->
            let identifier = RejectionDiagnostics.identifier diagnostic
            messages |> List.find (fst >> (=) identifier) |> snd
