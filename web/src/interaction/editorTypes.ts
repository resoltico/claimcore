import type { CurrentCase, ProductMetadata, CaseReceipt } from "../api/service";
import type { CommandKind } from "./metadata";
export type OperationEditorProps = {
  token: string;
  definition: ProductMetadata;
  current: CurrentCase | null;
  initialCommand: CommandKind;
  onClose: () => void;
  onCommitted: (receipt: CaseReceipt) => void;
  onMutationLockChange: (locked: boolean) => void;
};
