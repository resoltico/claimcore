import { usePresentation } from "../presentation/context";
import { renderCatalogKey } from "../presentation/messages";
import { isTerm, isVocabulary } from "../presentation/terms";
import { CatalogField } from "./CatalogField";
import { AssigneeInput } from "./AssigneeInput";
import { roleAdmitted } from "../operation/roleAvailability";
import { CorrectionInputs } from "./CorrectionInputs";
import { NoticeView } from "../presentation/Message";
import { productMetadata } from "../protocol/productMetadata";
import { taskInput } from "../protocol/taskCatalog";
import {
  isFormCollection,
  formValueText,
  type FormValues,
  type FormValue,
} from "../protocol/forms";
import type { CurrentCase, FieldCatalogEntry } from "../protocol/types";
import type { Notice } from "../protocol/notices";
import type { OperationTaskDefinition, TaskInputId } from "@contracts/client";
import type { TaskBindingSource } from "../operation/taskForm";

type Input = OperationTaskDefinition["inputs"][number];
type Props = {
  task: OperationTaskDefinition;
  binding: TaskBindingSource;
  current: CurrentCase | null;
  values: FormValues;
  disabled: boolean;
  errors: Readonly<Record<string, Notice>>;
  principalKind?: string | undefined;
  onChange: (input: string, value: FormValue) => void;
  onFieldsChange: (values: FormValues, remove?: ReadonlyArray<string>) => void;
};
import { taskRowInputKey } from "../operation/taskIssues";
const scalar = (id: TaskInputId) => {
  const key = taskInput(id).key;
  return [...productMetadata.catalog.fields, ...productMetadata.catalog.inputFields].find(
    (field): field is FieldCatalogEntry => field.scalar !== null && field.name === key,
  );
};
const caption = (p: ReturnType<typeof usePresentation>, id: TaskInputId, value: string): string => {
  const vocabulary = taskInput(id).vocabulary;
  if (vocabulary === null || !isVocabulary(vocabulary) || !isTerm(vocabulary, value)) {
    throw new Error("The declared choice has no Core presentation term.");
  }
  return p.term(vocabulary, value);
};
const choicesFor = (input: Input, current: CurrentCase | null): ReadonlyArray<string> => {
  if (input.id === "group:registration") {
    return current?.context.correction?.registration ?? input.choices;
  }
  if (input.id === "group:decision") {
    return current?.context.correction?.decision ?? input.choices;
  }
  return input.choices;
};
const seed = (current: CurrentCase | null, id: TaskInputId): string => {
  if (current === null) {
    return "";
  }
  switch (id) {
    case "field:responsibleActor":
      return current.context.seeds.work?.responsible ?? "";
    case "field:handlingSummary":
      return current.context.seeds.work?.summary ?? "";
    default: {
      const metadata = taskInput(id);
      const source =
        metadata.group === "registration"
          ? current.context.seeds.registration
          : metadata.group === "decision"
            ? current.context.seeds.decision
            : null;
      if (source === null) {
        return "";
      }
      // A flat typed input seed, never saved display or a nested transport path.
      return Object.entries(source).find(([key]) => key === metadata.key)?.[1] ?? "";
    }
  }
};
const ErrorText = ({ value }: { value: Notice | undefined }) =>
  value === undefined ? null : (
    <p role="alert">
      <NoticeView value={value} />
    </p>
  );
