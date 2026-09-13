import { Button } from "react-aria-components/Button";
import type { DefinitionPayload, Receipt } from "../../api/v2";
import { AccessibleModal } from "../../components/AccessibleModal";
import { CaseFieldsView } from "../../components/CaseFieldsView";
import type { OperationEditorModel } from "./editorTypes";

type CommandChangeProps = { model: OperationEditorModel };

export const CommandChangeDialog = ({ model }: CommandChangeProps) => {
  if (model.pendingCommand === null) return null;
  const pendingCommand = model.pendingCommand;
  return (
    <AccessibleModal
      title="Discard this command draft?"
      description="Changing command discards these authored values and creates a new operation ID."
      isOpen
      isDismissable
      onOpenChange={() => model.setPendingCommand(null)}
    >
      <div className="dialog-actions">
        <Button className="secondary-button" onPress={() => model.setPendingCommand(null)}>
          Keep editing
        </Button>
        <Button onPress={() => model.applyCommand(pendingCommand)}>
          Discard and change command
        </Button>
      </div>
    </AccessibleModal>
  );
};

type AcceptedOperationProps = {
  definition: DefinitionPayload;
  receipt: Receipt | null;
  onCommitted: () => void;
};

export const AcceptedOperation = ({ definition, receipt, onCommitted }: AcceptedOperationProps) => {
  if (receipt === null) return null;
  return (
    <section aria-labelledby="accepted-title" className="receipt">
      <h2 id="accepted-title">Accepted operation</h2>
      <p>
        Operation <bdi>{receipt.operationId}</bdi> is accepted.
      </p>
      <CaseFieldsView
        caseView={receipt.snapshot}
        fields={definition.definition.fields}
        context={`accepted operation ${receipt.operationId}`}
      />
      <Button onPress={onCommitted}>Return to case</Button>
    </section>
  );
};
