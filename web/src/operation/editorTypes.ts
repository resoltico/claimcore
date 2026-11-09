import type { CurrentCase, ProductMetadata, CaseOperationReceipt } from "../api/service";
import type { FormValues } from "../protocol/forms";
import type { ActKind } from "./metadata";
export type OperationEditorProps = {
  definition: ProductMetadata;
  current: CurrentCase | null;
  initialAct: ActKind;
  prefill?: FormValues;
  autoReview?: boolean;
  fixedPaths?: ReadonlyArray<string>;
  disclosureAvailable?: boolean;
  onClose: () => void;
  onCommitted: (receipt: CaseOperationReceipt) => void;
  onMutationLockChange: (locked: boolean) => void;
};
