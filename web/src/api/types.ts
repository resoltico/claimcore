import type {
  HistoryEntries,
  HostFailure,
  RequestByEndpoint,
  ResponseByEndpoint,
  CaseView,
  OperationReview as Review,
} from "@contracts/client";
import type { productMetadata } from "../protocol/productMetadata";
import type { Notice } from "./notices";
export type {
  CaseFields,
  CaseView,
  CaseReceipt,
  OperationReview,
  Diagnostic,
} from "@contracts/client";
export type HistoryEntry = HistoryEntries[number];
export type { OperationReceipt } from "@contracts/client";
export type CatalogDocument = typeof productMetadata.catalog;
export type ProductMetadata = typeof productMetadata;
export type FieldCatalogEntry = (
  CatalogDocument["fields"][number] | CatalogDocument["inputFields"][number]
) & {
  readonly scalar: NonNullable<CatalogDocument["inputFields"][number]["scalar"]>;
};
export type OperationDraft = Extract<RequestByEndpoint["operation.execute"], { kind: "CASE" }>;
export type ServiceResponse<K extends keyof ResponseByEndpoint> = ResponseByEndpoint[K];
export type EndpointOutcome = ResponseByEndpoint[keyof ResponseByEndpoint];
type Success<K extends keyof ResponseByEndpoint> = Extract<
  ResponseByEndpoint[K]["outcome"],
  { kind: "SUCCEEDED" }
>["data"];
export type CurrentCase = Omit<CaseView, "state"> & {
  readonly state: Extract<CaseView["state"], { kind: "ACTIVE" | "VOIDED" }>;
};
export const isCurrentCase = (value: CaseView): value is CurrentCase =>
  value.state.kind !== "ERASED";
export type CaseSummary = Success<"case.list">["items"][number];
export type SessionSnapshot = Success<"session">;
export type ApiResult<T> =
  | { readonly kind: "outcome"; readonly value: T; readonly status: number }
  | { readonly kind: "hostFailure"; readonly failure: HostFailure; readonly status: number }
  | { readonly kind: "deliveryFailure"; readonly notice: Notice; readonly dispatched?: boolean };

export type CaseReview = Extract<Review["subject"], { kind: "CASE" }>;
