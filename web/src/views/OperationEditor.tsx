import { CaseFieldsView } from "../components/CaseFieldsView";
import { normalizeAmountValues } from "../presentation/amountEntry";
import { useCallback, useEffect, useRef } from "react";
import { useOperationNavigation } from "../hooks/useOperationNavigation";
import { useCredential } from "../session/SessionContext";
import { useUnresolvedOperation } from "./operation/unresolvedOperation";
import { usePresentation } from "../presentation/context";
import { useOperation } from "../hooks/operation/useOperation";
import { AcceptedOperation } from "./operation/OperationDialogs";
import { OperationPanel, OperationDiscard } from "./operation/OperationPanel";
import type { OperationEditorProps } from "../operation/editorTypes";
const useCaseOperation = (props: OperationEditorProps) => {
  const credential = useCredential();
  const p = usePresentation();
  const model = useOperation({
    subject: "CASE",
    kind: props.initialAct,
    current: props.current,
    credential,
    normalizeValues: (values, entry) => normalizeAmountValues(values, entry, p.displayLocale),
    onLockChange: props.onMutationLockChange,
  });
  useUnresolvedOperation(model);
  const navigation = useOperationNavigation(model, props.onClose);
  const recorded = useRef<string | null>(null);
  const commit = useCallback(
    (receipt: Parameters<OperationEditorProps["onCommitted"]>[0]) => {
      if (recorded.current !== receipt.operationId) {
        recorded.current = receipt.operationId;
        navigation.committed();
        props.onCommitted(receipt);
      }
    },
    [navigation, props],
  );
  useEffect(() => {
    if (model.receipt?.subject.kind === "CASE") {
      commit({ ...model.receipt, subject: model.receipt.subject });
    }
  }, [model.receipt, commit]);
  return { model, navigation, commit };
};
const CurrentFacts = ({
  model,
  current,
  definition,
}: OperationEditorProps & { model: ReturnType<typeof useOperation> }) => {
  const p = usePresentation();
  if (
    current === null ||
    !model.disclosureCurrent ||
    model.disclosureRefused ||
    model.contextChanged ||
    model.priorUncertain
  ) {
    return null;
  }
  return (
    <aside className="editor-current">
      <CaseFieldsView
        values={current.state.fields}
        revision={current.revision}
        fields={definition.catalog.fields}
        context={p.text("ui.currentCase")}
      />
    </aside>
  );
};
export const OperationEditor = (props: OperationEditorProps) => {
  const p = usePresentation();
  const { model, navigation, commit } = useCaseOperation(props);
  if (model.receipt?.subject.kind === "CASE") {
    return (
      <AcceptedOperation
        definition={props.definition}
        receipt={{ ...model.receipt, subject: model.receipt.subject }}
        message={model.notice}
        onCommitted={commit}
      />
    );
  }
  const offered = props.current?.advice.acts ?? [props.initialAct];
  return (
    <section aria-labelledby="operation-title" className="operation-editor">
      <h1 id="operation-title">{p.actLabel(model.entry.kind)}</h1>
      <p>{p.actMeaning(model.entry.kind)}</p>
      <div className="editor-layout">
        <div>
          <OperationDiscard model={model} onClose={navigation.close} />
          <OperationPanel
            model={model}
            current={props.current}
            offered={offered}
            onClose={navigation.close}
          />
        </div>
        <CurrentFacts {...props} model={model} />
      </div>
    </section>
  );
};
