import { useRef, useEffect } from "react";
import { ReferenceSummary } from "../../components/ReferenceSummary";
import { CatalogInputs } from "../../components/CatalogInputs";
import { NoticeView } from "../../presentation/Message";
import { usePresentation } from "../../presentation/context";
import type { CurrentCase } from "../../api/service";
import type { OperationModel } from "../../hooks/operation/useOperation";
import { productMetadata } from "../../protocol/productMetadata";

export type OperationFormProps = {
  model: OperationModel;
  current: CurrentCase | null;
  offered: ReadonlyArray<string>;
  onCommand: (kind: string) => void;
  onClose?: () => void;
};
const ActPicker = ({ model, offered, onCommand }: OperationFormProps) => {
  const p = usePresentation();
  return offered.length < 2 ? null : (
    <label>
      {p.text(model.values["scope"] === "CASE" ? "ui.act" : "ui.authorityAction")}
      <select
        id="act-picker"
        value={String(model.values["act.kind"])}
        disabled={model.busy || model.phase === "REVIEWING" || model.priorUncertain}
        onChange={(event) => {
          onCommand(event.target.value);
        }}
      >
        {offered.map((kind) => (
          <option key={kind} value={kind}>
            {model.values["scope"] === "CASE" ? p.commandLabel(kind) : p.authorityLabel(kind)}
          </option>
        ))}
      </select>
    </label>
  );
};
const OperationInputs = ({ model, current, offered }: OperationFormProps) => {
  const p = usePresentation();
  const allowed = offered.includes(String(model.values["act.kind"]));
  const act = productMetadata.catalog.acts.find(
    (item) => model.values["scope"] === "CASE" && item.kind === model.values["act.kind"],
  );
  return (
    <>
      {" "}
      {current === null ? null : (
        <p>
          <ReferenceSummary
            id="ui.reference"
            values={{
              label: p.fieldLabel("caseReference"),
              reference: String(model.values["caseReference"]),
            }}
          />
        </p>
      )}
      {act?.inputs.kind === "CORRECTION_GROUPS" ? <p>{p.text("ui.correctionHint")}</p> : null}
      {offered.length === 0 ? null : (
        <CatalogInputs
          current={current}
          values={model.values}
          disabled={model.busy || model.phase === "REVIEWING" || !allowed}
          errorPath={model.fieldError}
          errorNotice={model.notice}
          onChange={model.edit}
        />
      )}
      {model.notice === null ? null : (
        <p role="alert" tabIndex={-1} data-error-summary>
          <NoticeView value={model.notice} />
        </p>
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
    const input = [
      ...form.current!.querySelectorAll<HTMLInputElement | HTMLSelectElement>("input,select"),
    ].find((item) => item.name === model.fieldError);
    (input ?? form.current?.querySelector<HTMLElement>("[data-error-summary]"))?.focus();
  }, [model.fieldError, model.notice]);
  const allowed = offered.includes(String(model.values["act.kind"]));
  return (
    <form
      ref={form}
      onSubmit={(event) => {
        event.preventDefault();
        if (allowed) {
          void model.preview();
        }
      }}
    >
      <ActPicker {...props} />
      <OperationInputs {...props} />
      <div className="actions">
        <button
          ref={preview}
          type="submit"
          disabled={model.busy || model.phase === "REVIEWING" || !allowed}
        >
          {p.text(model.phase === "PREVIEWING" ? "ui.previewing" : "ui.previewOperation")}
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
