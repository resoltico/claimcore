import { usePresentation } from "../presentation/context";
import { NoticeView } from "../presentation/Message";
import { useEffect, useRef, type RefObject } from "react";
import { useOperationEditor } from "../hooks/operation/useOperationEditor";
import { AcceptedOperation, CommandChangeDialog } from "./operation/OperationDialogs";
import { OperationForm } from "./operation/OperationForm";
import { OperationReview } from "./operation/OperationReview";
import type { OperationEditorModel, OperationEditorProps } from "../interaction/editorTypes";

const useEditorFocus = (
  model: OperationEditorModel,
  section: RefObject<HTMLElement | null>,
  previewButtonRef: RefObject<HTMLButtonElement | null>,
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
      previewButtonRef.current?.focus();
    }
  }, [model.state.delivery, previewButtonRef]);
};

const requestLeave = (model: OperationEditorModel, onClose: () => void): void => {
  if (model.canPreview && model.dirty) {
    model.setPendingDiscard("LEAVE");
  } else if (!model.locked) {
    onClose();
  }
};

const UnknownOperation = ({ model }: { model: OperationEditorModel }) => {
  const p = usePresentation();
  return (
    <section role="alert" aria-busy={model.state.delivery === "SUBMITTING"}>
      <NoticeView value={model.state.message ?? { kind: "local", reason: "outcomeUnknown" }} />
      <p>{p.text("ui.retryExactDescription")}</p>
      <button
        type="button"
        disabled={model.state.delivery === "SUBMITTING"}
        onClick={() => void model.submit()}
      >
        {p.text("ui.retryExact")}
      </button>
    </section>
  );
};

const DisclosureRefused = ({
  model,
  onClose,
}: {
  model: OperationEditorModel;
  onClose: () => void;
}) => {
  const p = usePresentation();
  return (
    <section role="alert">
      <NoticeView value={model.state.message!} />
      <button type="button" onClick={onClose}>
        {p.text("ui.backToCase")}
      </button>
    </section>
  );
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
  const previewButtonRef = useRef<HTMLButtonElement>(null);
  useEditorFocus(model, section, previewButtonRef);
  if (model.state.unresolved) {
    return <UnknownOperation model={model} />;
  }
  if (model.state.disclosureRefused) {
    return <DisclosureRefused model={model} onClose={props.onClose} />;
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
        previewButtonRef={previewButtonRef}
      />
      <OperationReview model={model} fields={props.definition.descriptors.fields} />
      <CommandChangeDialog model={model} onClose={props.onClose} />
    </section>
  );
};

export const OperationEditor = (props: OperationEditorProps) => {
  const model = useOperationEditor(props);
  return <OperationEditorLayout props={props} model={model} />;
};
