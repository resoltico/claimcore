import { codeSuggestions } from "../presentation/codeSuggestions";
import { TextInput, ChoiceControl } from "../ui/TextField";
import { MoneyField } from "../ui/MoneyField";
import { Select } from "../ui/Select";
import { grantFields, grantScope, grantTarget } from "../operation/grantTargetDraft";
import { affordanceSeed, type OperationAffordance } from "../operation/affordances";
import { usePresentation } from "../presentation/context";
import { renderCatalogKey } from "../presentation/messages";
import { isTerm, isVocabulary } from "../presentation/terms";
import { CatalogField } from "./CatalogField";
import { AssigneeInput } from "./AssigneeInput";
import { editGrantInput } from "../operation/operationDraft";
import { inputChoiceRestriction } from "../operation/inputChoices";
import { CorrectionInputs } from "./CorrectionInputs";
import { NoticeView } from "../presentation/Message";
import { productMetadata } from "../protocol/productMetadata";
import { taskInput, taskScalar } from "../protocol/taskCatalog";
import {
  isFormCollection,
  formValueText,
  type FormValues,
  type FormValue,
} from "../protocol/forms";
import type { CurrentCase } from "../protocol/types";
import type { Notice } from "../protocol/notices";
import type { OperationTaskDefinition, TaskInputId, OperationBinding } from "@contracts/client";

type Input = OperationTaskDefinition["inputs"][number];
type Props = {
  affordance?: OperationAffordance | undefined;
  task: OperationTaskDefinition;
  binding: OperationBinding;
  current: CurrentCase | null;
  values: FormValues;
  disabled: boolean;
  errors: Readonly<Record<string, Notice>>;
  constraintValues?: FormValues | undefined;
  onChange: (input: string, value: FormValue) => void;
  onFieldsChange: (values: FormValues, remove?: ReadonlyArray<string>) => void;
};
import { taskRowInputKey } from "../operation/taskIssues";
const caption = (p: ReturnType<typeof usePresentation>, id: TaskInputId, value: string): string => {
  const { vocabulary } = taskInput(id);
  if (vocabulary === null || !isVocabulary(vocabulary) || !isTerm(vocabulary, value)) {
    throw new Error("The declared choice has no Core presentation term.");
  }
  return p.term(vocabulary, value);
};
const choicesFor = (
  input: Input,
  affordance: OperationAffordance | undefined,
): ReadonlyArray<string> => {
  const narrowed: Readonly<Record<string, ReadonlyArray<string>>> | undefined = affordance?.choices;
  return narrowed?.[input.id] ?? input.choices;
};
const seed = (affordance: OperationAffordance | undefined, id: TaskInputId): string =>
  formValueText(affordanceSeed(affordance, id));
const ErrorText = ({ value, id }: { value: Notice | undefined; id?: string }) =>
  value === undefined ? null : (
    <p role="alert" id={id}>
      <NoticeView value={value} />
    </p>
  );
const helpIds = (hintId: string | null, error: Notice | undefined, key: string) =>
  [hintId === null ? null : `${key}-hint`, error === undefined ? null : `${key}-error`]
    .filter((id) => id !== null)
    .join(" ") || undefined;
