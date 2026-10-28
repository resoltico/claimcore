import { useUnresolvedOperation } from "./operation/unresolvedOperation";
import { usePresentation } from "../presentation/context";
import { useOperation } from "../hooks/operation/useOperation";
import { AcceptedOperation } from "./operation/OperationDialogs";
import { OperationPanel, OperationDiscard } from "./operation/OperationPanel";
import type { OperationEditorProps } from "../operation/editorTypes";
export const OperationEditor = (props: OperationEditorProps) => {
  const p = usePresentation();
  const model = useOperation({
    subject: "CASE",
    kind: props.initialAct,
    current: props.current,
    token: props.token,
    onLockChange: props.onMutationLockChange,
  });
  useUnresolvedOperation(model);
  if (model.receipt?.subject.kind === "CASE") {
    return (
      <AcceptedOperation
        definition={props.definition}
        receipt={{
          ...model.receipt,
          subject: model.receipt.subject,
        }}
        message={model.notice}
        onCommitted={(receipt) => {
          props.onCommitted(receipt);
        }}
      />
    );
  }
  const offered = props.current?.advice.acts ?? [props.initialAct];
  return (
    <section aria-labelledby="operation-title" className="operation-editor">
      <h2 id="operation-title">{p.actLabel(model.entry.kind)}</h2>
      <p>{p.actMeaning(model.entry.kind)}</p>
      <p>{p.text("ui.unchangedUntilSubmit")}</p>
      <OperationDiscard model={model} onClose={props.onClose} />
      <OperationPanel
        model={model}
        current={props.current}
        offered={offered}
        onClose={props.onClose}
      />
    </section>
  );
};
