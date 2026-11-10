import type {
  HostFailure,
  HumanReadDocument_caseGet,
  HumanReadDocument_authorityGrants,
  Representation,
  ResponseFor,
  HumanReceiptDocument,
  Screen,
  WorkDateBasis,
  RequestByEndpoint,
  ResponseByEndpoint,
} from "@contracts/client";
import type { productMetadata } from "./productMetadata";
import type { Notice } from "./notices";
export type { CaseFields } from "@contracts/client";
export type PersonDocument = HumanReadDocument_authorityGrants;
export type OperationReceipt = HumanReceiptDocument;
export type PresentedPage = { readonly view: Screen; readonly basis: WorkDateBasis };
export type CaseOperationReceipt = OperationReceipt & {
  readonly context: OperationReceipt["context"] & {
    readonly target: Extract<OperationReceipt["context"]["target"], { kind: "CASE" }>;
  };
};
export const isCaseReceipt = (value: OperationReceipt): value is CaseOperationReceipt =>
  value.context.target.kind === "CASE";
export type CatalogDocument = typeof productMetadata.catalog;
export type ProductMetadata = typeof productMetadata;
export type FieldCatalogEntry = (
  CatalogDocument["fields"][number] | CatalogDocument["inputFields"][number]
) & {
  readonly scalar: NonNullable<CatalogDocument["inputFields"][number]["scalar"]>;
  readonly valueKind?: CatalogDocument["fields"][number]["valueKind"];
};
export type OperationDraft = Extract<RequestByEndpoint["operation.execute"], { kind: "CASE" }>;
export type ServiceResponse<
  K extends keyof ResponseByEndpoint,
  R extends Representation = "HUMAN",
> = ResponseFor<K, R>;
export type EndpointOutcome = ResponseByEndpoint[keyof ResponseByEndpoint];
type Success<K extends keyof ResponseByEndpoint> =
  Extract<ResponseByEndpoint[K]["outcome"], { kind: "SUCCEEDED"; data: unknown }> extends {
    readonly data: infer T;
  }
    ? T
    : never;
export type CurrentCase = HumanReadDocument_caseGet & {
  readonly disclosure: "CURRENT";
  readonly basis: Extract<HumanReadDocument_caseGet["basis"], { kind: "SNAPSHOT" }>;
};
export const isCurrentCase = (value: HumanReadDocument_caseGet): value is CurrentCase =>
  value.disclosure === "CURRENT" && value.basis.kind === "SNAPSHOT";
export type CaseSummary = Success<"case.list">["items"][number];
export type SessionSnapshot = Success<"session">;
export type ApiResult<T> =
  | { readonly kind: "outcome"; readonly value: T; readonly status: number }
  | { readonly kind: "hostFailure"; readonly failure: HostFailure; readonly status: number }
  | { readonly kind: "deliveryFailure"; readonly notice: Notice; readonly dispatched?: boolean };