const TaskInputHelp = ({
  metadata,
  error,
  control,
}: {
  metadata: { hintId: ReturnType<typeof taskInput>["hintId"] };
  error: Notice | undefined;
  control: string;
}) => {
  const p = usePresentation();
  return (
    <>
      {metadata.hintId === null ? null : (
        <p id={`${control}-hint`} className="field-description">
          {renderCatalogKey(p, metadata.hintId)}
        </p>
      )}
      <ErrorText value={error} id={`${control}-error`} />
    </>
  );
};
type InputProps = Props & { defaults: FormValues };
type ControlProps = InputProps & { input: Input; errorKey?: string; sharedErrorId?: string };
const choiceHint = (input: Input, props: Props, offered: ReadonlyArray<string>, value: string) => {
  if (!offered.includes(value)) {
    return "ui.choiceNotOffered";
  }
  const restriction = inputChoiceRestriction(input, value, {
    ...props.constraintValues,
    ...grantFields(props.values),
  });
  return restriction === undefined ? null : (restriction.messageId ?? "ui.choiceNotOffered");
};
const ChoiceInput = ({ input, errorKey = input.id, ...props }: ControlProps) => {
  const p = usePresentation();
  const metadata = taskInput(input.id);
  const label = renderCatalogKey(p, metadata.labelId);
  const error = props.errors[errorKey];
  const offered = choicesFor(input, props.affordance);
  const selected = formValueText(
    Object.hasOwn(props.values, input.id) ? props.values[input.id] : props.defaults[input.id],
  );
  const options =
    offered.includes(selected) || !input.choices.some((value) => value === selected)
      ? offered
      : [...offered, selected];
  const optional = input.mode === "OPTIONAL";
  const segmented = options.length + Number(optional) <= 5;
  return (
    <fieldset
      className="task-choice"
      id={`field-${errorKey}`}
      disabled={props.disabled}
      aria-invalid={error !== undefined || undefined}
      aria-describedby={helpIds(metadata.hintId, error, errorKey)}
    >
      <legend>{label}</legend>
      <div className={segmented ? "segmented-choice" : "radio-choice"}>
        {optional ? (
          <label className={selected === "" ? "selected" : undefined}>
            <ChoiceControl
              type="radio"
              name={errorKey}
              value=""
              checked={selected === ""}
              onChange={() => {
                props.onChange(input.id, "");
              }}
            />
            {p.text("ui.leaveUnchanged")}
          </label>
        ) : null}
        {options.map((value) => {
          const hint = choiceHint(input, props, offered, value);
          const hintId = `${errorKey}-${value}-admission`;
          return (
            <span key={value} className="choice-option">
              <label className={selected === value ? "selected" : undefined}>
                <ChoiceControl
                  type="radio"
                  name={errorKey}
                  value={value}
                  disabled={props.disabled || hint !== null}
                  aria-describedby={hint === null ? undefined : hintId}
                  checked={selected === value}
                  onChange={() => {
                    props.onChange(input.id, value);
                  }}
                />
                {caption(p, input.id, value)}
              </label>
              {hint === null ? null : (
                <span className="field-hint" id={hintId}>
                  {renderCatalogKey(p, hint)}
                </span>
              )}
            </span>
          );
        })}
      </div>
      {input.id === "input:role" && selected === "" ? <p>{p.text("label.chooseRole")}</p> : null}
      <TaskInputHelp metadata={metadata} error={error} control={errorKey} />
    </fieldset>
  );
};
const InputControl = ({ input, errorKey = input.id, sharedErrorId, ...props }: ControlProps) => {
  const p = usePresentation();
  const metadata =
    props.task.subject === "PEOPLE" && input.id === "field:caseReference"
      ? { ...taskInput(input.id), hintId: null }
      : taskInput(input.id);
  const label = renderCatalogKey(p, metadata.labelId);
  const error = props.errors[errorKey];
  if (input.choices.length > 0) {
    return <ChoiceInput input={input} errorKey={errorKey} {...props} />;
  }
  const field = taskScalar(input.id);
  if (field !== undefined) {
    const common = {
      field,
      optional: input.mode === "OPTIONAL",
      ...(props.task.subject === "PEOPLE" && input.id === "field:caseReference"
        ? { description: p.text("label.existingCaseSelectionHint") }
        : {}),
      inputName: errorKey,
      value: formValueText(props.values[input.id]),
      disabled: props.disabled,
      error,
      sharedErrorId,
      onChange: (value: string) => {
        if (props.task.creates && input.mode === "OPTIONAL" && value === "") {
          props.onFieldsChange({}, [input.id]);
        } else {
          props.onChange(input.id, value);
        }
      },
    };
    return input.choicesFrom !== null && props.current !== null ? (
      <AssigneeInput
        {...common}
        current={props.current}
        lookup={input.choicesFrom}
        seedInput={input.id}
      />
    ) : (
      <CatalogField {...common} />
    );
  }
  return (
    <div className="text-field">
      <label htmlFor={errorKey}>{label}</label>
      <TextInput
        id={errorKey}
        name={errorKey}
        type="text"
        autoComplete="off"
        dir="auto"
        value={formValueText(props.values[input.id])}
        disabled={props.disabled}
        aria-invalid={error !== undefined || undefined}
        aria-describedby={helpIds(metadata.hintId, error, errorKey)}
        onChange={(event) => {
          props.onChange(input.id, event.target.value);
        }}
      />
      <TaskInputHelp metadata={metadata} error={error} control={errorKey} />
    </div>
  );
};
const catalogField = (input: Input) =>
  productMetadata.catalog.fields.find((field) => field.name === taskInput(input.id).key);

