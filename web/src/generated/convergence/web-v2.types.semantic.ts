/* Generated from ClaimCore.Contracts. Do not edit. */
export type FieldDescriptor = {
  readonly name: string;
  readonly nativeName: string;
  readonly label: string;
  readonly meaning: string;
  readonly allowsAbsence: boolean;
  readonly scalar:
    | {
        readonly kind: "TEXT";
        readonly minimumCharacters: number;
        readonly maximumCharacters: number;
        readonly requiresNonBlank: boolean;
        readonly rejectsSurroundingWhitespace: boolean;
        readonly rejectsControlCharacters: boolean;
        readonly requiresWellFormedUnicode: boolean;
      }
    | {
        readonly kind: "CALENDAR_DATE";
        readonly exactFormat: string;
        readonly minimum: string;
        readonly maximum: string;
      }
    | {
        readonly kind: "AMOUNT";
        readonly grammar: string;
        readonly maximumIntegerDigits: number;
        readonly maximumFractionalDigits: number;
      }
    | { readonly kind: "CURRENCY"; readonly grammar: string; readonly exactCharacters: number }
    | { readonly kind: "CASE_STATUS"; readonly allowedValues: ReadonlyArray<"OPENED" | "CLOSED"> };
};
export type CommandInputDescriptor =
  | { readonly fieldName: string; readonly prefill: "BLANK" }
  | {
      readonly fieldName: string;
      readonly prefill: "CURRENT_FIELD";
      readonly currentField: string;
    };
export type CorrectionGroupDescriptor = {
  readonly name: string;
  readonly label: string;
  readonly meaning: string;
  readonly actions: ReadonlyArray<"KEEP" | "REPLACE" | "CLEAR">;
  readonly replaceFields: ReadonlyArray<CommandInputDescriptor>;
};
export type CommandInputShape =
  | { readonly kind: "FIELDS"; readonly fields: ReadonlyArray<CommandInputDescriptor> }
  | {
      readonly kind: "CORRECTION_GROUPS";
      readonly groups: ReadonlyArray<CorrectionGroupDescriptor>;
    };
