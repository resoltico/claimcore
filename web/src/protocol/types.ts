import type {
  HostFailure,
  OperationReceipt as ReceiptData,
  PresentedView,
  WorkDateBasis,
  RequestByEndpoint,
  ResponseByEndpoint,
  CaseView,
} from "@contracts/client";
import type { productMetadata } from "./productMetadata";
import type { Notice } from "./notices";
export type { CaseFields, CaseView, OperationReview, Diagnostic } from "@contracts/client";
export type OperationReceipt = ReceiptData & { readonly view: PresentedView };
export type PresentedPage = { readonly view: PresentedView; readonly basis: WorkDateBasis };
export type CaseOperationReceipt = Omit<OperationReceipt, "subject"> & {
  readonly subject: Extract<OperationReceipt["subject"], { kind: "CASE" }>;
};
export type CatalogDocument = typeof productMetadata.catalog;
export type ProductMetadata = typeof productMetadata;
export type FieldCatalogEntry = (
  CatalogDocument["fields"][number] | CatalogDocument["inputFields"][number]
) & {
  readonly scalar: NonNullable<CatalogDocument["inputFields"][number]["scalar"]>;
  readonly valueKind?: CatalogDocument["fields"][number]["valueKind"];
};
export type OperationDraft = Extract<RequestByEndpoint["operation.execute"], { kind: "CASE" }>;
export type ServiceResponse<K extends keyof ResponseByEndpoint> = ResponseByEndpoint[K];
export type EndpointOutcome = ResponseByEndpoint[keyof ResponseByEndpoint];
type Success<K extends keyof ResponseByEndpoint> = Extract<
  ResponseByEndpoint[K]["outcome"],
  { kind: "SUCCEEDED" }
>["data"];
export type CurrentCase = Omit<CaseView, "state"> & {
  readonly view: PresentedView;
  readonly state: Extract<CaseView["state"], { kind: "ACTIVE" | "VOIDED" }>;
};
export const isCurrentCase = <T extends CaseView>(
  value: T,
): value is T & { readonly state: Extract<CaseView["state"], { kind: "ACTIVE" | "VOIDED" }> } =>
  value.state.kind !== "ERASED";
export type CaseSummary = Success<"case.list">["items"][number];
export type SessionSnapshot = Success<"session">;
export type ApiResult<T> =
  | { readonly kind: "outcome"; readonly value: T; readonly status: number }
  | { readonly kind: "hostFailure"; readonly failure: HostFailure; readonly status: number }
  | { readonly kind: "deliveryFailure"; readonly notice: Notice; readonly dispatched?: boolean };
