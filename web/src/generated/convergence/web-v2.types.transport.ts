/* Generated from ClaimCore.Contracts. Do not edit. */
export type HostFailure =
  | {
      readonly kind: "HOST_FAILURE";
      readonly code: "WEB_CONNECTION_REJECTED";
      readonly status: 403;
      readonly message: string;
      readonly diagnostic: {
        readonly id: "WEB_HOST_CONNECTION_REJECTED";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly executionPhase: null;
    }
  | {
      readonly kind: "HOST_FAILURE";
      readonly code: "WEB_ORIGIN_REJECTED";
      readonly status: 403;
      readonly message: string;
      readonly diagnostic: {
        readonly id: "WEB_HOST_ORIGIN_REJECTED";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly executionPhase: null;
    }
  | {
      readonly kind: "HOST_FAILURE";
      readonly code: "WEB_MEDIA_TYPE";
      readonly status: 415;
      readonly message: string;
      readonly diagnostic: {
        readonly id: "WEB_HOST_MEDIA_TYPE_REJECTED";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly executionPhase: null;
    }
  | {
      readonly kind: "HOST_FAILURE";
      readonly code: "WEB_BODY_TOO_LARGE";
      readonly status: 413;
      readonly message: string;
      readonly diagnostic: {
        readonly id: "WEB_HOST_BODY_TOO_LARGE" | "WEB_INPUT_BODY_TOO_LARGE";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly executionPhase: null;
    }
  | {
      readonly kind: "HOST_FAILURE";
      readonly code: "WEB_SESSION_REJECTED";
      readonly status: 401;
      readonly message: string;
      readonly diagnostic: {
        readonly id: "WEB_HOST_SESSION_REJECTED";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly executionPhase: null;
    }
  | {
      readonly kind: "HOST_FAILURE";
      readonly code: "WEB_CSRF_REJECTED";
      readonly status: 403;
      readonly message: string;
      readonly diagnostic: {
        readonly id: "WEB_HOST_ANTIFORGERY_REJECTED";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly executionPhase: null;
    }
  | {
      readonly kind: "HOST_FAILURE";
      readonly code: "WEB_LOGIN_REJECTED";
      readonly status: 401;
      readonly message: string;
      readonly diagnostic: {
        readonly id: "WEB_HOST_LOGIN_REJECTED";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly executionPhase: "NOT_STARTED";
    }
  | {
      readonly kind: "HOST_FAILURE";
      readonly code: "WEB_NOT_FOUND";
      readonly status: 404;
      readonly message: string;
      readonly diagnostic: {
        readonly id: "WEB_HOST_ENDPOINT_MISSING";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly executionPhase: null;
    }
  | {
      readonly kind: "HOST_FAILURE";
      readonly code: "WEB_BUSY";
      readonly status: 429;
      readonly message: string;
      readonly diagnostic: {
        readonly id: "WEB_HOST_BUSY";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly executionPhase: null;
    }
  | {
      readonly kind: "HOST_FAILURE";
      readonly code: "WEB_PROTOCOL";
      readonly status: 500;
      readonly message: string;
      readonly diagnostic: {
        readonly id: "WEB_HOST_EXPORT_METADATA_INVALID";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly executionPhase: null;
    }
  | {
      readonly kind: "HOST_FAILURE";
      readonly code: "WEB_INTERNAL";
      readonly status: 500;
      readonly message: string;
      readonly diagnostic: {
        readonly id: "WEB_HOST_BEFORE_DISPATCH_FAILED";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly executionPhase: "NOT_STARTED";
    }
  | {
      readonly kind: "HOST_FAILURE";
      readonly code: "WEB_INTERNAL";
      readonly status: 500;
      readonly message: string;
      readonly diagnostic: {
        readonly id: "WEB_HOST_DISPATCH_UNCONFIRMED";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly executionPhase: "STARTED_UNCONFIRMED";
    }
  | {
      readonly kind: "HOST_FAILURE";
      readonly code: "WEB_RESPONSE_FAILED";
      readonly status: 500;
      readonly message: string;
      readonly diagnostic: {
        readonly id: "WEB_HOST_COMPLETED_RESPONSE_FAILED";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly executionPhase: null;
    }
  | {
      readonly kind: "HOST_FAILURE";
      readonly code: "WEB_SESSION_REJECTED";
      readonly status: 403;
      readonly message: string;
      readonly diagnostic: {
        readonly id: "WEB_HOST_SESSION_FORBIDDEN";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly executionPhase: null;
    }
  | {
      readonly kind: "HOST_FAILURE";
      readonly code: "WEB_METHOD_REJECTED";
      readonly status: 405;
      readonly message: string;
      readonly diagnostic: {
        readonly id: "WEB_HOST_METHOD_REJECTED";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly executionPhase: "NOT_STARTED";
    }
  | {
      readonly kind: "HOST_FAILURE";
      readonly code: "WEB_INVALID_REQUEST";
      readonly status: 400;
      readonly message: string;
      readonly diagnostic: {
        readonly id:
          | "WEB_INPUT_EXPECTED_OBJECT"
          | "WEB_INPUT_DUPLICATE_PROPERTY"
          | "WEB_INPUT_UNKNOWN_PROPERTY"
          | "WEB_INPUT_MISSING_PROPERTY"
          | "WEB_INPUT_EXPECTED_STRING"
          | "WEB_INPUT_NULL_FORBIDDEN"
          | "WEB_INPUT_EXPECTED_BOOLEAN"
          | "WEB_INPUT_EXPECTED_INTEGER"
          | "WEB_INPUT_INVALID_REVISION"
          | "WEB_INPUT_INVALID_UUID"
          | "WEB_INPUT_INVALID_DIGEST"
          | "WEB_INPUT_INVALID_UNICODE"
          | "WEB_INPUT_INVALID_UTF8"
          | "WEB_INPUT_INVALID_JSON"
          | "WEB_INPUT_BODY_UNREADABLE"
          | "WEB_INPUT_BODY_CANCELLED"
          | "WEB_INPUT_UNKNOWN_COMMAND"
          | "WEB_INPUT_MISSING_COMMAND_VALUE"
          | "WEB_INPUT_CORRECTION_ACTION"
          | "WEB_INPUT_LOGOUT_SHAPE"
          | "WEB_INPUT_PAGE_RANGE"
          | "WEB_INPUT_HISTORY_DETAIL"
          | "WEB_INPUT_RECOVERY_VIEW"
          | "WEB_INPUT_SOURCE_DIGEST_HEADER";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly executionPhase: "NOT_STARTED";
    };
