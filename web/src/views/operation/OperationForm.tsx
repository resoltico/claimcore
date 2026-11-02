import { useRequesterContext } from "../../hooks/useRequesterContext";
import { OperationErrors } from "./OperationErrors";
import { useRef, useEffect } from "react";
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
  offered: ReadonlyArray<string>;
  onAct: (kind: string) => void;
  onClose?: () => void;
};
const ActPicker = ({ model, offered, onAct }: OperationFormProps) => {
  const p = usePresentation();
  return model.entry.subject === "CASE" || offered.length < 2 ? null : (
    <label>
      {p.text("ui.authorityAction")}
      <select
        id="act-picker"
        value={model.entry.kind}
        disabled={model.busy || model.phase === "REVIEWING" || model.priorUncertain}
        onChange={(event) => {
          onAct(event.target.value);
        }}
      >
        {offered.map((kind) => (
          <option key={kind} value={kind}>
            {p.authorityLabel(kind)}
          </option>
        ))}
      </select>
    </label>
  );
};
const OperationInputs = ({ model, current, offered, fixedTarget }: OperationFormProps) => {
  const p = usePresentation();
  const allowed = offered.includes(model.entry.kind);
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
      {offered.length === 0 ? null : (
        <CatalogInputs
          entry={act}
          current={current}
          values={model.values}
          {...(fixedTarget === undefined ? {} : { fixedTarget })}
          disabled={model.busy || model.phase === "REVIEWING" || !allowed}
          fieldErrors={model.fieldErrors}
          onChange={model.edit}
          onFieldsChange={model.editFields}
        />
      )}
    </>
  );
};
const useReviewReturnFocus = (model: OperationModel) => {
  const preview = useRef<HTMLButtonElement>(null);
  const previousPhase = useRef(model.phase);
  useEffect(() => {
    if (
      previousPhase.current === "REVIEWING" &&
      model.phase === "EDITING" &&
      model.notice === null &&
      preview.current?.isConnected === true &&
      !preview.current.disabled
    ) {
      preview.current.focus();
    }
    previousPhase.current = model.phase;
  }, [model.phase, model.notice]);
  return preview;
};
export const OperationForm = (props: OperationFormProps) => {
  const { model, current, offered, onClose } = props;
  const p = usePresentation();
  const preview = useReviewReturnFocus(model);
  const form = useRef<HTMLFormElement>(null);
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
      ref={form}
      onSubmit={(event) => {
        event.preventDefault();
        if (allowed) {
          void model.preview();
        }
      }}
    >
      <OperationErrors model={model} />
      <ActPicker {...props} />
      <OperationInputs {...props} />
      <div className="actions">
        <button
          ref={preview}
          type="submit"
          disabled={model.busy || model.phase === "REVIEWING" || !allowed}
        >
          {p.text(model.phase === "PREVIEWING" ? "ui.previewing" : "label.reviewTitle")}
        </button>
        {onClose === undefined ? null : (
          <button type="button" disabled={model.busy} onClick={onClose}>
            {p.text(current === null ? "ui.backToCases" : "ui.backToCase")}
          </button>
        )}
      </div>
    </form>
  );
};
