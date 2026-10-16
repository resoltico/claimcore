import { CorrectionMode } from "./CorrectionMode";
import { ReferenceSummary } from "../../components/ReferenceSummary";
import { NoticeView } from "../../presentation/Message";
import { usePresentation } from "../../presentation/context";
import { Button } from "react-aria-components/Button";
import { Form } from "react-aria-components/Form";
import type { RefObject } from "react";
import type { CurrentCase, ProductMetadata } from "../../api/service";
import { DescriptorField } from "../../components/DescriptorField";
import {
  correctionGroupName,
  isGroupedDraftValues,
  type CorrectionGroupName,
} from "../../interaction/metadata";
import type { OperationEditorModel } from "../../interaction/editorTypes";

type OperationFormProps = {
  definition: ProductMetadata;
  current: CurrentCase | null;
  model: OperationEditorModel;
  onClose: () => void;
  previewButtonRef: RefObject<HTMLButtonElement | null>;
};

const CommandPicker = ({ model }: Pick<OperationFormProps, "model">) => {
  const p = usePresentation();
  if (model.available.length < 2) {
    return null;
  }
  const change = (next: string): void => {
    const command = model.available.find((candidate) => candidate === next);
    if (command === undefined || command === model.state.command) {
      return;
    }
    if (model.dirty) {
      model.setPendingDiscard(command);
    } else {
      model.applyCommand(command);
    }
  };
  return (
    <>
      <label htmlFor="command-picker">{p.text("ui.command")}</label>
      <select
        id="command-picker"
        value={model.state.command}
        disabled={!model.canPreview}
        onChange={(event) => {
          change(event.target.value);
        }}
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
  if (current === null && model.referenceField !== undefined) {
    return (
      <DescriptorField
        field={model.referenceField}
        value={model.state.caseReference}
        disabled={!model.canPreview}
        error={
          model.state.fieldError?.name === model.referenceField.name
            ? model.state.fieldError.message
            : undefined
        }
        onChange={model.editReference}
      />
    );
  }
  return current === null ? null : (
    <p>
      <ReferenceSummary
        id="ui.reference"
        values={{ label: p.fieldLabel("caseReference"), reference: model.state.caseReference }}
      />
    </p>
  );
};

const AuthoringFields = ({ model }: Pick<OperationFormProps, "model">) => {
  const p = usePresentation();
  if (isGroupedDraftValues(model.state.values)) {
    return null;
  }
  const { values } = model.state;
  return (
    <>
      {model.fields.map(({ field }) => (
        <DescriptorField
          key={field.name}
          field={field}
          value={values[field.name] ?? ""}
          disabled={!model.canPreview}
          error={
            model.state.fieldError?.name === field.name ? model.state.fieldError.message : undefined
          }
          onChange={(value) => {
            model.edit(field.name, value);
          }}
        />
      ))}
      {model.fields.length === 0 ? <p>{p.text("ui.noAuthoredValues")}</p> : null}
    </>
  );
};

const ReplacementFields = ({
  model,
  name,
  values,
  fields,
}: {
  model: OperationEditorModel;
  name: CorrectionGroupName;
  values: Record<string, string>;
  fields: ReadonlyArray<OperationEditorModel["groups"][number]["fields"][number]>;
}) =>
  fields.map((field) => (
    <DescriptorField
      key={field.name}
      field={field}
      inputName={`${name}.${field.name}`}
      value={values[field.name] ?? ""}
      disabled={!model.canPreview}
      error={
        model.state.fieldError?.name === field.name ||
        model.state.fieldError?.name === `${name}.${field.name}`
          ? model.state.fieldError.message
          : undefined
      }
      onChange={(next) => {
        model.editCorrection(name, field.name, next);
      }}
    />
  ));

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
  if (!isGroupedDraftValues(model.state.values)) {
    return null;
  }
  const { values } = model.state;
  const value = values[name];
  if (value === undefined) {
    throw new Error(`The editor is missing correction group ${name}.`);
  }
  return (
    <fieldset>
      <legend>{label}</legend>
      <p>{meaning}</p>
      <CorrectionMode
        name={name}
        mode={value.mode}
        actions={actions}
        locked={!model.canPreview}
        error={
          model.state.fieldError?.name === name || model.state.fieldError?.name === `${name}.action`
            ? model.state.fieldError.message
            : undefined
        }
        onChange={(next) => {
          model.setCorrectionMode(name, next);
        }}
      />
      {value.mode === "REPLACE" ? (
        <ReplacementFields model={model} name={name} values={value.values} fields={fields} />
      ) : null}
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
          name={correctionGroupName(group.name)}
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
  current,
  model,
  onClose,
  previewButtonRef,
}: Pick<OperationFormProps, "current" | "model" | "onClose" | "previewButtonRef">) => {
  const p = usePresentation();
  return (
    <div className="actions">
      <Button ref={previewButtonRef} type="submit" isDisabled={!model.canPreview}>
        {model.state.delivery === "PREVIEWING"
          ? p.text("ui.previewing")
          : p.text("ui.previewOperation")}
      </Button>
      <Button className="secondary-button" onPress={onClose} isDisabled={model.locked}>
        {p.text(current === null ? "ui.backToCases" : "ui.backToCase")}
      </Button>
    </div>
  );
};

export const OperationForm = ({
  current,
  model,
  onClose,
  previewButtonRef,
}: OperationFormProps) => (
  <Form
    onSubmit={(event) => {
      event.preventDefault();
      void model.preview();
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
    <Actions
      current={current}
      model={model}
      onClose={onClose}
      previewButtonRef={previewButtonRef}
    />
  </Form>
);