const InputControl = ({
  input,
  errorKey = input.id,
  ...props
}: Props & { input: Input; errorKey?: string }) => {
  const p = usePresentation();
  const metadata = taskInput(input.id);
  const label = renderCatalogKey(p, metadata.labelId);
  const error = props.errors[errorKey];
  const options = choicesFor(input, props.current);
  const principalKind =
    props.principalKind ??
    (props.binding.kind === "AUTHORITY" ? props.binding.principal?.kind : undefined) ??
    formValueText(props.values["input:principal.kind"]);
  const unavailable = (value: string) =>
    input.id === "input:role" &&
    (props.task.act === "GRANT" || props.task.act === "ADMIT") &&
    !roleAdmitted(
      value,
      principalKind,
      formValueText(props.values["input:scope"]) || "INSTALLATION",
    );

  if (options.length > 0) {
    const selected = formValueText(props.values[input.id]);
    const optional = input.mode === "OPTIONAL";
    return (
      <fieldset id={`field-${errorKey}`} disabled={props.disabled}>
        <legend>{label}</legend>
        {optional ? (
          <label>
            <input
              type="radio"
              name={errorKey}
              value=""
              checked={selected === ""}
              onChange={() => props.onChange(input.id, "")}
            />
            {p.text("ui.leaveUnchanged")}
          </label>
        ) : null}
        {options.map((value) => {
          const blocked = unavailable(value);
          const hint = blocked
            ? roleAdmitted(value, principalKind)
              ? "ui.roleNotAvailableAtScope"
              : "ui.roleNotAvailableForService"
            : null;
          const hintId = errorKey + "-" + value + "-admission";
          return (
            <span key={value} className="choice-option">
              <label>
                <input
                  type="radio"
                  name={errorKey}
                  value={value}
                  disabled={props.disabled || blocked}
                  aria-describedby={hint === null ? undefined : hintId}
                  checked={
                    (selected ||
                      (input.id === "input:scope"
                        ? "INSTALLATION"
                        : metadata.kind === "GROUP"
                          ? "KEEP"
                          : "")) === value
                  }
                  onChange={() => props.onChange(input.id, value)}
                />
                {caption(p, input.id, value)}
              </label>
              {hint === null ? null : <small id={hintId}>{p.text(hint)}</small>}
            </span>
          );
        })}
        {input.id === "input:role" && selected === "" ? <p>{p.text("ui.chooseRole")}</p> : null}
        <ErrorText value={error} />
      </fieldset>
    );
  }
  const field = scalar(input.id);
  if (field !== undefined) {
    const common = {
      field,
      inputName: errorKey,
      value: formValueText(props.values[input.id]),
      disabled: props.disabled,
      error,
      onChange: (value: string) => props.onChange(input.id, value),
    };
    return input.id === "field:responsibleActor" && props.current !== null ? (
      <AssigneeInput {...common} current={props.current} />
    ) : (
      <CatalogField {...common} />
    );
  }
  return (
    <div className="text-field">
      <label htmlFor={errorKey}>{label}</label>
      <input
        id={errorKey}
        name={errorKey}
        type="text"
        autoComplete="off"
        dir="auto"
        value={formValueText(props.values[input.id])}
        disabled={props.disabled}
        aria-invalid={error !== undefined || undefined}
        onChange={(event) => props.onChange(input.id, event.target.value)}
      />
      {metadata.hintId === null ? null : <small>{renderCatalogKey(p, metadata.hintId)}</small>}
      <ErrorText value={error} />
    </div>
  );
};
const SelectedValue = ({ input, peer, ...props }: Props & { input: Input; peer?: Input }) => {
  const p = usePresentation();
  const members = peer === undefined ? [input] : [input, peer];
  const ids = members.map((input) => input.id);
  const selected = ids.some((id) => Object.hasOwn(props.values, id));
  const field = productMetadata.catalog.fields.find(
    (field) => field.name === taskInput(input.id).key,
  );
  const first = renderCatalogKey(p, taskInput(input.id).labelId);
  const title =
    peer === undefined
      ? first
      : p.text("ui.pairedFacts", {
          first,
          second: renderCatalogKey(p, taskInput(peer.id).labelId),
        });
  const clear = field?.allowsAbsence === true && props.task.id !== "CASE:OPEN";
  return (
    <fieldset className="selected-fact">
      <legend>
        <label>
          <input
            type="checkbox"
            disabled={props.disabled}
            checked={selected}
            data-selects-fields={members.map((input) => taskInput(input.id).key).join(" ")}
            onChange={(event) =>
              props.onFieldsChange(
                event.target.checked
                  ? Object.fromEntries(
                      members.map((input) => [input.id, seed(props.current, input.id)]),
                    )
                  : {},
                event.target.checked ? [] : ids,
              )
            }
          />
          {p.text(props.task.id === "CASE:OPEN" ? "ui.recordOptionalFact" : "ui.changeFact", {
            field: title,
          })}
        </label>
      </legend>
      {selected && clear ? (
        <label>
          <input
            type="checkbox"
            disabled={props.disabled}
            checked={props.values[input.id] === null}
            onChange={(event) =>
              props.onChange(input.id, event.target.checked ? null : seed(props.current, input.id))
            }
          />
          {p.text("ui.clearValue")}
        </label>
      ) : null}
      {selected && props.values[input.id] !== null
        ? members.map((input) => <InputControl key={input.id} input={input} {...props} />)
        : null}
    </fieldset>
  );
};
const permitted = (input: Input, props: Props): boolean => {
  if (input.source !== "AUTHORED") {
    return false;
  }
  if (
    input.id === "field:caseReference" &&
    props.task.subject !== "AUTHORITY" &&
    props.binding.kind !== "NEW_CASE"
  ) {
    return false;
  }
  if (
    input.id === "input:noteId" ||
    input.id === "input:reminderId" ||
    input.id === "input:paymentId"
  ) {
    return false;
  }
  if (
    props.binding.kind === "AUTHORITY" &&
    props.binding.principal !== undefined &&
    (input.id === "input:principal.kind" ||
      input.id === "input:principal.issuer" ||
      input.id === "input:principal.subject")
  ) {
    return false;
  }
  if (props.task.subject === "WORK" && props.current !== null) {
    if (
      (input.id === "field:responsibleActor" || input.id === "input:responsibleActorAction") &&
      !props.current.context.editableInputs.includes("field:responsibleActor")
    ) {
      return false;
    }
    if (
      input.id === "field:handlingSummary" &&
      !props.current.context.editableInputs.includes("field:handlingSummary")
    ) {
      return false;
    }
    if (
      input.id === "field:responsibleActor" &&
      props.values["input:responsibleActorAction"] !== "SET"
    ) {
      return false;
    }
  }
  if (
    props.task.subject === "AUTHORITY" &&
    input.id === "field:caseReference" &&
    (props.values["input:scope"] ?? "INSTALLATION") !== "CASE"
  ) {
    return false;
  }
  return (
    input.mode !== "UPDATE" ||
    props.current === null ||
    props.current.context.editableInputs.includes(input.id)
  );
};
const Fields = ({ inputs, ...props }: Props & { inputs: ReadonlyArray<Input> }) => {
  const render = (input: Input) => {
    const field = productMetadata.catalog.fields.find(
      (field) => field.name === taskInput(input.id).key,
    );
    const peer =
      field?.pairs === null || field?.pairs === undefined
        ? undefined
        : inputs.find((input) => taskInput(input.id).key === field.pairs);
    const selectable =
      input.mode === "UPDATE" ||
      (input.mode === "OPTIONAL" &&
        taskInput(input.id).kind === "FIELD" &&
        input.id !== "field:responsibleActor");
    if (peer !== undefined && field?.scalar.kind === "CURRENCY" && selectable) {
      return null;
    }
    return selectable ? (
      <SelectedValue
        key={input.id}
        input={input}
        {...(peer === undefined ? {} : { peer })}
        {...props}
      />
    ) : (
      <InputControl key={input.id} input={input} {...props} />
    );
  };
  return inputs.map(render);
};
const GrantRows = ({
  collection,
  ...props
}: Props & { collection: OperationTaskDefinition["collections"][number] }) => {
  const p = usePresentation();
  const value = props.values[collection.id];
  const rows = isFormCollection(value) ? value.rows : [];
  const update = (next: typeof rows) =>
    props.onChange(collection.id, { kind: "COLLECTION", rows: next });
  return (
    <fieldset id={`field-${collection.id}`} disabled={props.disabled}>
      <legend>{renderCatalogKey(p, taskInput(collection.input).labelId)}</legend>
      {rows.map((row, index) => (
        <fieldset key={row.key}>
          <legend>{p.text("ui.grantNumber", { number: p.integer(index + 1) })}</legend>
          {collection.inputs
            .filter(
              (input) =>
                input.id !== "field:caseReference" ||
                (row.values["input:scope"] ?? "INSTALLATION") === "CASE",
            )
            .map((input) => (
              <InputControl
                key={input.id}
                {...props}
                input={input}
                current={null}
                principalKind={
                  (props.binding.kind === "AUTHORITY"
                    ? props.binding.principal?.kind
                    : undefined) ?? formValueText(props.values["input:principal.kind"])
                }
                values={row.values}
                errorKey={taskRowInputKey(collection.id, row.key, input.id)}
                onChange={(id, value) =>
                  update(
                    rows.map((previous) =>
                      previous.key === row.key
                        ? { ...previous, values: { ...previous.values, [id]: value } }
                        : previous,
                    ),
                  )
                }
              />
            ))}
          <button
            type="button"
            disabled={props.disabled || rows.length <= collection.minimumRows}
            onClick={() => update(rows.filter((previous) => previous.key !== row.key))}
          >
            {p.text("ui.removeGrant")}
          </button>
        </fieldset>
      ))}
      <ErrorText value={props.errors[collection.id] ?? props.errors[collection.input]} />
      <button
        type="button"
        disabled={props.disabled || rows.length >= collection.maximumRows}
        onClick={() =>
          update([...rows, { key: crypto.randomUUID(), values: { "input:scope": "INSTALLATION" } }])
        }
      >
        {p.text("ui.addGrant")}
      </button>
    </fieldset>
  );
};
export const TaskInputs = (props: Props) => {
  const visible = props.task.inputs.filter((input) => permitted(input, props));
  const groups = visible.filter((input) => taskInput(input.id).kind === "GROUP");
  const groupNames = new Set(groups.map((input) => taskInput(input.id).group));
  const ordinary = visible.filter(
    (input) => taskInput(input.id).kind !== "GROUP" && !groupNames.has(taskInput(input.id).group),
  );
  return (
    <>
      <Fields inputs={ordinary} {...props} />
      {groups.map((group) => {
        const metadata = taskInput(group.id);
        const children = visible.filter(
          (input) =>
            input !== group &&
            taskInput(input.id).kind === "FIELD" &&
            taskInput(input.id).group === metadata.group,
        );
        return (
          <section className="correction-group" key={group.id}>
            <InputControl input={group} {...props} />
            {props.values[group.id] === "UPDATE" ? (
              <CorrectionInputs group={metadata.group} current={props.current}>
                <Fields inputs={children} {...props} />
              </CorrectionInputs>
            ) : null}
          </section>
        );
      })}
      {props.task.collections.map((collection) => (
        <GrantRows key={collection.id} collection={collection} {...props} />
      ))}
    </>
  );
};
