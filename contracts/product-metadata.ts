/* Generated from ClaimCore.Contracts. Do not edit. */
export const productMetadata = { descriptors: {
  "fields": [
    {
      "name": "incidentDate",
      "nativeName": "IncidentDate",
      "label": "Incident date",
      "meaning": "Date on which the incident occurred.",
      "allowsAbsence": false,
      "scalar": {
        "kind": "CALENDAR_DATE",
        "exactFormat": "yyyy-MM-dd",
        "minimum": "0001-01-01",
        "maximum": "9999-12-31"
      }
    },
    {
      "name": "incidentNotificationDate",
      "nativeName": "IncidentNotificationDate",
      "label": "Incident notification date (FNOL)",
      "meaning": "Date when this claims handler was first notified of the incident by anyone, not necessarily the claimant. Not the date another insurer or organisation was notified.",
      "allowsAbsence": false,
      "scalar": {
        "kind": "CALENDAR_DATE",
        "exactFormat": "yyyy-MM-dd",
        "minimum": "0001-01-01",
        "maximum": "9999-12-31"
      }
    },
    {
      "name": "incidentCountry",
      "nativeName": "IncidentCountry",
      "label": "Country of incident (ISO alpha-2 code)",
      "meaning": "Two uppercase ASCII letters identifying the incident country; ISO alpha-2 syntax only, without membership validation. ZZ means unknown or international.",
      "allowsAbsence": false,
      "scalar": {
        "kind": "TEXT",
        "format": {
          "minimumCharacters": 1,
          "grammar": "[A-Z]{2}"
        },
        "policy": null
      }
    },
    {
      "name": "claimantName",
      "nativeName": "ClaimantName",
      "label": "Claimant name",
      "meaning": "Name of the person or company making the claim.",
      "allowsAbsence": false,
      "scalar": {
        "kind": "TEXT",
        "format": {
          "minimumCharacters": 1,
          "grammar": null
        },
        "policy": {
          "maximumCharacters": 200,
          "requiresNonBlank": true,
          "rejectsSurroundingWhitespace": true,
          "rejectsControlCharacters": true,
          "rejectsForbiddenInvisibleCharacters": true
        }
      }
    },
    {
      "name": "insurerName",
      "nativeName": "InsurerName",
      "label": "Responsible insurer",
      "meaning": "Insurer recorded by the claims handler as responsible for this claim.",
      "allowsAbsence": false,
      "scalar": {
        "kind": "TEXT",
        "format": {
          "minimumCharacters": 1,
          "grammar": null
        },
        "policy": {
          "maximumCharacters": 200,
          "requiresNonBlank": true,
          "rejectsSurroundingWhitespace": true,
          "rejectsControlCharacters": true,
          "rejectsForbiddenInvisibleCharacters": true
        }
      }
    },
    {
      "name": "claimedAmount",
      "nativeName": "ClaimedAmount",
      "label": "Amount claimed",
      "meaning": "Amount requested by the claimant.",
      "allowsAbsence": false,
      "scalar": {
        "kind": "AMOUNT",
        "grammar": "(0|[1-9][0-9]{0,17})(\\.[0-9]{1,4})?",
        "maximumIntegerDigits": 18,
        "maximumFractionalDigits": 4
      }
    },
    {
      "name": "claimedCurrency",
      "nativeName": "ClaimedCurrency",
      "label": "Currency of claimed amount",
      "meaning": "Currency in which the amount is claimed.",
      "allowsAbsence": false,
      "scalar": {
        "kind": "CURRENCY",
        "grammar": "[A-Z]{3}",
        "exactCharacters": 3
      }
    },
    {
      "name": "caseReference",
      "nativeName": "CaseReference",
      "label": "Handler\u0027s case reference",
      "meaning": "Reference assigned by the claims handler operating this register, not an insurer reference. Unique and immutable within one installation; printable non-space ASCII with case-insensitive identity and authored spelling retained.",
      "allowsAbsence": false,
      "scalar": {
        "kind": "TEXT",
        "format": {
          "minimumCharacters": 1,
          "grammar": "[!-~]{1,80}"
        },
        "policy": null
      }
    },
    {
      "name": "paymentDecisionDate",
      "nativeName": "PaymentDecisionDate",
      "label": "Payment decision date",
      "meaning": "Date when this claims handler decided the amount to be paid. Not recorded until a decision is made.",
      "allowsAbsence": true,
      "scalar": {
        "kind": "CALENDAR_DATE",
        "exactFormat": "yyyy-MM-dd",
        "minimum": "0001-01-01",
        "maximum": "9999-12-31"
      }
    },
    {
      "name": "payableAmount",
      "nativeName": "PayableAmount",
      "label": "Amount to be paid",
      "meaning": "Amount the handler decided should be paid. Not recorded until decided; may differ from the claimed amount.",
      "allowsAbsence": true,
      "scalar": {
        "kind": "AMOUNT",
        "grammar": "(0|[1-9][0-9]{0,17})(\\.[0-9]{1,4})?",
        "maximumIntegerDigits": 18,
        "maximumFractionalDigits": 4
      }
    },
    {
      "name": "payableCurrency",
      "nativeName": "PayableCurrency",
      "label": "Currency of amount to be paid",
      "meaning": "Currency of the amount to be paid. Not recorded until decided; may differ from the claimed currency.",
      "allowsAbsence": true,
      "scalar": {
        "kind": "CURRENCY",
        "grammar": "[A-Z]{3}",
        "exactCharacters": 3
      }
    },
    {
      "name": "paymentDate",
      "nativeName": "PaymentDate",
      "label": "Payment date",
      "meaning": "Date when the full decided amount was paid, as recorded by the handler. Leave unrecorded if unpaid or unknown. Recording payment does not make or verify a bank transfer.",
      "allowsAbsence": true,
      "scalar": {
        "kind": "CALENDAR_DATE",
        "exactFormat": "yyyy-MM-dd",
        "minimum": "0001-01-01",
        "maximum": "9999-12-31"
      }
    },
    {
      "name": "status",
      "nativeName": "Status",
      "label": "Case status",
      "meaning": "Whether the case is open or closed. Closing a case does not mean that payment was made.",
      "allowsAbsence": false,
      "scalar": {
        "kind": "CASE_STATUS",
        "allowedValues": [
          "OPENED",
          "CLOSED"
        ]
      }
    }
  ],
  "commands": [
    {
      "kind": "OPEN",
      "label": "Open a case",
      "meaning": "Register the incident, claimant, insurer and amount claimed.",
      "labelId": "command.OPEN.label",
      "meaningId": "command.OPEN.meaning",
      "valuesMember": "values",
      "inputs": {
        "kind": "FIELDS",
        "fields": [
          {
            "fieldName": "incidentDate",
            "prefill": "BLANK"
          },
          {
            "fieldName": "incidentNotificationDate",
            "prefill": "BLANK"
          },
          {
            "fieldName": "incidentCountry",
            "prefill": "BLANK"
          },
          {
            "fieldName": "claimantName",
            "prefill": "BLANK"
          },
          {
            "fieldName": "insurerName",
            "prefill": "BLANK"
          },
          {
            "fieldName": "claimedAmount",
            "prefill": "BLANK"
          },
          {
            "fieldName": "claimedCurrency",
            "prefill": "BLANK"
          }
        ]
      }
    },
    {
      "kind": "AMEND_REGISTRATION",
      "label": "Amend registration",
      "meaning": "Replace the initial facts. The handler\u0027s case reference remains unchanged.",
      "labelId": "command.AMEND_REGISTRATION.label",
      "meaningId": "command.AMEND_REGISTRATION.meaning",
      "valuesMember": "values",
      "inputs": {
        "kind": "FIELDS",
        "fields": [
          {
            "fieldName": "incidentDate",
            "prefill": "CURRENT_FIELD",
            "currentField": "incidentDate"
          },
          {
            "fieldName": "incidentNotificationDate",
            "prefill": "CURRENT_FIELD",
            "currentField": "incidentNotificationDate"
          },
          {
            "fieldName": "incidentCountry",
            "prefill": "CURRENT_FIELD",
            "currentField": "incidentCountry"
          },
          {
            "fieldName": "claimantName",
            "prefill": "CURRENT_FIELD",
            "currentField": "claimantName"
          },
          {
            "fieldName": "insurerName",
            "prefill": "CURRENT_FIELD",
            "currentField": "insurerName"
          },
          {
            "fieldName": "claimedAmount",
            "prefill": "CURRENT_FIELD",
            "currentField": "claimedAmount"
          },
          {
            "fieldName": "claimedCurrency",
            "prefill": "CURRENT_FIELD",
            "currentField": "claimedCurrency"
          }
        ]
      }
    },
    {
      "kind": "CORRECT_CASE",
      "label": "Correct case facts",
      "meaning": "Correct recorded details on a case with a decision, payment or closed status. All changes are recorded together. Clearing a payment date does not refund money.",
      "labelId": "command.CORRECT_CASE.label",
      "meaningId": "command.CORRECT_CASE.meaning",
      "valuesMember": "values",
      "inputs": {
        "kind": "CORRECTION_GROUPS",
        "groups": [
          {
            "name": "registration",
            "label": "Registration",
            "meaning": "Keep the current registration details or replace all seven details together.",
            "actions": [
              "KEEP",
              "REPLACE"
            ],
            "replaceFields": [
              {
                "fieldName": "incidentDate",
                "prefill": "CURRENT_FIELD",
                "currentField": "incidentDate"
              },
              {
                "fieldName": "incidentNotificationDate",
                "prefill": "CURRENT_FIELD",
                "currentField": "incidentNotificationDate"
              },
              {
                "fieldName": "incidentCountry",
                "prefill": "CURRENT_FIELD",
                "currentField": "incidentCountry"
              },
              {
                "fieldName": "claimantName",
                "prefill": "CURRENT_FIELD",
                "currentField": "claimantName"
              },
              {
                "fieldName": "insurerName",
                "prefill": "CURRENT_FIELD",
                "currentField": "insurerName"
              },
              {
                "fieldName": "claimedAmount",
                "prefill": "CURRENT_FIELD",
                "currentField": "claimedAmount"
              },
              {
                "fieldName": "claimedCurrency",
                "prefill": "CURRENT_FIELD",
                "currentField": "claimedCurrency"
              }
            ]
          },
          {
            "name": "decision",
            "label": "Payment decision",
            "meaning": "Keep the current payment decision, replace its date, amount and currency together, or clear the decision.",
            "actions": [
              "KEEP",
              "REPLACE",
              "CLEAR"
            ],
            "replaceFields": [
              {
                "fieldName": "paymentDecisionDate",
                "prefill": "CURRENT_FIELD",
                "currentField": "paymentDecisionDate"
              },
              {
                "fieldName": "payableAmount",
                "prefill": "CURRENT_FIELD",
                "currentField": "payableAmount"
              },
              {
                "fieldName": "payableCurrency",
                "prefill": "CURRENT_FIELD",
                "currentField": "payableCurrency"
              }
            ]
          },
          {
            "name": "payment",
            "label": "Payment record",
            "meaning": "Keep the current payment date, replace it, or clear the recorded date. Clearing the date does not refund money.",
            "actions": [
              "KEEP",
              "REPLACE",
              "CLEAR"
            ],
            "replaceFields": [
              {
                "fieldName": "paymentDate",
                "prefill": "CURRENT_FIELD",
                "currentField": "paymentDate"
              }
            ]
          }
        ]
      }
    },
    {
      "kind": "DECIDE",
      "label": "Record payment decision",
      "meaning": "Record or replace the decided amount and currency. This does not send money.",
      "labelId": "command.DECIDE.label",
      "meaningId": "command.DECIDE.meaning",
      "valuesMember": "values",
      "inputs": {
        "kind": "FIELDS",
        "fields": [
          {
            "fieldName": "paymentDecisionDate",
            "prefill": "CURRENT_FIELD",
            "currentField": "paymentDecisionDate"
          },
          {
            "fieldName": "payableAmount",
            "prefill": "CURRENT_FIELD",
            "currentField": "payableAmount"
          },
          {
            "fieldName": "payableCurrency",
            "prefill": "CURRENT_FIELD",
            "currentField": "payableCurrency"
          }
        ]
      }
    },
    {
      "kind": "WITHDRAW_DECISION",
      "label": "Withdraw payment decision",
      "meaning": "Clear the unpaid decision while preserving its history.",
      "labelId": "command.WITHDRAW_DECISION.label",
      "meaningId": "command.WITHDRAW_DECISION.meaning",
      "valuesMember": "values",
      "inputs": {
        "kind": "FIELDS",
        "fields": []
      }
    },
    {
      "kind": "RECORD_PAYMENT",
      "label": "Record actual payment",
      "meaning": "Record the date the full decided amount was actually paid. This does not execute or verify a transfer.",
      "labelId": "command.RECORD_PAYMENT.label",
      "meaningId": "command.RECORD_PAYMENT.meaning",
      "valuesMember": "values",
      "inputs": {
        "kind": "FIELDS",
        "fields": [
          {
            "fieldName": "paymentDate",
            "prefill": "BLANK"
          }
        ]
      }
    },
    {
      "kind": "CLEAR_PAYMENT",
      "label": "Correct an erroneous payment record",
      "meaning": "Remove an erroneously recorded payment date. This is not a refund or reversal of an actual transfer. Earlier history remains.",
      "labelId": "command.CLEAR_PAYMENT.label",
      "meaningId": "command.CLEAR_PAYMENT.meaning",
      "valuesMember": "values",
      "inputs": {
        "kind": "FIELDS",
        "fields": []
      }
    },
    {
      "kind": "CLOSE",
      "label": "Close the case",
      "meaning": "Close the case. Closing does not mean that payment was made.",
      "labelId": "command.CLOSE.label",
      "meaningId": "command.CLOSE.meaning",
      "valuesMember": "values",
      "inputs": {
        "kind": "FIELDS",
        "fields": []
      }
    },
    {
      "kind": "REOPEN",
      "label": "Reopen the case",
      "meaning": "Reopen the case, preserving its details and history.",
      "labelId": "command.REOPEN.label",
      "meaningId": "command.REOPEN.meaning",
      "valuesMember": "values",
      "inputs": {
        "kind": "FIELDS",
        "fields": []
      }
    },
    {
      "kind": "VOID",
      "label": "Void case record",
      "meaning": "Block business commands while retaining the case and its history.",
      "labelId": "command.VOID.label",
      "meaningId": "command.VOID.meaning",
      "valuesMember": null,
      "inputs": {
        "kind": "FIELDS",
        "fields": [
          {
            "fieldName": "reason",
            "prefill": "BLANK"
          }
        ]
      }
    },
    {
      "kind": "REINSTATE",
      "label": "Reinstate case record",
      "meaning": "Restore ordinary case work on a voided record.",
      "labelId": "command.REINSTATE.label",
      "meaningId": "command.REINSTATE.meaning",
      "valuesMember": null,
      "inputs": {
        "kind": "FIELDS",
        "fields": [
          {
            "fieldName": "reason",
            "prefill": "BLANK"
          }
        ]
      }
    },
    {
      "kind": "ERASE",
      "label": "Erase case payload",
      "meaning": "Permanently remove business payload from the live database. WAL, backups and copies elsewhere are not erased.",
      "labelId": "command.ERASE.label",
      "meaningId": "command.ERASE.meaning",
      "valuesMember": null,
      "inputs": {
        "kind": "FIELDS",
        "fields": []
      }
    }
  ],
  "inputFields": [
    {
      "name": "reason",
      "label": "Stewardship reason",
      "meaning": "Explain the record stewardship action.",
      "scalar": {
        "kind": "TEXT",
        "format": {
          "minimumCharacters": 1,
          "grammar": null
        },
        "policy": {
          "maximumCharacters": 200,
          "requiresNonBlank": true,
          "rejectsSurroundingWhitespace": true,
          "rejectsControlCharacters": true,
          "rejectsForbiddenInvisibleCharacters": true
        }
      }
    }
  ],
  "authorityTarget": {
    "fieldName": "target",
    "label": "Actor principal",
    "primitive": "PRINCIPAL",
    "path": [
      "target"
    ]
  },
  "authorityCommands": [
    {
      "kind": "REGISTER_ACTOR",
      "label": "Register actor",
      "meaning": "Register a principal without granting case or administration capabilities.",
      "labelId": "authority.REGISTER_ACTOR.label",
      "meaningId": "authority.REGISTER_ACTOR.meaning",
      "inputs": []
    },
    {
      "kind": "SET_GRANT",
      "label": "Set actor grant",
      "meaning": "Grant or revoke a role at installation or case scope.",
      "labelId": "authority.SET_GRANT.label",
      "meaningId": "authority.SET_GRANT.meaning",
      "inputs": [
        {
          "fieldName": "role",
          "label": "Role",
          "primitive": "ROLE",
          "path": [
            "act",
            "role"
          ]
        },
        {
          "fieldName": "scope",
          "label": "Grant scope",
          "primitive": "SCOPE",
          "path": [
            "act",
            "scope"
          ]
        },
        {
          "fieldName": "active",
          "label": "Grant active",
          "primitive": "BOOLEAN",
          "path": [
            "act",
            "active"
          ]
        }
      ]
    },
    {
      "kind": "SET_ENABLED",
      "label": "Set actor enabled",
      "meaning": "Enable or disable an actor. The final enabled human owner remains protected.",
      "labelId": "authority.SET_ENABLED.label",
      "meaningId": "authority.SET_ENABLED.meaning",
      "inputs": [
        {
          "fieldName": "enabled",
          "label": "Actor enabled",
          "primitive": "BOOLEAN",
          "path": [
            "act",
            "enabled"
          ]
        }
      ]
    }
  ],
  "authorityFields": [
    {
      "name": "operationId",
      "label": "Operation ID",
      "labelId": "authority.field.operationId.label"
    },
    {
      "name": "expectedRevision",
      "label": "Expected revision",
      "labelId": "authority.field.expectedRevision.label"
    },
    {
      "name": "act",
      "label": "Operation",
      "labelId": "authority.field.act.label"
    },
    {
      "name": "kind",
      "label": "Type",
      "labelId": "authority.field.kind.label"
    },
    {
      "name": "issuer",
      "label": "Identity issuer",
      "labelId": "authority.field.issuer.label"
    },
    {
      "name": "subject",
      "label": "Subject or client ID",
      "labelId": "authority.field.subject.label"
    },
    {
      "name": "target",
      "label": "Actor principal",
      "labelId": "authority.field.target.label"
    },
    {
      "name": "role",
      "label": "Role",
      "labelId": "authority.field.role.label"
    },
    {
      "name": "scope",
      "label": "Grant scope",
      "labelId": "authority.field.scope.label"
    },
    {
      "name": "active",
      "label": "Grant active",
      "labelId": "authority.field.active.label"
    },
    {
      "name": "enabled",
      "label": "Actor enabled",
      "labelId": "authority.field.enabled.label"
    }
  ],
  "statuses": [
    "OPENED",
    "CLOSED"
  ],
  "rejectionDiagnostics": [
    {
      "id": "INPUT_TEXT_REQUIRED",
      "parameters": []
    },
    {
      "id": "INPUT_MALFORMED_UNICODE",
      "parameters": []
    },
    {
      "id": "INPUT_SURROUNDING_WHITESPACE",
      "parameters": []
    },
    {
      "id": "INPUT_TEXT_TOO_SHORT",
      "parameters": [
        {
          "name": "minimumCharacters",
          "minimum": 0,
          "maximum": 2147483647
        }
      ]
    },
    {
      "id": "INPUT_TEXT_TOO_LONG",
      "parameters": [
        {
          "name": "maximumCharacters",
          "minimum": 0,
          "maximum": 2147483647
        }
      ]
    },
    {
      "id": "INPUT_CONTROL_CHARACTERS",
      "parameters": []
    },
    {
      "id": "TEXT_FORMATTING_CHARACTER",
      "parameters": []
    },
    {
      "id": "INPUT_CASE_REFERENCE_FORMAT",
      "parameters": []
    },
    {
      "id": "INPUT_COUNTRY_FORMAT",
      "parameters": []
    },
    {
      "id": "INPUT_CALENDAR_DATE_REQUIRED",
      "parameters": []
    },
    {
      "id": "INPUT_DATE_ORDER",
      "parameters": []
    },
    {
      "id": "INPUT_FUTURE_DATE",
      "parameters": []
    },
    {
      "id": "INPUT_DECIMAL_FORMAT",
      "parameters": [
        {
          "name": "maximumIntegerDigits",
          "minimum": 1,
          "maximum": 2147483647
        },
        {
          "name": "maximumFractionalDigits",
          "minimum": 0,
          "maximum": 2147483647
        }
      ]
    },
    {
      "id": "INPUT_CURRENCY_FORMAT",
      "parameters": []
    },
    {
      "id": "INPUT_AMOUNT_NOT_REPRESENTABLE",
      "parameters": []
    },
    {
      "id": "COMMAND_OPERATION_ID_REQUIRED",
      "parameters": []
    },
    {
      "id": "COMMAND_EXPECTED_REVISION_RANGE",
      "parameters": []
    },
    {
      "id": "STATE_STORED_REVISION_RANGE",
      "parameters": []
    },
    {
      "id": "REVISION_EXHAUSTED",
      "parameters": []
    },
    {
      "id": "STATE_CASE_REFERENCE_MISMATCH",
      "parameters": []
    },
    {
      "id": "STATE_TRANSITION_MISMATCH",
      "parameters": []
    },
    {
      "id": "INPUT_EXACT_FIELDS_REQUIRED",
      "parameters": []
    },
    {
      "id": "COMMAND_GROUPED_CORRECTION_REQUIRED",
      "parameters": []
    },
    {
      "id": "CORRECTION_COMPLETE_DECISION_REQUIRED",
      "parameters": []
    },
    {
      "id": "CORRECTION_PAYMENT_CLEAR_REQUIRED",
      "parameters": []
    },
    {
      "id": "CORRECTION_PAYMENT_ACKNOWLEDGEMENT_REQUIRED",
      "parameters": []
    },
    {
      "id": "CORRECTION_PAYMENT_DECISION_REQUIRED",
      "parameters": []
    },
    {
      "id": "CORRECTION_REPLACEMENT_FIELDS_REQUIRED",
      "parameters": []
    },
    {
      "id": "CORRECTION_REGISTRATION_CLEAR_FORBIDDEN",
      "parameters": []
    },
    {
      "id": "CASE_NOT_FOUND",
      "parameters": []
    },
    {
      "id": "CASE_ALREADY_EXISTS",
      "parameters": []
    },
    {
      "id": "CASE_REVISION_CONFLICT",
      "parameters": []
    },
    {
      "id": "CASE_CLOSED",
      "parameters": []
    },
    {
      "id": "CASE_ALREADY_CLOSED",
      "parameters": []
    },
    {
      "id": "CASE_ALREADY_OPENED",
      "parameters": []
    },
    {
      "id": "CASE_AMENDMENT_REQUIRES_UNDECIDED",
      "parameters": []
    },
    {
      "id": "CORRECTION_NO_CHANGES",
      "parameters": []
    },
    {
      "id": "CORRECTION_EXISTING_VALUE_REQUIRED",
      "parameters": []
    },
    {
      "id": "CASE_DECISION_REQUIRED",
      "parameters": []
    },
    {
      "id": "CASE_PAYMENT_ALREADY_RECORDED",
      "parameters": []
    },
    {
      "id": "CASE_PAYMENT_NOT_RECORDED",
      "parameters": []
    },
    {
      "id": "CASE_DECISION_ALREADY_PAID",
      "parameters": []
    },
    {
      "id": "CASE_ZERO_DECISION_CANNOT_BE_PAID",
      "parameters": []
    },
    {
      "id": "QUERY_PAGE_LIMIT_RANGE",
      "parameters": [
        {
          "name": "maximumPageSize",
          "minimum": 1,
          "maximum": 2147483647
        }
      ]
    },
    {
      "id": "QUERY_CURSOR_INVALID",
      "parameters": []
    },
    {
      "id": "ACT_NOT_PERMITTED",
      "parameters": []
    },
    {
      "id": "LEDGER_SEQUENCE_EXHAUSTED",
      "parameters": []
    },
    {
      "id": "OPERATION_CONTENT_CONFLICT",
      "parameters": []
    },
    {
      "id": "ACCESS_RESOURCE_UNAVAILABLE",
      "parameters": []
    },
    {
      "id": "CASE_VOIDED",
      "parameters": []
    },
    {
      "id": "CASE_ERASED",
      "parameters": []
    },
    {
      "id": "ALREADY_VOIDED",
      "parameters": []
    },
    {
      "id": "NOT_VOIDED",
      "parameters": []
    },
    {
      "id": "LAST_OWNER",
      "parameters": []
    },
    {
      "id": "ACTOR_ALREADY_REGISTERED",
      "parameters": []
    },
    {
      "id": "ACTOR_UNKNOWN",
      "parameters": []
    },
    {
      "id": "HUMAN_PRINCIPAL_REQUIRED",
      "parameters": []
    }
  ],
  "faultDiagnostics": [
    {
      "id": "CORE_STORE_UNAVAILABLE",
      "parameters": []
    },
    {
      "id": "CORE_STORE_INTEGRITY_ERROR",
      "parameters": []
    },
    {
      "id": "CORE_SCHEMA_MISMATCH",
      "parameters": []
    },
    {
      "id": "CORE_OPERATION_CONTENT_CONFLICT",
      "parameters": []
    }
  ],
  "roles": [
    {
      "role": "OWNER",
      "capabilities": [
        "MANAGE_AUTHORITY",
        "READ_AUTHORITY"
      ]
    },
    {
      "role": "CASE_READER",
      "capabilities": [
        "READ_CASE",
        "READ_HISTORY"
      ]
    },
    {
      "role": "CASE_REGISTRAR",
      "capabilities": [
        "READ_CASE",
        "READ_HISTORY",
        "REGISTER"
      ]
    },
    {
      "role": "CASE_HANDLER",
      "capabilities": [
        "READ_CASE",
        "READ_HISTORY",
        "READ_HISTORY_FULL",
        "REGISTER",
        "DECIDE",
        "CLOSE"
      ]
    },
    {
      "role": "PAYMENT_RECORDER",
      "capabilities": [
        "READ_CASE",
        "READ_HISTORY",
        "RECORD_PAYMENT"
      ]
    },
    {
      "role": "DATA_STEWARD",
      "capabilities": [
        "READ_CASE",
        "READ_HISTORY",
        "STEWARD"
      ]
    },
    {
      "role": "AUDITOR",
      "capabilities": [
        "READ_CASE",
        "READ_HISTORY",
        "READ_HISTORY_FULL",
        "READ_AUTHORITY"
      ]
    }
  ],
  "limits": {
    "defaultPageSize": 50,
    "maximumPageSize": 50,
    "requestByteLimit": 65536
  }
}, productVersion: "0.9.0", wireFingerprint: "af898b414e939d6a996a6944c8f9c9dcfa6816e45d6ed222c15db5d9707244df" } as const;
