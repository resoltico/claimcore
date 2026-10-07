import { usePresentation } from "../../presentation/context";
import { Button } from "react-aria-components/Button";
import { useEffect, useRef } from "react";
import type { Notice } from "../../api/notices";
import { NoticeView } from "../../presentation/Message";
import type { DefinitionPayload, Receipt } from "../../api/v3";
import { AccessibleModal } from "../../components/AccessibleModal";
import { CaseFieldsView } from "../../components/CaseFieldsView";
import type { OperationEditorModel } from "./editorTypes";

type CommandChangeProps = { model: OperationEditorModel; onClose: () => void };

export const CommandChangeDialog = ({ model, onClose }: CommandChangeProps) => {
  const p = usePresentation();
  if (model.pendingDiscard === null) {
    return null;
  }
  const { pendingDiscard } = model;
  return (
    <AccessibleModal
      closeLabel={p.text("ui.keepEditing")}
      title={p.text("ui.changeCommandTitle")}
      description={p.text(
        pendingDiscard === "LEAVE" ? "ui.leaveDraftDescription" : "ui.changeCommandDescription",
      )}
      isOpen
      isDismissable
      onOpenChange={() => {
        model.setPendingDiscard(null);
      }}
    >
      <div className="dialog-actions">
        <Button
          onPress={() => {
            if (pendingDiscard === "LEAVE") {
              onClose();
            } else {
              model.applyCommand(pendingDiscard);
            }
          }}
        >
          {p.text(pendingDiscard === "LEAVE" ? "ui.discardAndLeave" : "ui.discardAndChange")}
        </Button>
      </div>
    </AccessibleModal>
  );
};

type AcceptedOperationProps = {
  definition: DefinitionPayload;
  receipt: Receipt;
  message: Notice | null;
  onCommitted: (receipt: Receipt) => void;
};

export const AcceptedOperation = ({
  definition,
  receipt,
  message,
  onCommitted,
}: AcceptedOperationProps) => {
  const p = usePresentation();
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    heading.current?.focus();
  }, []);
  return (
    <section aria-labelledby="accepted-title" className="receipt">
      {message === null ? null : (
        <p role="alert">
          <NoticeView value={message} />
        </p>
      )}
      <h2 ref={heading} tabIndex={-1} id="accepted-title">
        {p.text("ui.acceptedOperation")}
      </h2>
      <p>{p.text("ui.operationAccepted", { operationId: receipt.operationId })}</p>
      <CaseFieldsView
        caseView={receipt.snapshot}
        fields={definition.definition.fields}
        context={p.text("ui.acceptedContext", { operationId: receipt.operationId })}
      />
      <Button
        onPress={() => {
          onCommitted(receipt);
        }}
      >
        {p.text("ui.returnToCase")}
      </Button>
    </section>
  );
};
