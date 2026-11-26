import type {
  HostFailure,
  HumanDocument,
  DocumentSubject,
  Representation,
  ResponseFor,
  Screen,
  WorkDateBasis,
  ResponseByEndpoint,
} from "@contracts/client";
import type { productMetadata } from "./productMetadata";
import type { Notice } from "./notices";
export type SubjectDocument<K extends DocumentSubject["kind"]> = HumanDocument & {
  readonly subject: Extract<DocumentSubject, { kind: K }>;
};
export const hasDocumentSubject = <K extends DocumentSubject["kind"]>(
  value: HumanDocument,
  kind: K,
): value is SubjectDocument<K> => value.subject.kind === kind;
export type CaseDocument = SubjectDocument<"CASE">;
export type PersonDocument = SubjectDocument<"PERSON">;
export type RequesterDocument = SubjectDocument<"REQUESTER">;
export type OperationReceipt = HumanDocument;
export type PresentedPage = { readonly view: Screen; readonly basis: WorkDateBasis };
export type CaseLinkedReceipt = { readonly document: OperationReceipt; readonly caseId: string };
export const linkedCaseReceipt = (document: OperationReceipt): CaseLinkedReceipt | null => {
  const target = document.links.find((link) => link.rel === "OPEN_CASE")?.link;
  const selected = target?.input["target"];
  return target?.endpoint === "case.get" &&
    typeof selected === "object" &&
    selected !== null &&
    "kind" in selected &&
    selected.kind === "ID" &&
    "caseId" in selected &&
    typeof selected.caseId === "string"
    ? { document, caseId: selected.caseId }
    : null;
};
export type CatalogDocument = typeof productMetadata.catalog;
export type ProductMetadata = typeof productMetadata;
export type FieldCatalogEntry = (
  CatalogDocument["fields"][number] | CatalogDocument["inputFields"][number]
) & {
  readonly scalar: NonNullable<CatalogDocument["inputFields"][number]["scalar"]>;
  readonly valueKind?: CatalogDocument["fields"][number]["valueKind"];
};
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
export type CurrentCase = CaseDocument & {
  readonly disclosure: "CURRENT";
  readonly basis: Extract<HumanDocument["basis"], { kind: "SNAPSHOT" }>;
};
export const isCurrentCase = (value: HumanDocument): value is CurrentCase =>
  value.subject.kind === "CASE" &&
  value.disclosure === "CURRENT" &&
  value.basis.kind === "SNAPSHOT";
export type SessionSnapshot = Success<"session">;
export type ApiResult<T> =
  | { readonly kind: "outcome"; readonly value: T; readonly status: number }
  | {
      readonly kind: "hostFailure";
      readonly failure: HostFailure;
      readonly status: number;
      readonly retryAfterMs?: number;
    }
  | { readonly kind: "deliveryFailure"; readonly notice: Notice; readonly dispatched?: boolean };