const PairedInputs = ({ input, peer, ...props }: InputProps & { input: Input; peer: Input }) => {
  const p = usePresentation();
  const amountField = catalogField(input);
  const currencyField = catalogField(peer);
  if (amountField === undefined || currencyField === undefined) {
    throw new Error("Money fields require both catalog members.");
  }
  const amountError = props.errors[input.id];
  const currencyError = props.errors[peer.id];
  const currencies = codeSuggestions(p.language === "en-XA" ? "en" : p.language, true);
  return (
    <MoneyField
      label={renderCatalogKey(p, taskInput(input.id).labelId)}
      hint={p.hint(amountField)}
      rule={{ name: amountField.name, id: amountField.ruleId }}
      disabled={props.disabled}
      amount={{
        id: input.id,
        fieldName: amountField.name,
        value: formValueText(props.values[input.id]),
        onChange: (value) => {
          props.onChange(input.id, value);
        },
      }}
      currency={{
        id: peer.id,
        label: renderCatalogKey(p, taskInput(peer.id).labelId),
        hint: p.hint(currencyField),
        value: formValueText(props.values[peer.id]),
        fieldName: currencyField.name,
        options: currencies.map((currency) => ({ id: currency.code, label: currency.code })),
        onChange: (value) => {
          props.onChange(peer.id, value);
        },
      }}
      {...(amountError === undefined && currencyError === undefined
        ? {}
        : {
            error: (
              <>
                <ErrorText value={amountError} />
                <ErrorText value={currencyError} />
              </>
            ),
          })}
    />
  );
};

const SelectedValue = ({ input, peer, ...props }: InputProps & { input: Input; peer?: Input }) => {
  const p = usePresentation();
  const members = peer === undefined ? [input] : [input, peer];
  const ids = members.map((member) => member.id);
  const selected = ids.some((id) => Object.hasOwn(props.values, id));
  const first = renderCatalogKey(p, taskInput(input.id).labelId);
  const title =
    peer === undefined
      ? first
      : p.text("ui.pairedFacts", {
          first,
          second: renderCatalogKey(p, taskInput(peer.id).labelId),
        });
  const clear = input.clearable;
  let control = null;
  if (selected && props.values[input.id] !== null) {
    control =
      peer === undefined ? (
        <InputControl input={input} {...props} />
      ) : (
        <PairedInputs input={input} peer={peer} {...props} />
      );
  }
  return (
    <fieldset className="selected-fact">
      <legend>
        <label>
          <ChoiceControl
            type="checkbox"
            disabled={props.disabled}
            checked={selected}
            data-selects-fields={members.map((member) => taskInput(member.id).key).join(" ")}
            onChange={(event) => {
              props.onFieldsChange(
                event.target.checked
                  ? Object.fromEntries(
                      members.map((member) => [member.id, seed(props.affordance, member.id)]),
                    )
                  : {},
                event.target.checked ? [] : ids,
              );
            }}
          />
          {p.text(input.mode === "OPTIONAL" ? "ui.recordOptionalFact" : "ui.changeFact", {
            field: title,
          })}
        </label>
      </legend>
      {selected && clear ? (
        <label>
          <ChoiceControl
            type="checkbox"
            disabled={props.disabled}
            checked={props.values[input.id] === null}
            onChange={(event) => {
              props.onChange(
                input.id,
                event.target.checked ? null : seed(props.affordance, input.id),
              );
            }}
          />
          {p.text("correction.CLEAR.label")}
        </label>
      ) : null}
      {control}
    </fieldset>
  );
};
const permitted = (input: Input, props: Props): boolean => {
  const editable =
    props.affordance === undefined ? undefined : new Set<string>(props.affordance.editable);
  const authored = input.source === "AUTHORED";
  const scoped =
    input.id !== "field:caseReference" ||
    props.task.subject !== "PEOPLE" ||
    grantScope(props.values) === "CASE";
  return authored && (editable === undefined || editable.has(input.id)) && scoped;
};
const selectsValue = (input: Input, subject: Props["task"]["subject"]): boolean => {
  // The grant scope already controls omission; a visible case reference is required.
  if (subject === "PEOPLE" && input.id === "field:caseReference") {
    return false;
  }
  return (
    input.mode === "UPDATE" ||
    (input.mode === "OPTIONAL" &&
      (taskInput(input.id).kind === "FIELD" || input.id === "input:profile.contact"))
  );
};
const maySelectGrantTarget = (editable: ReadonlyArray<string> | undefined) =>
  editable === undefined ||
  editable.includes("input:caseId") ||
  editable.includes("field:caseReference");
