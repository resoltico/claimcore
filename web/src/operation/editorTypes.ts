import type { CurrentCase, ProductMetadata, CaseReceipt } from "../api/service";
import type { ActKind } from "./metadata";
export type OperationEditorProps = {
  definition: ProductMetadata;
  current: CurrentCase | null;
  initialAct: ActKind;
  disclosureAvailable?: boolean;
  onClose: () => void;
  onCommitted: (receipt: CaseReceipt) => void;
  onMutationLockChange: (locked: boolean) => void;
};
