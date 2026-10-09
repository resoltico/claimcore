import { usePresentation } from "../presentation/context";
import { NoticeView } from "../presentation/Message";
import { useEffect, useRef, type RefObject } from "react";
import { useOperationEditor } from "../hooks/operation/useOperationEditor";
import { AcceptedOperation, CommandChangeDialog } from "./operation/OperationDialogs";
import { OperationForm } from "./operation/OperationForm";
import { OperationReview } from "./operation/OperationReview";
import type { OperationEditorModel, OperationEditorProps } from "./operation/editorTypes";

const useEditorFocus = (
  model: OperationEditorModel,
  section: RefObject<HTMLElement | null>,
  prepareButtonRef: RefObject<HTMLButtonElement | null>,
) => {
  const previousDelivery = useRef(model.state.delivery);
  useEffect(() => {
    const field = model.state.fieldError?.name;
    if (model.state.delivery !== "DEFINITELY_REJECTED" || field === undefined) {
      return;
    }
    // React attaches this ref during commit before effects run.
    const inputs = section.current!.querySelectorAll<HTMLInputElement | HTMLSelectElement>(
      "input, select",
    );
    const target =
      [...inputs].find((input) => input.name === field) ??
      [...inputs].find((input) => input.dataset["fieldName"] === field);
    target?.focus();
  }, [model.state.delivery, model.state.fieldError, section]);
  useEffect(() => {
    const previous = previousDelivery.current;
    previousDelivery.current = model.state.delivery;
    if (previous === "REVIEWING" && model.state.delivery === "EDITING") {
      prepareButtonRef.current?.focus();
    }
  }, [model.state.delivery, prepareButtonRef]);
};

const requestLeave = (model: OperationEditorModel, onClose: () => void): void => {
  if (model.canPrepare && model.dirty) {
    model.setPendingDiscard("LEAVE");
  } else if (!model.locked) {
    onClose();
  }
};

const OperationEditorLayout = ({
  props,
  model,
}: {
  props: OperationEditorProps;
  model: OperationEditorModel;
}) => {
  const p = usePresentation();
  const section = useRef<HTMLElement>(null);
  const prepareButtonRef = useRef<HTMLButtonElement>(null);
  useEditorFocus(model, section, prepareButtonRef);
  if (model.state.delivery === "OUTCOME_UNKNOWN") {
    return (
      <p role="alert">
        <NoticeView value={model.state.message!} />
      </p>
    );
  }
  if (model.receipt !== null) {
    return (
      <AcceptedOperation
        definition={props.definition}
        receipt={model.receipt}
        message={model.state.message}
        onCommitted={props.onCommitted}
      />
    );
  }
  return (
    <section ref={section} aria-labelledby="operation-title" className="operation-editor">
      <h2 id="operation-title">{p.commandLabel(model.state.command)}</h2>
      <p>{p.commandMeaning(model.state.command)}</p>
      <p>{p.text("ui.unchangedUntilSubmit")}</p>
      <OperationForm
        definition={props.definition}
        current={props.current}
        model={model}
        onClose={() => {
          requestLeave(model, props.onClose);
        }}
        prepareButtonRef={prepareButtonRef}
      />
      <OperationReview model={model} fields={props.definition.definition.fields} />
      <CommandChangeDialog model={model} onClose={props.onClose} />
    </section>
  );
};

export const OperationEditor = (props: OperationEditorProps) => {
  const model = useOperationEditor(props);
  return <OperationEditorLayout props={props} model={model} />;
};