const GrantTargetInput = ({
  inputs,
  errorKey,
  editableTarget,
  ...props
}: InputProps & {
  inputs: ReadonlyArray<Input>;
  errorKey?: (id: TaskInputId) => string;
  editableTarget?: ReadonlyArray<string>;
}) => {
  const p = usePresentation();
  const raw = grantTarget(props.values);
  const scope = inputs.find((input) => input.id === "input:scope");
  const selected = raw.selection;
  const active = inputs.find(
    (input) => input.id === (selected.kind === "ID" ? "input:caseId" : "field:caseReference"),
  );
  const editable: ReadonlyArray<string> | undefined = editableTarget ?? props.affordance?.editable;
  const maySelect = maySelectGrantTarget(editable);
  const disabled = (id: TaskInputId) =>
    props.disabled || (editable !== undefined && !editable.includes(id));
  const key = (id: TaskInputId) => errorKey?.(id) ?? id;
  const set = (selection: typeof raw.selection) => {
    props.onChange("input:scope", { kind: "GRANT_TARGET", selection });
  };
  const fields = grantFields({ ...props.values, "input:scope": raw });
  return (
    <>
      {scope === undefined ? null : (
        <InputControl
          {...props}
          input={scope}
          disabled={disabled(scope.id)}
          values={fields}
          errorKey={key(scope.id)}
          onChange={(_id, value) => {
            set(
              value === "INSTALLATION" ? { kind: "INSTALLATION" } : { kind: "REFERENCE", text: "" },
            );
          }}
        />
      )}
      {selected.kind === "INSTALLATION" ? null : (
        <>
          {maySelect ? (
            <Select
              label={p.text("label.caseSelection")}
              value={selected.kind}
              disabled={props.disabled}
              options={[
                { value: "REFERENCE", label: p.fieldLabel("caseReference") },
                { value: "ID", label: p.fieldLabel("caseId") },
              ]}
              onChange={(value) => {
                set({ kind: value === "ID" ? "ID" : "REFERENCE", text: "" });
              }}
            />
          ) : null}
          {active === undefined ? null : (
            <InputControl
              {...props}
              input={active}
              disabled={disabled(active.id)}
              values={fields}
              errorKey={key(active.id)}
              onChange={(_id, value) => {
                set({ kind: selected.kind, text: formValueText(value) });
              }}
            />
          )}
        </>
      )}
    </>
  );
};
const isGrantInput = (input: Input) =>
  ["input:scope", "input:caseId", "field:caseReference"].includes(input.id);

const isPairedCurrency = (
  field: (typeof productMetadata.catalog.fields)[number] | undefined,
  inputs: ReadonlyArray<Input>,
) =>
  field?.scalar.kind === "CURRENCY" &&
  inputs.some((input) =>
    productMetadata.catalog.fields.some(
      (owner) => owner.name === taskInput(input.id).key && owner.pairs === field.name,
    ),
  );

