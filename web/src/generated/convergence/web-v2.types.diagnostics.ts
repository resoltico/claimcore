/* Generated from ClaimCore.Contracts. Do not edit. */
export type Rejection = {
  readonly code:
    | "INVALID_INPUT"
    | "CASE_NOT_FOUND"
    | "CASE_ALREADY_EXISTS"
    | "VERSION_CONFLICT"
    | "CASE_CLOSED"
    | "AMENDMENT_REQUIRES_UNDECIDED"
    | "DECISION_REQUIRED"
    | "PAYMENT_ALREADY_RECORDED"
    | "PAYMENT_NOT_RECORDED"
    | "DECISION_ALREADY_PAID"
    | "ALREADY_CLOSED"
    | "ALREADY_OPENED"
    | "ZERO_DECISION_CANNOT_BE_PAID"
    | "IDEMPOTENCY_CONFLICT"
    | "OPERATION_REVOKED"
    | "RECOVERY_ATTEMPT_LIMIT_REACHED";
  readonly diagnostic:
    | {
        readonly id:
          | "INPUT_TEXT_REQUIRED"
          | "INPUT_MALFORMED_UNICODE"
          | "INPUT_SURROUNDING_WHITESPACE"
          | "INPUT_CONTROL_CHARACTERS"
          | "INPUT_CALENDAR_DATE_REQUIRED"
          | "INPUT_DATE_ORDER"
          | "INPUT_FUTURE_DATE"
          | "INPUT_CURRENCY_FORMAT"
          | "INPUT_AMOUNT_NOT_REPRESENTABLE"
          | "COMMAND_OPERATION_ID_REQUIRED"
          | "COMMAND_EXPECTED_REVISION_RANGE"
          | "STATE_STORED_REVISION_RANGE"
          | "STATE_REVISION_EXHAUSTED"
          | "STATE_CASE_REFERENCE_MISMATCH"
          | "STATE_TRANSITION_MISMATCH"
          | "INPUT_EXACT_FIELDS_REQUIRED"
          | "COMMAND_GROUPED_CORRECTION_REQUIRED"
          | "CORRECTION_COMPLETE_DECISION_REQUIRED"
          | "CORRECTION_PAYMENT_CLEAR_REQUIRED"
          | "CORRECTION_PAYMENT_ACKNOWLEDGEMENT_REQUIRED"
          | "CORRECTION_PAYMENT_DECISION_REQUIRED"
          | "CORRECTION_REPLACEMENT_FIELDS_REQUIRED"
          | "CORRECTION_REGISTRATION_CLEAR_FORBIDDEN"
          | "CASE_NOT_FOUND"
          | "CASE_ALREADY_EXISTS"
          | "CASE_REVISION_CONFLICT"
          | "CASE_CLOSED"
          | "CASE_ALREADY_CLOSED"
          | "CASE_ALREADY_OPENED"
          | "CASE_AMENDMENT_REQUIRES_UNDECIDED"
          | "CORRECTION_NO_CHANGES"
          | "CORRECTION_EXISTING_VALUE_REQUIRED"
          | "CASE_DECISION_REQUIRED"
          | "CASE_PAYMENT_ALREADY_RECORDED"
          | "CASE_PAYMENT_NOT_RECORDED"
          | "CASE_DECISION_ALREADY_PAID"
          | "CASE_ZERO_DECISION_CANNOT_BE_PAID"
          | "QUERY_HISTORY_CURSOR_INVALID"
          | "OPERATION_CONTENT_CONFLICT"
          | "OPERATION_REVOKED"
          | "OPERATION_RECOVERY_ATTEMPT_LIMIT";
        readonly parameters: Readonly<Record<string, never>>;
      }
    | {
        readonly id: "INPUT_TEXT_TOO_SHORT";
        readonly parameters: { readonly minimumCharacters: number };
      }
    | {
        readonly id: "INPUT_TEXT_TOO_LONG";
        readonly parameters: { readonly maximumCharacters: number };
      }
    | {
        readonly id: "INPUT_DECIMAL_FORMAT";
        readonly parameters: {
          readonly maximumIntegerDigits: number;
          readonly maximumFractionalDigits: number;
        };
      }
    | {
        readonly id: "QUERY_PAGE_LIMIT_RANGE";
        readonly parameters: { readonly maximumPageSize: number };
      };
  readonly message: string;
  readonly field:
    | "incidentDate"
    | "incidentNotificationDate"
    | "incidentCountry"
    | "claimantName"
    | "insurerName"
    | "claimedAmount"
    | "claimedCurrency"
    | "caseReference"
    | "paymentDecisionDate"
    | "payableAmount"
    | "payableCurrency"
    | "paymentDate"
    | "status"
    | "operationId"
    | "expectedRevision"
    | "revision"
    | "command"
    | "fields"
    | "registration"
    | "decision"
    | "payment"
    | "limit"
    | "cursor"
    | null;
  readonly actualRevision: string | null;
  readonly recommendedAction:
    | "CORRECT_INPUT"
    | "READ_CURRENT"
    | "RETRY_SAFE"
    | "RECOVER_EXACT"
    | "REAUTHENTICATE"
    | "STOP_AND_INVESTIGATE"
    | "NONE_REQUIRED";
};
