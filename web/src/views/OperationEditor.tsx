import { useEffect, useRef } from "react";
import { useOperationEditor } from "../hooks/operation/useOperationEditor";
import { AcceptedOperation, CommandChangeDialog } from "./operation/OperationDialogs";
import { OperationForm } from "./operation/OperationForm";
import { OperationReview } from "./operation/OperationReview";
import type { OperationEditorModel, OperationEditorProps } from "./operation/editorTypes";

const OperationEditorLayout = ({
  props,
  model,
}: {
  props: OperationEditorProps;
  model: OperationEditorModel;
}) => {
  const section = useRef<HTMLElement>(null);
  const prepareButtonRef = useRef<HTMLButtonElement>(null);
  const previousDelivery = useRef(model.state.delivery);
  useEffect(() => {
    const field = model.state.fieldError?.name;
    if (model.state.delivery !== "DEFINITELY_REJECTED" || field === undefined) return;
    // React attaches this ref during commit before effects run.
    const inputs = section.current!.querySelectorAll("input");
    const target = [...inputs].find((input) => input.name === field);
    target?.focus();
  }, [model.state.delivery, model.state.fieldError]);
  useEffect(() => {
    const previous = previousDelivery.current;
    previousDelivery.current = model.state.delivery;
    if (previous === "REVIEWING" && model.state.delivery === "EDITING")
      prepareButtonRef.current?.focus();
  }, [model.state.delivery]);
  return (
    <section ref={section} aria-labelledby="operation-title" className="operation-editor">
      <h2 id="operation-title">{model.command.label}</h2>
      <p>{model.command.meaning}</p>
      <p>Accepted state is unchanged until the exact prepared request is explicitly submitted.</p>
      <OperationForm
        definition={props.definition}
        current={props.current}
        model={model}
        onClose={props.onClose}
        prepareButtonRef={prepareButtonRef}
      />
      <OperationReview model={model} />
      <CommandChangeDialog model={model} />
      <AcceptedOperation
        definition={props.definition}
        receipt={model.receipt}
        onCommitted={props.onCommitted}
      />
    </section>
  );
};

export const OperationEditor = (props: OperationEditorProps) => {
  const model = useOperationEditor(props);
  return <OperationEditorLayout props={props} model={model} />;
};
