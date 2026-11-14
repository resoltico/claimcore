import type { OperationAffordance } from "./affordances";
import type { MutationLockChange } from "../hooks/useWorkspaceMutationLock";
import type { CurrentCase, ProductMetadata, CaseLinkedReceipt } from "../api/service";
import type { FormValues } from "../protocol/forms";
import type { OperationBinding } from "@contracts/client";
import type { ActKind } from "./metadata";
export type OperationEditorProps = {
  affordance?: OperationAffordance;
  definition: ProductMetadata;
  current: CurrentCase | null;
  initialAct: ActKind;
  prefill?: FormValues;
  autoReview?: boolean;
  binding?: Extract<OperationBinding, { kind: "CASE" }>;
  disclosureAvailable?: boolean;
  onClose: () => void;
  onCommitted: (receipt: CaseLinkedReceipt, operationId: string) => void;
  onMutationLockChange: MutationLockChange;
};
