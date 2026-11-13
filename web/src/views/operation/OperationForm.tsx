import { Entries } from "../../ui/regions/Entries";
import { Button } from "../../ui/Button";
import { Select } from "../../ui/Select";
import { OperationErrors } from "./OperationErrors";
import { useRef, useEffect, type RefObject } from "react";
import { TaskInputs } from "../../components/TaskInputs";
import { ScreenView } from "../../ui/ScreenView";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { taskForAct } from "../../protocol/taskCatalog";
import type { CurrentCase } from "../../protocol/types";
import type { FormValues, FormValue } from "../../protocol/forms";
import type { Notice } from "../../protocol/notices";
import type { HumanRejection, OperationTaskDefinition } from "@contracts/client";
import type { TaskBindingSource } from "../../operation/taskForm";
import type { OperationSelection } from "../../protocol/operationCatalog";

type FormModel = {
  entry: OperationSelection;
  task: OperationTaskDefinition;
  bindingSource: TaskBindingSource;
  values: FormValues;
  fieldErrors: Readonly<Record<string, Notice>>;
  rejection: HumanRejection | null;
  notice: Notice | null;
  busy: boolean;
  revalidating: boolean;
  phase: string;
  priorUncertain: boolean;
  conflict: unknown;
  edit: (input: string, value: FormValue) => void;
  editFields: (values: FormValues, omitted?: ReadonlyArray<string>) => void;
  preview: () => Promise<void>;
};
export type OperationFormProps = {
  model: FormModel;
  current: CurrentCase | null;
  offered: ReadonlyArray<string>;
  onAct: (kind: string) => void;
  onClose?: () => void;
  formId?: string;
  onMount?: (form: HTMLFormElement) => void;
  onFieldBlur?: (control: EventTarget | null) => void;
};
const PaymentContext = ({ current }: { current: CurrentCase | null }) => {
  const p = usePresentation();
  const payment = current?.screen.regions.find((region) => region.role === "PAYMENTS");
  if (current === null || payment?.content.kind !== "TABLE") {
    return null;
  }
  const entries = (payment.content.sections ?? [])
    .flatMap((section) => section.entries)
    .filter((entry) => entry.key === "remaining");
  return (
    <div className="payment-input-context">
      <Entries entries={entries} timeZone={current.basis.observation.timeZone} />
      <p>{p.text("field.paidCurrency.hint")}</p>
    </div>
  );
};
const OperationInputs = ({ model, current, offered }: OperationFormProps) => {
  const p = usePresentation();
  const creation = model.task.id === "case.open" && current === null;
  const allowed = offered.includes(model.entry.kind);
  return (
    <>
      {current === null ? null : (
        <ScreenView
          view={current.screen}
          roles={["IDENTITY"]}
          timeZone={current.basis.observation.timeZone}
        />
      )}
      <p className="operation-meaning">{renderCatalogKey(p, model.task.meaningId)}</p>
      {model.task.id === "case.recordPayment" ? <PaymentContext current={current} /> : null}
      {model.task.id === "case.correct" ? <p>{p.text("ui.correctionHint")}</p> : null}
      {offered.length === 0 && !creation ? null : (
        <TaskInputs
          task={model.task}
          binding={model.bindingSource}
          current={current}
          values={model.values}
          disabled={
            model.busy ||
            model.revalidating ||
            model.phase === "REVIEWING" ||
            (!allowed && !creation)
          }
          errors={model.fieldErrors}
          onChange={model.edit}
          onFieldsChange={model.editFields}
        />
      )}
    </>
  );
};
export const OperationForm = (props: OperationFormProps) => {
  const { model, offered, onMount, onFieldBlur } = props;
  const p = usePresentation();
  const form = useRef<HTMLFormElement>(null);
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
    if (model.notice !== null) {
      form.current?.querySelector<HTMLElement>("[data-error-summary]")?.focus();
    }
  }, [model.notice]);
  const allowed = offered.includes(model.entry.kind);
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
      {model.entry.subject === "CASE" || offered.length < 2 ? null : (
        <Select
          id="act-picker"
          label={p.text("ui.peopleAction")}
          value={model.entry.kind}
          disabled={model.busy || model.phase === "REVIEWING" || model.priorUncertain}
          options={offered.map((act) => ({
            value: act,
            label: renderCatalogKey(p, taskForAct(model.entry.subject, act).labelId),
          }))}
          onChange={props.onAct}
        />
      )}
      <OperationInputs {...props} />
    </form>
  );
};
export const OperationFormActions = ({
  model,
  offered,
  onClose,
  formId,
  preview,
}: Omit<OperationFormProps, "onAct"> & { preview: RefObject<HTMLButtonElement | null> }) => {
  const p = usePresentation();
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
          isDisabled={model.busy || model.revalidating || !offered.includes(model.entry.kind)}
        >
          {p.text(model.phase === "PREVIEWING" ? "ui.previewing" : "label.reviewTitle")}
        </Button>
      ) : null}
    </>
  );
};
