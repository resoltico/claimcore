import type {
  HostFailure,
  RequestByEndpoint,
  ResponseByEndpoint,
  SemanticDefinition,
} from "../generated/contracts/types";
import type { Notice } from "./notices";
export type {
  HostFailure,
  CaseFields,
  CaseView,
  Receipt,
  HistoryEntry,
  Review,
  Rejection,
  Fault,
  SemanticDefinition,
} from "../generated/contracts/types";
export type ServiceResponse<K extends keyof ResponseByEndpoint> = ResponseByEndpoint[K];
export type EndpointOutcome = ResponseByEndpoint[keyof ResponseByEndpoint];
export type CommandDraft = RequestByEndpoint["command.execute"];
export type FieldDescriptor = SemanticDefinition["fields"][number];
export type CommandDescriptor = SemanticDefinition["commands"][number];
export type CommandInputShape = CommandDescriptor["inputs"];
export type CommandInputDescriptor =
  | Extract<CommandInputShape, { kind: "FIELDS" }>["fields"][number]
  | CorrectionGroupDescriptor["replaceFields"][number];
export type CorrectionGroupDescriptor = Extract<
  CommandInputShape,
  { kind: "CORRECTION_GROUPS" }
>["groups"][number];
type Success<K extends keyof ResponseByEndpoint> = Extract<
  ResponseByEndpoint[K]["outcome"],
  { tag: "SUCCEEDED" }
>["data"];
export type CaseLookup = Success<"case.get">;
export type CaseState = Extract<CaseLookup, { tag: "FOUND" }>["current"];
export type CurrentCase = Extract<CaseState, { state: "LIVE" }>;
export type CaseSummary = Success<"case.list">["items"][number];
export type SessionSnapshot = Success<"session">;
export type PublicDefinition = Readonly<{
  definition: SemanticDefinition;
  productVersion: string;
  semanticFingerprint: string;
  wireFingerprint: string;
}>;
export type ApiResult<T> =
  | { readonly kind: "outcome"; readonly value: T; readonly status: number }
  | { readonly kind: "hostFailure"; readonly failure: HostFailure; readonly status: number }
  | { readonly kind: "deliveryFailure"; readonly notice: Notice };