export type CommandDescriptor = {
  readonly kind:
    | "OPEN"
    | "AMEND_REGISTRATION"
    | "CORRECT_CASE"
    | "DECIDE"
    | "WITHDRAW_DECISION"
    | "RECORD_PAYMENT"
    | "CLEAR_PAYMENT"
    | "CLOSE"
    | "REOPEN";
  readonly label: string;
  readonly meaning: string;
  readonly inputs: CommandInputShape;
};
export type SemanticDefinition = {
  readonly contractKind: "SEMANTIC_CORE_V1";
  readonly application: "ClaimCore";
  readonly scope: "trusted-local-operator-claims-register";
  readonly ruleSetVersion: 1;
  readonly canonicalCommandFormat: 2;
  readonly requestFingerprintVersion: 1;
  readonly recoveryEnvelopeFormat: 1;
  readonly defaultPageSize: 50;
  readonly maximumPageSize: 50;
  readonly requestByteLimit: 65536;
  readonly fields: ReadonlyArray<FieldDescriptor>;
  readonly commands: ReadonlyArray<CommandDescriptor>;
  readonly statuses: ReadonlyArray<"OPENED" | "CLOSED">;
  readonly rejectionDiagnostics: ReadonlyArray<
    | { readonly id: "INPUT_TEXT_REQUIRED"; readonly parameters: readonly [] }
    | { readonly id: "INPUT_MALFORMED_UNICODE"; readonly parameters: readonly [] }
    | { readonly id: "INPUT_SURROUNDING_WHITESPACE"; readonly parameters: readonly [] }
    | {
        readonly id: "INPUT_TEXT_TOO_SHORT";
        readonly parameters: readonly [
          { readonly name: "minimumCharacters"; readonly minimum: 0; readonly maximum: 2147483647 },
        ];
      }
    | {
        readonly id: "INPUT_TEXT_TOO_LONG";
        readonly parameters: readonly [
          { readonly name: "maximumCharacters"; readonly minimum: 0; readonly maximum: 2147483647 },
        ];
      }
    | { readonly id: "INPUT_CONTROL_CHARACTERS"; readonly parameters: readonly [] }
    | { readonly id: "INPUT_CALENDAR_DATE_REQUIRED"; readonly parameters: readonly [] }
    | { readonly id: "INPUT_DATE_ORDER"; readonly parameters: readonly [] }
    | { readonly id: "INPUT_FUTURE_DATE"; readonly parameters: readonly [] }
    | {
        readonly id: "INPUT_DECIMAL_FORMAT";
        readonly parameters: readonly [
          {
            readonly name: "maximumIntegerDigits";
            readonly minimum: 1;
            readonly maximum: 2147483647;
          },
          {
            readonly name: "maximumFractionalDigits";
            readonly minimum: 0;
            readonly maximum: 2147483647;
          },
        ];
      }
    | { readonly id: "INPUT_CURRENCY_FORMAT"; readonly parameters: readonly [] }
    | { readonly id: "INPUT_AMOUNT_NOT_REPRESENTABLE"; readonly parameters: readonly [] }
    | { readonly id: "COMMAND_OPERATION_ID_REQUIRED"; readonly parameters: readonly [] }
    | { readonly id: "COMMAND_EXPECTED_REVISION_RANGE"; readonly parameters: readonly [] }
    | { readonly id: "STATE_STORED_REVISION_RANGE"; readonly parameters: readonly [] }
    | { readonly id: "STATE_REVISION_EXHAUSTED"; readonly parameters: readonly [] }
    | { readonly id: "STATE_CASE_REFERENCE_MISMATCH"; readonly parameters: readonly [] }
    | { readonly id: "STATE_TRANSITION_MISMATCH"; readonly parameters: readonly [] }
    | { readonly id: "INPUT_EXACT_FIELDS_REQUIRED"; readonly parameters: readonly [] }
    | { readonly id: "COMMAND_GROUPED_CORRECTION_REQUIRED"; readonly parameters: readonly [] }
    | { readonly id: "CORRECTION_COMPLETE_DECISION_REQUIRED"; readonly parameters: readonly [] }
    | { readonly id: "CORRECTION_PAYMENT_CLEAR_REQUIRED"; readonly parameters: readonly [] }
    | {
        readonly id: "CORRECTION_PAYMENT_ACKNOWLEDGEMENT_REQUIRED";
        readonly parameters: readonly [];
      }
    | { readonly id: "CORRECTION_PAYMENT_DECISION_REQUIRED"; readonly parameters: readonly [] }
    | { readonly id: "CORRECTION_REPLACEMENT_FIELDS_REQUIRED"; readonly parameters: readonly [] }
    | { readonly id: "CORRECTION_REGISTRATION_CLEAR_FORBIDDEN"; readonly parameters: readonly [] }
    | { readonly id: "CASE_NOT_FOUND"; readonly parameters: readonly [] }
    | { readonly id: "CASE_ALREADY_EXISTS"; readonly parameters: readonly [] }
    | { readonly id: "CASE_REVISION_CONFLICT"; readonly parameters: readonly [] }
    | { readonly id: "CASE_CLOSED"; readonly parameters: readonly [] }
    | { readonly id: "CASE_ALREADY_CLOSED"; readonly parameters: readonly [] }
    | { readonly id: "CASE_ALREADY_OPENED"; readonly parameters: readonly [] }
    | { readonly id: "CASE_AMENDMENT_REQUIRES_UNDECIDED"; readonly parameters: readonly [] }
    | { readonly id: "CORRECTION_NO_CHANGES"; readonly parameters: readonly [] }
    | { readonly id: "CORRECTION_EXISTING_VALUE_REQUIRED"; readonly parameters: readonly [] }
    | { readonly id: "CASE_DECISION_REQUIRED"; readonly parameters: readonly [] }
    | { readonly id: "CASE_PAYMENT_ALREADY_RECORDED"; readonly parameters: readonly [] }
    | { readonly id: "CASE_PAYMENT_NOT_RECORDED"; readonly parameters: readonly [] }
    | { readonly id: "CASE_DECISION_ALREADY_PAID"; readonly parameters: readonly [] }
    | { readonly id: "CASE_ZERO_DECISION_CANNOT_BE_PAID"; readonly parameters: readonly [] }
    | {
        readonly id: "QUERY_PAGE_LIMIT_RANGE";
        readonly parameters: readonly [
          { readonly name: "maximumPageSize"; readonly minimum: 1; readonly maximum: 2147483647 },
        ];
      }
    | { readonly id: "QUERY_HISTORY_CURSOR_INVALID"; readonly parameters: readonly [] }
    | { readonly id: "OPERATION_CONTENT_CONFLICT"; readonly parameters: readonly [] }
    | { readonly id: "OPERATION_REVOKED"; readonly parameters: readonly [] }
    | { readonly id: "OPERATION_RECOVERY_ATTEMPT_LIMIT"; readonly parameters: readonly [] }
  >;
  readonly faultDiagnostics: ReadonlyArray<
    | { readonly id: "CORE_OPERATION_CONTENT_CONFLICT"; readonly parameters: readonly [] }
    | { readonly id: "CORE_STORE_UNAVAILABLE"; readonly parameters: readonly [] }
    | { readonly id: "CORE_COMMIT_OUTCOME_UNKNOWN"; readonly parameters: readonly [] }
    | { readonly id: "CORE_STORE_INTEGRITY_ERROR"; readonly parameters: readonly [] }
    | { readonly id: "CORE_SCHEMA_MISMATCH"; readonly parameters: readonly [] }
    | { readonly id: "RECOVERY_STORE_RESPONSE_INVALID"; readonly parameters: readonly [] }
    | { readonly id: "RECOVERY_STORE_CONTENT_CONFLICT"; readonly parameters: readonly [] }
    | { readonly id: "RECOVERY_STORE_PREPARATION_MISSING"; readonly parameters: readonly [] }
    | { readonly id: "RECOVERY_CAPACITY_EXHAUSTED"; readonly parameters: readonly [] }
    | { readonly id: "RECOVERY_SCHEMA_MISMATCH"; readonly parameters: readonly [] }
    | { readonly id: "RECOVERY_STORE_UNAVAILABLE"; readonly parameters: readonly [] }
    | { readonly id: "RECOVERY_STORE_INTEGRITY_ERROR"; readonly parameters: readonly [] }
    | { readonly id: "RECOVERY_READ_CANCELLED"; readonly parameters: readonly [] }
    | { readonly id: "RECOVERY_MUTATION_CANCELLED_BEFORE_COMMIT"; readonly parameters: readonly [] }
    | { readonly id: "RECOVERY_MUTATION_OUTCOME_UNKNOWN"; readonly parameters: readonly [] }
    | { readonly id: "RECOVERY_RETAINED_CANONICAL_INVALID"; readonly parameters: readonly [] }
    | { readonly id: "RECOVERY_RETAINED_DOMAIN_SHAPE_INVALID"; readonly parameters: readonly [] }
    | {
        readonly id: "RECOVERY_RETAINED_PREPARATION_UNVERIFIABLE";
        readonly parameters: readonly [];
      }
    | { readonly id: "RECOVERY_NEW_PREPARATION_MISSING"; readonly parameters: readonly [] }
    | {
        readonly id: "RECOVERY_PREPARATION_DISMISSED_BEFORE_EXECUTION";
        readonly parameters: readonly [];
      }
    | { readonly id: "RECOVERY_RETAINED_DIGEST_MISMATCH"; readonly parameters: readonly [] }
    | { readonly id: "RECOVERY_STORED_DATA_INVALID"; readonly parameters: readonly [] }
  >;
  readonly recoveryDiagnostics: ReadonlyArray<
    | { readonly id: "RECOVERY_OPERATION_ID_REQUIRED"; readonly parameters: readonly [] }
    | { readonly id: "RECOVERY_REQUEST_DIGEST_INVALID"; readonly parameters: readonly [] }
    | { readonly id: "RECOVERY_PAGE_LIMIT_RANGE"; readonly parameters: readonly [] }
    | { readonly id: "RECOVERY_LIST_CURSOR_INVALID"; readonly parameters: readonly [] }
    | { readonly id: "RECOVERY_LIST_CURSOR_VIEW_MISMATCH"; readonly parameters: readonly [] }
    | { readonly id: "RECOVERY_ATTEMPT_CURSOR_INVALID"; readonly parameters: readonly [] }
    | {
        readonly id: "RECOVERY_ATTEMPT_CURSOR_OPERATION_MISMATCH";
        readonly parameters: readonly [];
      }
    | { readonly id: "RECOVERY_DISMISSAL_CONFIRMATION_REQUIRED"; readonly parameters: readonly [] }
    | { readonly id: "RECOVERY_PREPARATION_NOT_FOUND"; readonly parameters: readonly [] }
    | { readonly id: "RECOVERY_OPERATION_CONTENT_CONFLICT"; readonly parameters: readonly [] }
    | { readonly id: "RECOVERY_PREPARATION_DISMISSED"; readonly parameters: readonly [] }
    | { readonly id: "RECOVERY_SUBMISSION_ALREADY_STARTED"; readonly parameters: readonly [] }
    | {
        readonly id: "RECOVERY_ACCEPTED_OPERATION_DISMISSAL_FORBIDDEN";
        readonly parameters: readonly [];
      }
    | { readonly id: "RECOVERY_SOURCE_DIGEST_MISMATCH"; readonly parameters: readonly [] }
    | { readonly id: "RECOVERY_REQUEST_DIGEST_MISMATCH"; readonly parameters: readonly [] }
    | { readonly id: "RECOVERY_INSTALLATION_MISMATCH"; readonly parameters: readonly [] }
    | { readonly id: "RECOVERY_ENVELOPE_INVALID_OR_UNSUPPORTED"; readonly parameters: readonly [] }
    | {
        readonly id: "RECOVERY_CANONICAL_RECORD_INVALID_OR_UNSUPPORTED";
        readonly parameters: readonly [];
      }
    | { readonly id: "RECOVERY_OPERATION_REVOKED"; readonly parameters: readonly [] }
    | { readonly id: "RECOVERY_ATTEMPT_LIMIT_REACHED"; readonly parameters: readonly [] }
  >;
  readonly rules: ReadonlyArray<{
    readonly identifier: string;
    readonly category: "CROSS_FIELD" | "TRANSITION";
    readonly meaning: string;
  }>;
};
