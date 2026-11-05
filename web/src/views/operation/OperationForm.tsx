import { Button } from "../../ui/Button";
import { Select } from "../../ui/Select";
import { useRequesterContext } from "../../hooks/useRequesterContext";
import { OperationErrors } from "./OperationErrors";
import { useRef, useEffect, type RefObject } from "react";
import { ReferenceSummary } from "../../components/ReferenceSummary";
import { CatalogInputs } from "../../components/CatalogInputs";
import { usePresentation } from "../../presentation/context";
import type { CurrentCase } from "../../api/service";
import type { OperationModel } from "../../hooks/operation/useOperation";
import { operationEntry } from "../../protocol/operationCatalog";

export type OperationFormProps = {
  model: OperationModel;
  current: CurrentCase | null;
  fixedTarget?: boolean;
  fixedPaths?: ReadonlyArray<string>;
  offered: ReadonlyArray<string>;
  onAct: (kind: string) => void;
  onClose?: () => void;
  formId?: string;
  onMount?: (form: HTMLFormElement) => void;
  onFieldBlur?: (control: EventTarget | null) => void;
};
const ActPicker = ({ model, offered, onAct }: OperationFormProps) => {
  const p = usePresentation();
  return model.entry.subject === "CASE" || offered.length < 2 ? null : (
    <Select
      id="act-picker"
      label={p.text("ui.authorityAction")}
      value={model.entry.kind}
      disabled={model.busy || model.phase === "REVIEWING" || model.priorUncertain}
      options={offered.map((kind) => ({ value: kind, label: p.authorityLabel(kind) }))}
      onChange={onAct}
    />
  );
};
const OperationInputs = ({
  model,
  current,
  offered,
  fixedTarget,
  fixedPaths,
}: OperationFormProps) => {
  const p = usePresentation();
  const allowed = offered.includes(model.entry.kind);
  const retainCreationInputs =
    model.entry.subject === "CASE" && model.entry.kind === "OPEN" && current === null;
  const act = operationEntry(model.entry);
  return (
    <>
      {" "}
      {current === null ? null : (
        <p>
          <ReferenceSummary
            id="ui.reference"
            values={{
              label: p.fieldLabel("caseReference"),
              reference: current.caseReference,
            }}
          />
        </p>
      )}
      {act.inputs.some((input) => input.kind.kind === "GROUP" && input.adviceGroup !== null) ? (
        <p>{p.text("ui.correctionHint")}</p>
      ) : null}
      {offered.length === 0 && !retainCreationInputs ? null : (
        <CatalogInputs
          entry={act}
          current={current}
          values={model.values}
          {...(fixedTarget === undefined ? {} : { fixedTarget })}
          {...(fixedPaths === undefined ? {} : { fixedPaths })}
          disabled={model.busy || model.revalidating || model.phase === "REVIEWING" || !allowed}
          fieldErrors={model.fieldErrors}
          onChange={model.edit}
          onFieldsChange={model.editFields}
        />
      )}
    </>
  );
};
export const OperationForm = (props: OperationFormProps) => {
  const { model, current, offered } = props;
  const form = useRef<HTMLFormElement>(null);
  const { onMount, onFieldBlur } = props;
  useEffect(() => {
    const element = form.current;
    if (element === null) {
      return;
    }
    onMount?.(element);
    const remember = (event: FocusEvent) => {
      onFieldBlur?.(event.target);
    };
    element.addEventListener("focusout", remember);
    return () => {
      element.removeEventListener("focusout", remember);
    };
  }, [onMount, onFieldBlur]);
  useEffect(() => {
    if (model.notice === null) {
      return;
    }
    form.current?.querySelector<HTMLElement>("[data-error-summary]")?.focus();
  }, [model.notice]);
  const paused =
    useRequesterContext()?.maintenance.active === true ||
    current?.advice.maintenance.active === true;
  const allowed = offered.includes(model.entry.kind) && !paused;
  return (
    <form
      className="operation-form-card"
      id={props.formId}
      ref={form}
      onSubmit={(event) => {
        event.preventDefault();
        if (allowed && model.conflict === null && !model.revalidating) {
          void model.preview();
        }
      }}
    >
      <OperationErrors model={model} />
      <ActPicker {...props} />
      <OperationInputs {...props} />
    </form>
  );
};

export const OperationFormActions = ({
  model,
  current,
  offered,
  onClose,
  formId,
  preview,
}: Omit<OperationFormProps, "onAct"> & { preview: RefObject<HTMLButtonElement | null> }) => {
  const p = usePresentation();
  const paused =
    useRequesterContext()?.maintenance.active === true ||
    current?.advice.maintenance.active === true;
  const allowed = offered.includes(model.entry.kind) && !paused;
  return (
    <>
      {onClose === undefined ? null : (
        <Button variant="secondary" isDisabled={model.busy} onPress={onClose}>
          {p.text("ui.close")}
        </Button>
      )}
      {model.conflict === null ? (
        <Button
          ref={preview}
          type="submit"
          {...(formId === undefined ? {} : { form: formId })}
          isDisabled={model.busy || model.revalidating || !allowed}
        >
          {p.text(model.phase === "PREVIEWING" ? "ui.previewing" : "label.reviewTitle")}
        </Button>
      ) : null}
    </>
  );
};
