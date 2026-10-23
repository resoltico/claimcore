import type {
  HistoryEntries,
  HostFailure,
  RequestByEndpoint,
  ResponseByEndpoint,
  CaseView,
} from "@contracts/client";
import type { productMetadata } from "../protocol/productMetadata";
import type { Notice } from "./notices";
export type {
  CaseFields,
  CaseView,
  CaseReceipt,
  CaseReview,
  OperationReview,
  Diagnostic,
} from "@contracts/client";
export type HistoryEntry = HistoryEntries[number];
export type { OperationReceipt } from "@contracts/client";
export type Descriptors = typeof productMetadata.descriptors;
export type ProductMetadata = typeof productMetadata;
export type FieldDescriptor = Descriptors["fields"][number] | Descriptors["inputFields"][number];
export type CommandDraft = Extract<RequestByEndpoint["operation.execute"], { scope: "CASE" }>;
export type ServiceResponse<K extends keyof ResponseByEndpoint> = ResponseByEndpoint[K];
export type EndpointOutcome = ResponseByEndpoint[keyof ResponseByEndpoint];
type Success<K extends keyof ResponseByEndpoint> = Extract<
  ResponseByEndpoint[K]["outcome"],
  { tag: "SUCCEEDED" }
>["data"];
export type CaseState = CaseView;
export type CurrentCase = Omit<CaseView, "state"> & {
  readonly state: Extract<CaseView["state"], { tag: "LIVE" }>;
};
export const isCurrentCase = (value: CaseView): value is CurrentCase => value.state.tag === "LIVE";
export type CaseSummary = Success<"case.list">["items"][number];
export type SessionSnapshot = Success<"session">;
export type ApiResult<T> =
  | { readonly kind: "outcome"; readonly value: T; readonly status: number }
  | { readonly kind: "hostFailure"; readonly failure: HostFailure; readonly status: number }
  | { readonly kind: "deliveryFailure"; readonly notice: Notice; readonly dispatched?: boolean };
