import type { CurrentCase, ProductMetadata, CaseOperationReceipt } from "../api/service";
import type { ActKind } from "./metadata";
export type OperationEditorProps = {
  definition: ProductMetadata;
  current: CurrentCase | null;
  initialAct: ActKind;
  disclosureAvailable?: boolean;
  onClose: () => void;
  onCommitted: (receipt: CaseOperationReceipt) => void;
  onMutationLockChange: (locked: boolean) => void;
};
