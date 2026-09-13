import { Button } from "react-aria-components/Button";
import { Form } from "react-aria-components/Form";
import type { RefObject } from "react";
import type { CurrentCase, DefinitionPayload } from "../../api/v2";
import { DescriptorField } from "../../components/DescriptorField";
import { commandFor } from "../../domain/metadata";
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

const AuthoringFields = ({ model }: Pick<OperationFormProps, "model">) => (
  <>
    {model.fields.map(({ field }) => (
      <DescriptorField
        key={field.name}
        field={field}
        value={model.state.values[field.name] ?? ""}
        error={
          model.state.fieldError?.name === field.name ? model.state.fieldError.message : undefined
        }
        onChange={(value) => model.edit(field.name, value)}
      />
    ))}
    {model.fields.length === 0 ? <p>This command has no authored values.</p> : null}
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
    {model.state.message === null ? null : (
      <p className="error" role="alert">
        {model.state.message}
      </p>
    )}
    <Actions model={model} onClose={onClose} prepareButtonRef={prepareButtonRef} />
  </Form>
);
