import { Button } from "react-aria-components/Button";
import { Form } from "react-aria-components/Form";
import type { RefObject } from "react";
import type { CurrentCase, DefinitionPayload } from "../../api/v2";
import { DescriptorField } from "../../components/DescriptorField";
import { commandFor, isCorrectionValues, type CorrectionGroupName } from "../../domain/metadata";
import type { OperationEditorModel } from "./editorTypes";

type OperationFormProps = {
  definition: DefinitionPayload;
  current: CurrentCase | null;
  model: OperationEditorModel;
  onClose: () => void;
  prepareButtonRef: RefObject<HTMLButtonElement | null>;
};

const CommandPicker = ({ definition, model }: Pick<OperationFormProps, "definition" | "model">) => {
  if (model.available.length < 2) return null;
  const change = (next: string): void => {
    const command = model.available.find((candidate) => candidate === next);
    if (command === undefined || command === model.state.command) return;
    if (model.dirty) model.setPendingCommand(command);
    else model.applyCommand(command);
  };
  return (
    <>
      <label htmlFor="command-picker">Command</label>
      <select
        id="command-picker"
        value={model.state.command}
        disabled={model.locked}
        onChange={(event) => change(event.target.value)}
      >
        {model.available.map((kind) => (
          <option key={kind} value={kind}>
            {commandFor(definition.definition, kind).label}
          </option>
        ))}
      </select>
    </>
  );
};

const Reference = ({ current, model }: Pick<OperationFormProps, "current" | "model">) => {
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
    <p>
      Case reference: <bdi>{model.state.caseReference}</bdi>
    </p>
  );
};

const AuthoringFields = ({ model }: Pick<OperationFormProps, "model">) => {
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
      {model.fields.length === 0 ? <p>This command has no authored values.</p> : null}
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
}) => (
  <>
    <label htmlFor={`correction-${name}-mode`}>Action</label>
    <select
      id={`correction-${name}-mode`}
      value={mode}
      disabled={locked}
      onChange={(event) => onChange(event.target.value)}
    >
      {actions.map((action) => (
        <option key={action} value={action}>
          {action}
        </option>
      ))}
    </select>
  </>
);

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

const CorrectionFields = ({ model }: Pick<OperationFormProps, "model">) =>
  model.groups.length === 0 ? null : (
    <>
      <p>Each correction group is explicit. KEEP preserves its accepted values.</p>
      {model.groups.map(({ group, fields }) => (
        <CorrectionGroup
          key={group.name}
          model={model}
          name={group.name as CorrectionGroupName}
          label={group.label}
          meaning={group.meaning}
          actions={group.actions}
          fields={fields}
        />
      ))}
    </>
  );

const Actions = ({
  model,
  onClose,
  prepareButtonRef,
}: Pick<OperationFormProps, "model" | "onClose" | "prepareButtonRef">) => (
  <div className="actions">
    <Button ref={prepareButtonRef} type="submit" isDisabled={!model.canPrepare}>
      {model.state.delivery === "PREPARING" ? "Preparing…" : "Prepare exact request"}
    </Button>
    {model.state.delivery === "PREPARATION_UNKNOWN" ? (
      <Button onPress={() => void model.prepare()}>Retry exact prepare</Button>
    ) : null}
    <Button className="secondary-button" onPress={onClose} isDisabled={model.locked}>
      Back without preparing
    </Button>
  </div>
);

export const OperationForm = ({
  definition,
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
    <CommandPicker definition={definition} model={model} />
    <AuthoringFields model={model} />
    <CorrectionFields model={model} />
    {model.state.message === null ? null : (
      <p className="error" role="alert">
        {model.state.message}
      </p>
    )}
    <Actions model={model} onClose={onClose} prepareButtonRef={prepareButtonRef} />
  </Form>
);
