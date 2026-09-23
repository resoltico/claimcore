import { NoticeView } from "../../presentation/Message";
import { usePresentation } from "../../presentation/context";
import { Button } from "react-aria-components/Button";
import { Form } from "react-aria-components/Form";
import type { RefObject } from "react";
import type { CurrentCase, DefinitionPayload } from "../../api/v2";
import { DescriptorField } from "../../components/DescriptorField";
import { isCorrectionValues, type CorrectionGroupName } from "../../domain/metadata";
import type { OperationEditorModel } from "./editorTypes";

type OperationFormProps = {
  definition: DefinitionPayload;
  current: CurrentCase | null;
  model: OperationEditorModel;
  onClose: () => void;
  prepareButtonRef: RefObject<HTMLButtonElement | null>;
};

const CommandPicker = ({ model }: Pick<OperationFormProps, "model">) => {
  const p = usePresentation();
  if (model.available.length < 2) return null;
  const change = (next: string): void => {
    const command = model.available.find((candidate) => candidate === next);
    if (command === undefined || command === model.state.command) return;
    if (model.dirty) model.setPendingCommand(command);
    else model.applyCommand(command);
  };
  return (
    <>
      <label htmlFor="command-picker">{p.text("ui.command")}</label>
      <select
        id="command-picker"
        value={model.state.command}
        disabled={model.locked}
        onChange={(event) => change(event.target.value)}
      >
        {model.available.map((kind) => (
          <option key={kind} value={kind}>
            {p.commandLabel(kind)}
          </option>
        ))}
      </select>
    </>
  );
};

const Reference = ({ current, model }: Pick<OperationFormProps, "current" | "model">) => {
  const p = usePresentation();
  if (current === null && model.referenceField !== undefined)
    return (
      <DescriptorField
        field={model.referenceField}
        value={model.state.caseReference}
        error={
          model.state.fieldError?.name === model.referenceField.name
            ? model.state.fieldError.message
            : undefined
        }
        onChange={model.editReference}
      />
    );
  return current === null ? null : (
    <p>{p.text("ui.reference", { reference: model.state.caseReference })}</p>
  );
};

const AuthoringFields = ({ model }: Pick<OperationFormProps, "model">) => {
  const p = usePresentation();
  if (isCorrectionValues(model.state.values)) return null;
  const values = model.state.values;
  return (
    <>
      {model.fields.map(({ field }) => (
        <DescriptorField
          key={field.name}
          field={field}
          value={values[field.name] ?? ""}
          error={
            model.state.fieldError?.name === field.name ? model.state.fieldError.message : undefined
          }
          onChange={(value) => model.edit(field.name, value)}
        />
      ))}
      {model.fields.length === 0 ? <p>{p.text("ui.noAuthoredValues")}</p> : null}
    </>
  );
};

const CorrectionMode = ({
  name,
  mode,
  actions,
  locked,
  onChange,
}: {
  name: CorrectionGroupName;
  mode: string;
  actions: ReadonlyArray<"KEEP" | "REPLACE" | "CLEAR">;
  locked: boolean;
  onChange: (next: string) => void;
}) => {
  const p = usePresentation();
  return (
    <>
      <label htmlFor={`correction-${name}-mode`}>{p.text("ui.action")}</label>
      <select
        id={`correction-${name}-mode`}
        value={mode}
        disabled={locked}
        onChange={(event) => onChange(event.target.value)}
      >
        {actions.map((action) => (
          <option key={p.token(action)} value={p.token(action)}>
            {p.token(action)}
          </option>
        ))}
      </select>
    </>
  );
};

const CorrectionGroup = ({
  model,
  name,
  label,
  meaning,
  actions,
  fields,
}: {
  model: OperationEditorModel;
  name: CorrectionGroupName;
  label: string;
  meaning: string;
  actions: ReadonlyArray<"KEEP" | "REPLACE" | "CLEAR">;
  fields: ReadonlyArray<OperationEditorModel["groups"][number]["fields"][number]>;
}) => {
  if (!isCorrectionValues(model.state.values)) return null;
  const values = model.state.values;
  const value = values[name];
  return (
    <fieldset>
      <legend>{label}</legend>
      <p>{meaning}</p>
      <CorrectionMode
        name={name}
        mode={value.mode}
        actions={actions}
        locked={model.locked}
        onChange={(next) => model.setCorrectionMode(name, next)}
      />
      {value.mode !== "REPLACE"
        ? null
        : fields.map((field) => (
            <DescriptorField
              key={field.name}
              field={field}
              inputName={`${name}.${field.name}`}
              value={value.values[field.name] ?? ""}
              error={
                model.state.fieldError?.name === field.name ||
                model.state.fieldError?.name === `${name}.${field.name}`
                  ? model.state.fieldError.message
                  : undefined
              }
              onChange={(next) => model.editCorrection(name, field.name, next)}
            />
          ))}
    </fieldset>
  );
};

const CorrectionFields = ({ model }: Pick<OperationFormProps, "model">) => {
  const p = usePresentation();
  return model.groups.length === 0 ? null : (
    <>
      <p>{p.text("ui.correctionHint")}</p>
      {model.groups.map(({ group, fields }) => (
        <CorrectionGroup
          key={group.name}
          model={model}
          name={group.name as CorrectionGroupName}
          label={p.groupLabel(group.name)}
          meaning={p.groupMeaning(group.name)}
          actions={group.actions}
          fields={fields}
        />
      ))}
    </>
  );
};

const Actions = ({
  model,
  onClose,
  prepareButtonRef,
}: Pick<OperationFormProps, "model" | "onClose" | "prepareButtonRef">) => {
  const p = usePresentation();
  return (
    <div className="actions">
      <Button ref={prepareButtonRef} type="submit" isDisabled={!model.canPrepare}>
        {model.state.delivery === "PREPARING" ? p.text("ui.preparing") : p.text("ui.prepareExact")}
      </Button>
      {model.state.delivery === "PREPARATION_UNKNOWN" ? (
        <Button onPress={() => void model.prepare()}>{p.text("ui.retryPrepare")}</Button>
      ) : null}
      <Button className="secondary-button" onPress={onClose} isDisabled={model.locked}>
        {p.text("ui.backWithoutPreparing")}
      </Button>
    </div>
  );
};

export const OperationForm = ({
  current,
  model,
  onClose,
  prepareButtonRef,
}: OperationFormProps) => (
  <Form
    onSubmit={(event) => {
      event.preventDefault();
      void model.prepare();
    }}
  >
    <Reference current={current} model={model} />
    <CommandPicker model={model} />
    <AuthoringFields model={model} />
    <CorrectionFields model={model} />
    {model.state.message === null ? null : (
      <p className="error" role="alert">
        <NoticeView value={model.state.message} />
      </p>
    )}
    <Actions model={model} onClose={onClose} prepareButtonRef={prepareButtonRef} />
  </Form>
);
