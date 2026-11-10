import type { MutationLockChange } from "../hooks/useWorkspaceMutationLock";
import type { CurrentCase, ProductMetadata, CaseOperationReceipt } from "../api/service";
import type { FormValues } from "../protocol/forms";
import type { TaskBindingSource } from "./taskForm";
import type { ActKind } from "./metadata";
export type OperationEditorProps = {
  definition: ProductMetadata;
  current: CurrentCase | null;
  initialAct: ActKind;
  prefill?: FormValues;
  autoReview?: boolean;
  binding?: Extract<TaskBindingSource, { kind: "CASE" }>;
  disclosureAvailable?: boolean;
  onClose: () => void;
  onCommitted: (receipt: CaseOperationReceipt, operationId: string) => void;
  onMutationLockChange: MutationLockChange;
};
