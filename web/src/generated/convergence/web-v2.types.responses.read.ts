/* Generated from ClaimCore.Contracts. Do not edit. */
import type {
  CaseSummary,
  CurrentCase,
  DefinitionPayload,
  Fault,
  HistoryEntry,
  Receipt,
  Rejection,
  SessionSnapshot,
} from "./web-v2.types.core";

export type WebV2ReadResponseByEndpoint = {
  readonly session: {
    readonly endpoint: "session";
    readonly outcome: { readonly tag: "SNAPSHOT"; readonly data: SessionSnapshot };
  };
  readonly "session.login": {
    readonly endpoint: "session.login";
    readonly outcome: { readonly tag: "SNAPSHOT"; readonly data: SessionSnapshot };
  };
  readonly "session.logout": {
    readonly endpoint: "session.logout";
    readonly outcome: { readonly tag: "SNAPSHOT"; readonly data: SessionSnapshot };
  };
  readonly definition: {
    readonly endpoint: "definition";
    readonly outcome: { readonly tag: "DESCRIBED"; readonly data: DefinitionPayload };
  };
  readonly "case.get": {
    readonly endpoint: "case.get";
    readonly outcome:
      | {
          readonly tag: "SUCCEEDED";
          readonly data:
            | { readonly tag: "FOUND"; readonly current: CurrentCase }
            | { readonly tag: "NOT_FOUND"; readonly caseReference: string };
        }
      | { readonly tag: "REJECTED"; readonly data: Rejection }
      | { readonly tag: "FAILED"; readonly data: Fault }
      | { readonly tag: "CANCELLED"; readonly data: null };
  };
  readonly "case.list": {
    readonly endpoint: "case.list";
    readonly outcome:
      | {
          readonly tag: "SUCCEEDED";
          readonly data: {
            readonly items: ReadonlyArray<CaseSummary>;
            readonly nextCursor: string | null;
          };
        }
      | { readonly tag: "REJECTED"; readonly data: Rejection }
      | { readonly tag: "FAILED"; readonly data: Fault }
      | { readonly tag: "CANCELLED"; readonly data: null };
  };
  readonly "case.history": {
    readonly endpoint: "case.history";
    readonly outcome:
      | {
          readonly tag: "SUCCEEDED";
          readonly data:
            | {
                readonly tag: "FOUND";
                readonly entries: ReadonlyArray<HistoryEntry>;
                readonly nextCursor: string | null;
              }
            | { readonly tag: "NOT_FOUND"; readonly caseReference: string };
        }
      | { readonly tag: "REJECTED"; readonly data: Rejection }
      | { readonly tag: "FAILED"; readonly data: Fault }
      | { readonly tag: "CANCELLED"; readonly data: null };
  };
  readonly "operation.observe": {
    readonly endpoint: "operation.observe";
    readonly outcome:
      | {
          readonly tag: "SUCCEEDED";
          readonly data:
            | { readonly tag: "FOUND"; readonly receipt: Receipt }
            | { readonly tag: "NOT_FOUND"; readonly operationId: string };
        }
      | { readonly tag: "REJECTED"; readonly data: Rejection }
      | { readonly tag: "FAILED"; readonly data: Fault }
      | { readonly tag: "CANCELLED"; readonly data: null };
  };
};
