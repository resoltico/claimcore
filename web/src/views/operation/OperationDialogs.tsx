import { usePresentation } from "../../presentation/context";
import { Button } from "react-aria-components/Button";
import type { DefinitionPayload, Receipt } from "../../api/v2";
import { AccessibleModal } from "../../components/AccessibleModal";
import { CaseFieldsView } from "../../components/CaseFieldsView";
import type { OperationEditorModel } from "./editorTypes";

type CommandChangeProps = { model: OperationEditorModel };

export const CommandChangeDialog = ({ model }: CommandChangeProps) => {
  const p = usePresentation();
  if (model.pendingCommand === null) return null;
  const pendingCommand = model.pendingCommand;
  return (
    <AccessibleModal
      title={p.text("ui.changeCommandTitle")}
      description={p.text("ui.changeCommandDescription")}
      isOpen
      isDismissable
      onOpenChange={() => model.setPendingCommand(null)}
    >
      <div className="dialog-actions">
        <Button className="secondary-button" onPress={() => model.setPendingCommand(null)}>
          {p.text("ui.keepEditing")}
        </Button>
        <Button onPress={() => model.applyCommand(pendingCommand)}>
          {p.text("ui.discardAndChange")}
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
  const p = usePresentation();
  if (receipt === null) return null;
  return (
    <section aria-labelledby="accepted-title" className="receipt">
      <h2 id="accepted-title">{p.text("ui.acceptedOperation")}</h2>
      <p>{p.text("ui.operationAccepted", { operationId: receipt.operationId })}</p>
      <CaseFieldsView
        caseView={receipt.snapshot}
        fields={definition.definition.fields}
        context={p.text("ui.acceptedContext", { operationId: receipt.operationId })}
      />
      <Button onPress={onCommitted}>{p.text("ui.returnToCase")}</Button>
    </section>
  );
};
