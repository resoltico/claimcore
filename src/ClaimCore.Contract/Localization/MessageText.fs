namespace ClaimCore.Contract

/// Authored English terminology and safe message templates. No file access is used by core decisions.
[<RequireQualifiedAccess>]
type MessagePart =
    | Literal of string
    | Hole of string

module MessageText =
    let private definitions =
        [
            "diagnostic.PRINCIPAL_INPUT_INVALID",
            [
                MessagePart.Literal "Correct principal field "
                MessagePart.Hole "target"
                MessagePart.Literal ": "
                MessagePart.Hole "issue"
                MessagePart.Literal ". No operation was dispatched by this attempt."
            ]
            "cli.callbackAccepted",
            [
                MessagePart.Literal "Authentication response received. Return to the terminal."
            ]
            "cli.callbackRefused", [ MessagePart.Literal "Authentication response refused." ]
            "cli.beforeDispatchFailure",
            [ MessagePart.Literal "Unexpected failure before dispatch." ]
            "authority.REGISTER_ACTOR.label", [ MessagePart.Literal "Register actor" ]
            "authority.REGISTER_ACTOR.meaning",
            [
                MessagePart.Literal
                    "Register a principal without granting case or administration capabilities."
            ]
            "authority.SET_ENABLED.label", [ MessagePart.Literal "Set actor enabled" ]
            "authority.SET_ENABLED.meaning",
            [
                MessagePart.Literal
                    "Enable or disable an actor. The final enabled human owner remains protected."
            ]
            "authority.SET_GRANT.label", [ MessagePart.Literal "Set actor grant" ]
            "authority.SET_GRANT.meaning",
            [ MessagePart.Literal "Grant or revoke a role at installation or case scope." ]
            "authority.field.act.label", [ MessagePart.Literal "Operation" ]
            "authority.field.active.label", [ MessagePart.Literal "Grant active" ]
            "authority.field.enabled.label", [ MessagePart.Literal "Actor enabled" ]
            "authority.field.expectedRevision.label", [ MessagePart.Literal "Expected revision" ]
            "authority.field.issuer.label", [ MessagePart.Literal "Identity issuer" ]
            "authority.field.kind.label", [ MessagePart.Literal "Type" ]
            "authority.field.operationId.label", [ MessagePart.Literal "Operation ID" ]
            "authority.field.role.label", [ MessagePart.Literal "Role" ]
            "authority.field.scope.label", [ MessagePart.Literal "Grant scope" ]
            "authority.field.subject.label", [ MessagePart.Literal "Subject or client ID" ]
            "authority.field.target.label", [ MessagePart.Literal "Actor principal" ]
            "command.AMEND_REGISTRATION.label", [ MessagePart.Literal "Amend registration" ]
            "command.AMEND_REGISTRATION.meaning",
            [
                MessagePart.Literal
                    "Replace the initial facts. The handler's case reference remains unchanged."
            ]
            "command.CLEAR_PAYMENT.label",
            [ MessagePart.Literal "Correct an erroneous payment record" ]
            "command.CLEAR_PAYMENT.meaning",
            [
                MessagePart.Literal
                    "Remove an erroneously recorded payment date. This is not a refund or reversal of an actual transfer. Earlier history remains."
            ]
            "command.CLOSE.label", [ MessagePart.Literal "Close the case" ]
            "command.CLOSE.meaning",
            [
                MessagePart.Literal "Close the case. Closing does not mean that payment was made."
            ]
            "command.CORRECT_CASE.label", [ MessagePart.Literal "Correct case facts" ]
            "command.CORRECT_CASE.meaning",
            [
                MessagePart.Literal
                    "Correct recorded details on a case with a decision, payment or closed status. All changes are recorded together. Clearing a payment date does not refund money."
            ]
            "command.DECIDE.label", [ MessagePart.Literal "Record payment decision" ]
            "command.DECIDE.meaning",
            [
                MessagePart.Literal
                    "Record or replace the decided amount and currency. This does not send money."
            ]
            "command.ERASE.label", [ MessagePart.Literal "Erase case payload" ]
            "command.ERASE.meaning",
            [
                MessagePart.Literal
                    "Permanently remove business payload from the live database. WAL, backups and copies elsewhere are not erased."
            ]
            "command.OPEN.label", [ MessagePart.Literal "Open a case" ]
            "command.OPEN.meaning",
            [
                MessagePart.Literal "Register the incident, claimant, insurer and amount claimed."
            ]
            "command.RECORD_PAYMENT.label", [ MessagePart.Literal "Record actual payment" ]
            "command.RECORD_PAYMENT.meaning",
            [
                MessagePart.Literal
                    "Record the date the full decided amount was actually paid. This does not execute or verify a transfer."
            ]
            "command.REINSTATE.label", [ MessagePart.Literal "Reinstate case record" ]
            "command.REINSTATE.meaning",
            [ MessagePart.Literal "Restore ordinary case work on a voided record." ]
            "command.REOPEN.label", [ MessagePart.Literal "Reopen the case" ]
            "command.REOPEN.meaning",
            [ MessagePart.Literal "Reopen the case, preserving its details and history." ]
            "command.VOID.label", [ MessagePart.Literal "Void case record" ]
            "command.VOID.meaning",
            [
                MessagePart.Literal
                    "Block business commands while retaining the case and its history."
            ]
            "command.WITHDRAW_DECISION.label", [ MessagePart.Literal "Withdraw payment decision" ]
            "command.WITHDRAW_DECISION.meaning",
            [
                MessagePart.Literal "Clear the unpaid decision while preserving its history."
            ]
            "diagnostic.ACCESS_RESOURCE_UNAVAILABLE",
            [ MessagePart.Literal "The requested resource is unavailable." ]
            "diagnostic.ACTOR_ALREADY_REGISTERED",
            [ MessagePart.Literal "This principal is already registered." ]
            "diagnostic.ACTOR_UNKNOWN", [ MessagePart.Literal "This principal is not registered." ]
            "diagnostic.ACT_NOT_PERMITTED", [ MessagePart.Literal "This act is not permitted." ]
            "diagnostic.ALREADY_VOIDED", [ MessagePart.Literal "This case is already voided." ]
            "diagnostic.CASE_ALREADY_CLOSED", [ MessagePart.Literal "The case is already closed." ]
            "diagnostic.CASE_ALREADY_EXISTS",
            [ MessagePart.Literal "The case reference already exists." ]
            "diagnostic.CASE_ALREADY_OPENED", [ MessagePart.Literal "The case is already open." ]
            "diagnostic.CASE_AMENDMENT_REQUIRES_UNDECIDED",
            [
                MessagePart.Literal
                    "“Amend registration” requires an undecided case. Use “Correct case facts” to correct recorded registration while keeping its decision."
            ]
            "diagnostic.CASE_CLOSED",
            [
                MessagePart.Literal
                    "This action cannot change a closed case. Use “Correct case facts” for factual corrections while keeping its status, or “Reopen the case” when you intend to resume ordinary case work."
            ]
            "diagnostic.CASE_DECISION_ALREADY_PAID",
            [
                MessagePart.Literal
                    "“Record payment decision” and “Withdraw payment decision” cannot change a paid decision. Use “Correct case facts” for a factual correction, with explicit payment reaffirmation or clearing when replacing a paid decision."
            ]
            "diagnostic.CASE_DECISION_REQUIRED",
            [ MessagePart.Literal "Record a payment decision first." ]
            "diagnostic.CASE_ERASED", [ MessagePart.Literal "This case has been erased." ]
            "diagnostic.CASE_PAYMENT_ALREADY_RECORDED",
            [ MessagePart.Literal "ClaimCore records one full payment." ]
            "diagnostic.CASE_PAYMENT_NOT_RECORDED",
            [ MessagePart.Literal "There is no payment record to clear." ]
            "diagnostic.CASE_REVISION_CONFLICT",
            [ MessagePart.Literal "Read the current case before making a changed request." ]
            "diagnostic.CASE_VOIDED", [ MessagePart.Literal "This case is voided." ]
            "diagnostic.CASE_ZERO_DECISION_CANNOT_BE_PAID",
            [ MessagePart.Literal "A zero decision is not a payment." ]
            "diagnostic.COMMAND_EXPECTED_REVISION_RANGE",
            [
                MessagePart.Literal "Use an integer revision from 0 through 9007199254740991."
            ]
            "diagnostic.COMMAND_OPERATION_ID_REQUIRED",
            [ MessagePart.Literal "Use a non-empty UUID." ]
            "diagnostic.CORE_INTERNAL_ERROR",
            [ MessagePart.Literal "An internal error occurred. Stop and investigate." ]
            "diagnostic.CORE_OPERATION_CONTENT_CONFLICT",
            [
                MessagePart.Literal
                    "Stored operation content is inconsistent. Stop and investigate."
            ]
            "diagnostic.CORE_SCHEMA_MISMATCH",
            [
                MessagePart.Literal
                    "The schema differs from this build. Run migrate or use a matching build."
            ]
            "diagnostic.CORE_STORE_INTEGRITY_ERROR",
            [ MessagePart.Literal "Stored data failed verification. Stop and run verify." ]
            "diagnostic.CORE_STORE_UNAVAILABLE",
            [ MessagePart.Literal "The store is unavailable. Retry." ]
            "diagnostic.CORRECTION_EXISTING_VALUE_REQUIRED",
            [
                MessagePart.Literal
                    "A correction can only replace or clear a value already recorded on this case."
            ]
            "diagnostic.CORRECTION_NO_CHANGES",
            [
                MessagePart.Literal
                    "Choose at least one factual correction that changes the current case."
            ]
            "diagnostic.CORRECTION_PAYMENT_ACKNOWLEDGEMENT_REQUIRED",
            [
                MessagePart.Literal
                    "Replacing a paid decision requires explicit payment reaffirmation or clearing the payment record."
            ]
            "diagnostic.CORRECTION_PAYMENT_CLEAR_REQUIRED",
            [
                MessagePart.Literal "Clearing a paid decision requires clearing the payment record."
            ]
            "diagnostic.HOST_ANTIFORGERYREJECTED",
            [ MessagePart.Literal "Request verification was refused." ]
            "diagnostic.HOST_BEFOREDISPATCHFAILED",
            [ MessagePart.Literal "Processing failed before dispatch." ]
            "diagnostic.HOST_BODYTOOLARGE", [ MessagePart.Literal "Request body is too large." ]
            "diagnostic.HOST_COMPLETEDRESPONSEFAILED",
            [ MessagePart.Literal "The completed response could not be delivered." ]
            "diagnostic.HOST_CONNECTIONREJECTED", [ MessagePart.Literal "Connection was refused." ]
            "diagnostic.HOST_CONTRACTMISMATCH",
            [
                MessagePart.Literal "The client contract differs. Reload or use a matching client."
            ]
            "diagnostic.HOST_DISPATCHUNCONFIRMED",
            [ MessagePart.Literal "Preserve the exact request and replay it." ]
            "diagnostic.HOST_ENDPOINTMISSING", [ MessagePart.Literal "Endpoint was not found." ]
            "diagnostic.HOST_INPUTINVALID",
            [ MessagePart.Literal "The input does not satisfy the endpoint contract." ]
            "diagnostic.HOST_MEDIATYPEREJECTED",
            [ MessagePart.Literal "Use the endpoint media type." ]
            "diagnostic.HOST_METHODREJECTED", [ MessagePart.Literal "The method is unsupported." ]
            "diagnostic.HOST_ORIGINREJECTED", [ MessagePart.Literal "Origin was refused." ]
            "diagnostic.HOST_RATELIMITED", [ MessagePart.Literal "Request admission is busy." ]
            "diagnostic.HOST_SESSIONFORBIDDEN",
            [ MessagePart.Literal "Session access was refused." ]
            "diagnostic.HOST_SESSIONREJECTED", [ MessagePart.Literal "Authentication was refused." ]
            "diagnostic.HUMAN_PRINCIPAL_REQUIRED",
            [ MessagePart.Literal "This role requires a human principal." ]
            "diagnostic.INPUT_CALENDAR_DATE_REQUIRED",
            [ MessagePart.Literal "Use one valid calendar date in YYYY-MM-DD format." ]
            "diagnostic.INPUT_CASE_REFERENCE_FORMAT",
            [ MessagePart.Literal "Use 1 to 80 printable non-space ASCII characters." ]
            "diagnostic.INPUT_CONTROL_CHARACTERS",
            [ MessagePart.Literal "Control characters are not accepted." ]
            "diagnostic.INPUT_COUNTRY_FORMAT",
            [ MessagePart.Literal "Use two uppercase ASCII letters for the country code." ]
            "diagnostic.INPUT_CURRENCY_FORMAT",
            [
                MessagePart.Literal
                    "Use three uppercase ASCII letters (A–Z) for the currency identifier."
            ]
            "diagnostic.INPUT_DATE_ORDER",
            [ MessagePart.Literal "The dates are in an invalid chronological order." ]
            "diagnostic.INPUT_DECIMAL_FORMAT",
            [
                MessagePart.Literal "Use non-negative decimal text: up to "
                MessagePart.Hole "maximumIntegerDigits"
                MessagePart.Literal " integer digits and "
                MessagePart.Hole "maximumFractionalDigits"
                MessagePart.Literal
                    " fractional digits; no sign, exponent or redundant leading zeros: 0.10 is valid; 00.10 is invalid."
            ]
            "diagnostic.INPUT_FUTURE_DATE",
            [
                MessagePart.Literal
                    "A future date cannot record an event that has already occurred."
            ]
            "diagnostic.INPUT_MALFORMED_UNICODE",
            [ MessagePart.Literal "Malformed Unicode is not accepted." ]
            "diagnostic.INPUT_SURROUNDING_WHITESPACE",
            [ MessagePart.Literal "Leading or trailing whitespace is not accepted." ]
            "diagnostic.INPUT_TEXT_REQUIRED",
            [ MessagePart.Literal "A non-blank value is required." ]
            "diagnostic.INPUT_TEXT_TOO_LONG",
            [
                MessagePart.Literal "The value must not exceed "
                MessagePart.Hole "maximumCharacters"
                MessagePart.Literal " Unicode characters."
            ]
            "diagnostic.LAST_OWNER",
            [ MessagePart.Literal "At least one enabled human owner is required." ]
            "diagnostic.LEDGER_SEQUENCE_EXHAUSTED",
            [ MessagePart.Literal "The ledger has reached its final sequence." ]
            "diagnostic.NOT_VOIDED", [ MessagePart.Literal "This case is not voided." ]
            "diagnostic.OPERATION_CONTENT_CONFLICT",
            [
                MessagePart.Literal
                    "This operation ID belongs to different command content. Do not reuse it."
            ]
            "diagnostic.QUERY_CURSOR_INVALID",
            [ MessagePart.Literal "Use one pagination cursor returned by ClaimCore." ]
            "diagnostic.QUERY_PAGE_LIMIT_RANGE",
            [
                MessagePart.Literal "Use a value from 1 through "
                MessagePart.Hole "maximumPageSize"
                MessagePart.Literal "."
            ]
            "diagnostic.REVISION_EXHAUSTED",
            [ MessagePart.Literal "The case has reached its final revision." ]
            "diagnostic.TEXT_FORMATTING_CHARACTER",
            [
                MessagePart.Literal "Formatting characters are not accepted in a new assertion."
            ]
            "field.caseReference.label", [ MessagePart.Literal "Handler's case reference" ]
            "field.caseReference.meaning",
            [
                MessagePart.Literal
                    "Reference assigned by the claims handler operating this register, not an insurer reference. Unique and immutable within one installation; printable non-space ASCII with case-insensitive identity and authored spelling retained."
            ]
            "field.claimantName.label", [ MessagePart.Literal "Claimant name" ]
            "field.claimantName.meaning",
            [ MessagePart.Literal "Name of the person or company making the claim." ]
            "field.claimedAmount.label", [ MessagePart.Literal "Amount claimed" ]
            "field.claimedAmount.meaning",
            [ MessagePart.Literal "Amount requested by the claimant." ]
            "field.claimedCurrency.label", [ MessagePart.Literal "Currency of claimed amount" ]
            "field.claimedCurrency.meaning",
            [ MessagePart.Literal "Currency in which the amount is claimed." ]
            "field.incidentCountry.label",
            [ MessagePart.Literal "Country of incident (ISO alpha-2 code)" ]
            "field.incidentCountry.meaning",
            [
                MessagePart.Literal
                    "Two uppercase ASCII letters identifying the incident country; ISO alpha-2 syntax only, without membership validation. ZZ means unknown or international."
            ]
            "field.incidentDate.label", [ MessagePart.Literal "Incident date" ]
            "field.incidentDate.meaning",
            [ MessagePart.Literal "Date on which the incident occurred." ]
            "field.incidentNotificationDate.label",
            [ MessagePart.Literal "Incident notification date (FNOL)" ]
            "field.incidentNotificationDate.meaning",
            [
                MessagePart.Literal
                    "Date when this claims handler was first notified of the incident by anyone, not necessarily the claimant. Not the date another insurer or organisation was notified."
            ]
            "field.insurerName.label", [ MessagePart.Literal "Responsible insurer" ]
            "field.insurerName.meaning",
            [
                MessagePart.Literal
                    "Insurer recorded by the claims handler as responsible for this claim."
            ]
            "field.payableAmount.label", [ MessagePart.Literal "Amount to be paid" ]
            "field.payableAmount.meaning",
            [
                MessagePart.Literal
                    "Amount the handler decided should be paid. Not recorded until decided; may differ from the claimed amount."
            ]
            "field.payableCurrency.label", [ MessagePart.Literal "Currency of amount to be paid" ]
            "field.payableCurrency.meaning",
            [
                MessagePart.Literal
                    "Currency of the amount to be paid. Not recorded until decided; may differ from the claimed currency."
            ]
            "field.paymentDate.label", [ MessagePart.Literal "Payment date" ]
            "field.paymentDate.meaning",
            [
                MessagePart.Literal
                    "Date when the full decided amount was paid, as recorded by the handler. Leave unrecorded if unpaid or unknown. Recording payment does not make or verify a bank transfer."
            ]
            "field.paymentDecisionDate.label", [ MessagePart.Literal "Payment decision date" ]
            "field.paymentDecisionDate.meaning",
            [
                MessagePart.Literal
                    "Date when this claims handler decided the amount to be paid. Not recorded until a decision is made."
            ]
            "field.reason.label", [ MessagePart.Literal "Stewardship reason" ]
            "field.reason.meaning", [ MessagePart.Literal "Explain the record stewardship action." ]
            "field.status.label", [ MessagePart.Literal "Case status" ]
            "field.status.meaning",
            [
                MessagePart.Literal
                    "Whether the case is open or closed. Closing a case does not mean that payment was made."
            ]
            "group.decision.label", [ MessagePart.Literal "Payment decision" ]
            "group.decision.meaning",
            [
                MessagePart.Literal
                    "Keep the current payment decision, replace its date, amount and currency together, or clear the decision."
            ]
            "group.payment.label", [ MessagePart.Literal "Payment record" ]
            "group.payment.meaning",
            [
                MessagePart.Literal
                    "Keep the current payment date, replace it, or clear the recorded date. Clearing the date does not refund money."
            ]
            "group.registration.label", [ MessagePart.Literal "Registration" ]
            "group.registration.meaning",
            [
                MessagePart.Literal
                    "Keep the current registration details or replace all seven details together."
            ]
        ]

    let admitDefinitions (definitions: (string * MessagePart list) list) =
        let messages = Map.ofList definitions

        if messages.Count <> List.length definitions then
            invalidArg (nameof definitions) "Duplicate core message identity."

        messages

    let all = admitDefinitions definitions

    let template identity = all |> Map.find identity

    let literal identity =
        match template identity with
        | [ MessagePart.Literal value ] -> value
        | _ -> invalidArg (nameof identity) "This message requires typed arguments."
