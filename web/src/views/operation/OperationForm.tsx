import { useRef, useEffect } from "react";
import { ReferenceSummary } from "../../components/ReferenceSummary";
import { ProtocolFields } from "../../components/ProtocolFields";
import { NoticeView } from "../../presentation/Message";
import { usePresentation } from "../../presentation/context";
import { operationDocument, visibleOperationSchema } from "../../interaction/operationForm";
import type { CurrentCase } from "../../api/service";
import type { OperationModel } from "../../hooks/operation/useOperation";

export type OperationFormProps = {
  model: OperationModel;
  current: CurrentCase | null;
  offered: ReadonlyArray<string>;
  onCommand: (kind: string) => void;
  onClose?: () => void;
};
const CommandPicker = ({ model, offered, onCommand }: OperationFormProps) => {
  const p = usePresentation();
  return offered.length < 2 ? null : (
    <label>
      {p.text(model.values["scope"] === "CASE" ? "ui.command" : "ui.authorityAction")}
      <select
        id="command-picker"
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
      {offered.length === 0 ? null : (
        <ProtocolFields
          document={operationDocument}
          node={visibleOperationSchema(model.values, current)}
          path=""
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
export const OperationForm = (props: OperationFormProps) => {
  const { model, current, offered, onClose } = props;
  const p = usePresentation();
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
      <CommandPicker {...props} />
      <OperationInputs {...props} />
      <div className="actions">
        <button type="submit" disabled={model.busy || model.phase === "REVIEWING" || !allowed}>
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
