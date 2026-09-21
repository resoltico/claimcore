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
  readonly rules: ReadonlyArray<{
    readonly identifier: string;
    readonly category: "CROSS_FIELD" | "TRANSITION";
    readonly meaning: string;
  }>;
};
