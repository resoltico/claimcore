import type { CurrentCase, ProductMetadata, CaseReceipt } from "../api/service";
import type { ActKind } from "./metadata";
export type OperationEditorProps = {
  token: string;
  definition: ProductMetadata;
  current: CurrentCase | null;
  initialAct: ActKind;
  onClose: () => void;
  onCommitted: (receipt: CaseReceipt) => void;
  onMutationLockChange: (locked: boolean) => void;
};
