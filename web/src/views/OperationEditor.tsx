import { usePresentation } from "../presentation/context";
import { useOperation } from "../hooks/operation/useOperation";
import { AcceptedOperation } from "./operation/OperationDialogs";
import { OperationPanel } from "./operation/OperationPanel";
import type { OperationEditorProps } from "../interaction/editorTypes";

export const OperationEditor = (props: OperationEditorProps) => {
  const p = usePresentation();
  const model = useOperation({
    scope: "CASE",
    kind: props.initialCommand,
    current: props.current,
    token: props.token,
    onLockChange: props.onMutationLockChange,
  });
  if (model.receipt?.scope.kind === "CASE") {
    return (
      <AcceptedOperation
        definition={props.definition}
        receipt={{ ...model.receipt, scope: model.receipt.scope }}
        message={model.notice}
        onCommitted={props.onCommitted}
      />
    );
  }
  const offered = props.current?.advice.acts.map((offer) => offer.act) ?? [props.initialCommand];
  return (
    <section aria-labelledby="operation-title" className="operation-editor">
      <h2 id="operation-title">{p.commandLabel(String(model.values["act.kind"]))}</h2>
      <p>{p.commandMeaning(String(model.values["act.kind"]))}</p>
      <p>{p.text("ui.unchangedUntilSubmit")}</p>
      <OperationPanel
        model={model}
        current={props.current}
        offered={offered}
        onClose={props.onClose}
      />
    </section>
  );
};