const Fields = ({
  inputs,
  showSections = false,
  ...props
}: InputProps & { inputs: ReadonlyArray<Input>; showSections?: boolean }) => {
  const p = usePresentation();
  const render = (input: Input) => {
    const field = catalogField(input);
    if (isPairedCurrency(field, inputs)) {
      return null;
    }
    const peer = inputs.find((candidate) => taskInput(candidate.id).key === field?.pairs);
    if (!props.task.creates && selectsValue(input, props.task.subject)) {
      return (
        <div key={input.id} className="task-field-wide">
          <SelectedValue input={input} {...(peer === undefined ? {} : { peer })} {...props} />
        </div>
      );
    }
    if (peer !== undefined) {
      return (
        <div key={input.id}>
          <PairedInputs input={input} peer={peer} {...props} />
        </div>
      );
    }
    const scalar = taskScalar(input.id)?.scalar;
    const narrative = scalar?.kind === "PLAIN_TEXT" && scalar.allowsLineFeed;
    const wide = input.choices.length > 0 || narrative;
    return (
      <div key={input.id} className={wide ? "task-field-wide" : undefined}>
        <InputControl input={input} {...props} />
      </div>
    );
  };
  const hasGrantTarget =
    props.task.subject === "PEOPLE" &&
    props.task.inputs.some((input) => input.id === "input:scope") &&
    inputs.some(isGrantInput);
  const sections = new Map<NonNullable<ReturnType<typeof catalogField>>["section"], Input[]>();
  for (const input of inputs) {
    if (hasGrantTarget && isGrantInput(input)) {
      continue;
    }
    const section = showSections ? (catalogField(input)?.section ?? null) : null;
    const fields = sections.get(section);
    if (fields === undefined) {
      sections.set(section, [input]);
    } else {
      fields.push(input);
    }
  }
  return (
    <>
      {hasGrantTarget && <GrantTargetInput {...props} inputs={props.task.inputs} />}
      {[...sections].map(([section, fields]) =>
        section === null ? (
          <div className="task-field-grid" key="ungrouped">
            {fields.map(render)}
          </div>
        ) : (
          <section className="task-field-section" key={section}>
            <h3>{p.term("section", section)}</h3>
            <div className="task-field-grid">{fields.map(render)}</div>
          </section>
        ),
      )}
    </>
  );
};
const CollectionRows = ({
  collection,
  ...props
}: InputProps & { collection: OperationTaskDefinition["collections"][number] }) => {
  const p = usePresentation();
  const value = props.values[collection.id];
  const rows = isFormCollection(value) ? value.rows : [];
  const hasGrantTarget = collection.inputs.some(isGrantInput);
  const update = (next: typeof rows) => {
    props.onChange(collection.id, { kind: "COLLECTION", rows: next });
  };
  return (
    <section
      className="task-collection"
      id={`field-${collection.id}`}
      aria-labelledby={`label-${collection.id}`}
    >
      <h3 id={`label-${collection.id}`}>{renderCatalogKey(p, collection.labelId)}</h3>
      {rows.map((row, index) => (
        <fieldset key={row.key} disabled={props.disabled} className="collection-item">
          <legend>{p.text(collection.itemLabelId, { number: p.integer(index + 1) })}</legend>
          {hasGrantTarget && (
            <GrantTargetInput
              {...props}
              defaults={collection.defaults}
              inputs={collection.inputs}
              editableTarget={collection.inputs.map((input) => input.id)}
              values={row.values}
              current={null}
              errorKey={(id) => taskRowInputKey(collection.id, row.key, id)}
              onChange={(id, nextValue) => {
                update(
                  rows.map((previous) =>
                    previous.key === row.key
                      ? { ...previous, values: editGrantInput(previous.values, id, nextValue) }
                      : previous,
                  ),
                );
              }}
            />
          )}
          {collection.inputs
            .filter((input) => !hasGrantTarget || !isGrantInput(input))
            .map((input) => (
              <InputControl
                key={input.id}
                {...props}
                input={input}
                defaults={collection.defaults}
                current={null}
                constraintValues={props.values}
                values={row.values}
                errorKey={taskRowInputKey(collection.id, row.key, input.id)}
                onChange={(id, nextValue) => {
                  update(
                    rows.map((previous) =>
                      previous.key === row.key
                        ? { ...previous, values: editGrantInput(previous.values, id, nextValue) }
                        : previous,
                    ),
                  );
                }}
              />
            ))}
          <button
            type="button"
            disabled={props.disabled || rows.length <= collection.minimumRows}
            onClick={() => {
              update(rows.filter((previous) => previous.key !== row.key));
            }}
          >
            {renderCatalogKey(p, collection.removeLabelId)}
          </button>
        </fieldset>
      ))}
      <ErrorText value={props.errors[collection.id] ?? props.errors[collection.input]} />
      <button
        type="button"
        disabled={props.disabled || rows.length >= collection.maximumRows}
        onClick={() => {
          update([
            ...rows,
            {
              key: crypto.randomUUID(),
              values: { ...collection.defaults },
            },
          ]);
        }}
      >
        {renderCatalogKey(p, collection.addLabelId)}
      </button>
    </section>
  );
};
export const TaskInputs = (props: Props) => {
  const inputProps = { ...props, defaults: props.task.defaults };
  const visible = props.task.inputs.filter((input) => permitted(input, props));
  const groups = visible.filter((input) => taskInput(input.id).kind === "GROUP");
  const groupNames = new Set(groups.map((input) => taskInput(input.id).group));
  const ordinary = visible.filter(
    (input) => taskInput(input.id).kind !== "GROUP" && !groupNames.has(taskInput(input.id).group),
  );
  return (
    <>
      <Fields inputs={ordinary} showSections {...inputProps} />
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
            <InputControl input={group} {...inputProps} />
            {props.values[group.id] === "UPDATE" ? (
              <CorrectionInputs group={metadata.group} current={props.current}>
                <Fields inputs={children} {...inputProps} />
              </CorrectionInputs>
            ) : null}
          </section>
        );
      })}
      {props.task.collections.map((collection) => (
        <CollectionRows key={collection.id} collection={collection} {...inputProps} />
      ))}
    </>
  );
};
