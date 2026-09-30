import type { HostFailure } from "../generated/convergence/web-v3.types";
import type { Notice } from "./notices";

/** Generated endpoint-owned types exposed by the validated browser API boundary. */
export type {
  AdvisoryReview,
  CaseFields,
  CaseSummary,
  CaseView,
  CommandDescriptor,
  CommandDraft,
  CommandInputDescriptor,
  CommandInputShape,
  CorrectionGroupDescriptor,
  CurrentCase,
  DefinitionPayload,
  EndpointOutcome,
  FieldDescriptor,
  PreparationDetails,
  PreparationSummary,
  Receipt,
  Rejection,
  RecoveryDetails,
  RecoveryImportPreview,
  RecoveryInspection,
  RecoveryListItem,
  RecoveryPage,
  RevokedOperation,
  SemanticDefinition,
  SessionSnapshot,
  WebV3Response,
} from "../generated/convergence/web-v3.types";

/** Local delivery facts belong to the browser adapter, not generated server DTOs. */
export type ApiResult<T> =
  | { readonly kind: "outcome"; readonly value: T; readonly status: number }
  | { readonly kind: "hostFailure"; readonly failure: HostFailure; readonly status: number }
  | { readonly kind: "deliveryFailure"; readonly notice: Notice };
