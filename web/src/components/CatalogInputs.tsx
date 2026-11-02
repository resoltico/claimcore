import { AssigneeInput } from "./AssigneeInput";
import { roleAdmitted } from "../operation/roleAvailability";
import { CorrectionInputs } from "./CorrectionInputs";
import { isTerm, isVocabulary } from "../presentation/terms";
import type { CatalogEntry } from "../protocol/catalogAssembly";
import type { CurrentCase, FieldCatalogEntry } from "../protocol/types";
import { formValueText, isChoiceValues, type FormValues, type FormValue } from "../protocol/forms";
import type { Notice } from "../protocol/notices";
import { NoticeView } from "../presentation/Message";
import { renderCatalogKey } from "../presentation/messages";
import { usePresentation } from "../presentation/context";
import { CatalogField } from "./CatalogField";
import { productMetadata } from "../protocol/productMetadata";
import {
  bindingPath,
  visibleOperationInputs,
  selectableFact,
  prefilledValue,
  type CatalogInput,
} from "../operation/operationForm";

type Props = {
  entry: CatalogEntry;
  values: FormValues;
  current: CurrentCase | null;
  disabled: boolean;
  fixedTarget?: boolean;
  fieldErrors: Readonly<Record<string, Notice>>;
  onChange: (path: string, value: FormValue) => void;
  onFieldsChange: (values: FormValues, omitted?: ReadonlyArray<string>) => void;
};
const permittedOptions = (input: CatalogInput, props: Props): ReadonlyArray<string> => {
  if (input.kind.kind !== "CHOICE" && input.kind.kind !== "GROUP") {
    return [];
  }
  const group =
    input.adviceGroup === null ? undefined : props.current?.advice.correction?.[input.adviceGroup];
  return group === undefined
    ? input.kind.options
    : input.kind.options.filter((mode) => group.some((value) => value === mode));
};
const inputError = (input: CatalogInput, props: Props) =>
  props.fieldErrors[bindingPath(input)] ?? null;
const descriptionIds = (input: CatalogInput, error: Notice | null) =>
  [
    input.meaningId === null ? null : `${input.identity}-meaning`,
    error === null ? null : `${input.identity}-error`,
  ]
    .filter((id) => id !== null)
    .join(" ") || undefined;
const InputDescription = ({ input, error }: { input: CatalogInput; error: Notice | null }) => {
  const p = usePresentation();
  return (
    <>
      {input.meaningId === null ? null : (
        <small id={`${input.identity}-meaning`}>{renderCatalogKey(p, input.meaningId)}</small>
      )}
      {error === null ? null : (
        <small id={`${input.identity}-error`}>
          <NoticeView value={error} />
        </small>
      )}
    </>
  );
};
const choiceLabel = (
  p: ReturnType<typeof usePresentation>,
  input: CatalogInput,
  value: string,
): string => {
  if (value === "" && input.kind.kind === "GROUP" && !input.required) {
    return p.text("ui.leaveUnchanged");
  }
  const vocabulary =
    input.kind.kind === "CHOICE" || input.kind.kind === "GROUP" ? input.kind.vocabulary : null;
  return vocabulary !== null && isVocabulary(vocabulary) && isTerm(vocabulary, value)
    ? p.term(vocabulary, value)
    : value;
};
const roleOptionUnavailable = (input: CatalogInput, props: Props, value: string) =>
  props.entry.subject === "AUTHORITY" &&
  props.entry.kind === "GRANT" &&
  input.kind.kind === "CHOICE" &&
  input.kind.vocabulary === "role" &&
  typeof props.values["target.kind"] === "string" &&
  !roleAdmitted(
    value,
    props.values["target.kind"],
    typeof props.values["act.scope.kind"] === "string" ? props.values["act.scope.kind"] : undefined,
  );
const ChoiceOption = ({
  input,
  value,
  ...props
}: Props & { input: CatalogInput; value: string }) => {
  const p = usePresentation();
  const path = bindingPath(input);
  const error = inputError(input, props);
  const blocked = roleOptionUnavailable(input, props, value);
  const reason = `${input.identity}-${value}-unavailable`;
  return (
    <label className={props.values[path] === value ? "selected" : ""}>
      <input
        type="radio"
        name={path}
        value={value}
        checked={props.values[path] === value}
        disabled={props.disabled || blocked}
        aria-describedby={
          [descriptionIds(input, error), blocked ? reason : undefined].filter(Boolean).join(" ") ||
          undefined
        }
        onChange={() => {
          props.onChange(path, value);
        }}
      />
      {choiceLabel(p, input, value)}
      {blocked ? (
        <small id={reason} aria-hidden="true">
          {p.text(
            typeof props.values["target.kind"] === "string" &&
              !roleAdmitted(value, props.values["target.kind"])
              ? "ui.roleNotAvailableForService"
              : "ui.roleNotAvailableAtScope",
          )}
        </small>
      ) : null}
    </label>
  );
};
const choiceGroupId = (input: CatalogInput) =>
  input.kind.kind === "GROUP" && input.diagnosticTargets[0] !== undefined
    ? `correction-${input.diagnosticTargets[0]}-mode`
    : input.identity;
const ChoiceInput = ({ input, ...props }: Props & { input: CatalogInput }) => {
  const p = usePresentation();
  const path = bindingPath(input);
  const group = input.kind.kind === "GROUP";
  const error = inputError(input, props);
  const admitted = permittedOptions(input, props);
  const options = group && !input.required ? ["", ...admitted] : admitted;
  const label = renderCatalogKey(p, input.labelId);
  const roleChoice = input.kind.kind === "CHOICE" && input.kind.vocabulary === "role";
  return (
    <fieldset className={group ? "correction-card" : "choice-card"}>
      <legend>{label}</legend>
      <div
        role="radiogroup"
        className={options.length <= 4 ? "segmented-choice" : "radio-choice"}
        aria-label={group ? p.text("label.action") : label}
        id={choiceGroupId(input)}
        aria-invalid={error !== null || undefined}
        aria-describedby={descriptionIds(input, error)}
      >
        {options.map((value) => (
          <ChoiceOption key={value} input={input} value={value} {...props} />
        ))}
      </div>
      {roleChoice && (props.values[path] === undefined || props.values[path] === "") ? (
        <p>{p.text("ui.chooseRole")}</p>
      ) : null}
      <InputDescription input={input} error={error} />
    </fieldset>
  );
};
const scalarInputAppearance = (input: CatalogInput) =>
  input.source === "OPERATION_IDENTITY"
    ? { className: "identifier-value", dir: "ltr" }
    : { dir: "auto" };
const ScalarInput = ({ input, ...props }: Props & { input: CatalogInput }) => {
  const p = usePresentation();
  const path = bindingPath(input);
  const error = inputError(input, props);
  const field = [...productMetadata.catalog.fields, ...productMetadata.catalog.inputFields].find(
    (item): item is FieldCatalogEntry => item.scalar !== null && item.name === input.field,
  );
  if (field?.name === "responsibleActor" && props.current !== null) {
    return (
      <AssigneeInput
        field={field}
        current={props.current}
        inputName={path}
        value={formValueText(props.values[path])}
        disabled={props.disabled}
        error={error ?? undefined}
        onChange={(value) => {
          props.onChange(path, value);
        }}
      />
    );
  }
  if (field !== undefined) {
    return (
      <CatalogField
        field={field}
        inputName={path}
        value={formValueText(props.values[path])}
        disabled={props.disabled}
        error={error ?? undefined}
        onChange={(value) => {
          props.onChange(path, value);
        }}
      />
    );
  }
  return (
    <div className="text-field">
      <label htmlFor={input.identity}>{renderCatalogKey(p, input.labelId)}</label>
      <input
        id={input.identity}
        name={path}
        type="text"
        autoComplete="off"
        {...scalarInputAppearance(input)}
        value={formValueText(props.values[path])}
        disabled={props.disabled}
        aria-invalid={props.fieldErrors[path] !== undefined || undefined}
        aria-describedby={descriptionIds(input, error)}
        onChange={(event) => {
          props.onChange(path, event.target.value);
        }}
      />
      <InputDescription input={input} error={error} />
    </div>
  );
};
const ChoiceSetInput = ({ input, ...props }: Props & { input: CatalogInput }) => {
  const p = usePresentation();
  const path = bindingPath(input);
  const error = inputError(input, props);
  const value = props.values[path];
  const selected = isChoiceValues(value) ? value : [];
  if (input.kind.kind !== "CHOICES") {
    return null;
  }
  const { vocabulary, options } = input.kind;
  return (
    <fieldset aria-describedby={descriptionIds(input, error)}>
      <legend>{renderCatalogKey(p, input.labelId)}</legend>
      {options.map((option) => (
        <label key={option}>
          <input
            type="checkbox"
            name={path}
            value={option}
            disabled={props.disabled}
            checked={selected.includes(option)}
            onChange={(event) => {
              props.onChange(
                path,
                event.target.checked
                  ? [...selected, option]
                  : selected.filter((choice) => choice !== option),
              );
            }}
          />
          {isVocabulary(vocabulary) && isTerm(vocabulary, option)
            ? p.term(vocabulary, option)
            : option}
        </label>
      ))}
      <InputDescription input={input} error={error} />
    </fieldset>
  );
};
const BindingInput = ({ input, ...props }: Props & { input: CatalogInput }) => {
  const p = usePresentation();
  const path = bindingPath(input);
  const error = inputError(input, props);
  if (input.kind.kind === "CHOICES") {
    return <ChoiceSetInput input={input} {...props} />;
  }
  if (input.kind.kind === "BOOLEAN") {
    return (
      <div>
        <label htmlFor={input.identity}>
          <input
            id={input.identity}
            type="checkbox"
            name={path}
            disabled={props.disabled}
            checked={props.values[path] === true}
            aria-invalid={props.fieldErrors[path] !== undefined || undefined}
            aria-describedby={descriptionIds(input, error)}
            onChange={(event) => {
              props.onChange(path, event.target.checked);
            }}
          />
          {renderCatalogKey(p, input.labelId)}
        </label>
        <InputDescription input={input} error={error} />
      </div>
    );
  }
  return input.kind.kind === "GROUP" || input.kind.kind === "CHOICE" ? (
    <ChoiceInput {...props} input={input} />
  ) : (
    <ScalarInput {...props} input={input} />
  );
};
const peerInput = (input: CatalogInput, inputs: ReadonlyArray<CatalogInput>) => {
  const field = productMetadata.catalog.fields.find((candidate) => candidate.name === input.field);
  const peer = field?.pairs;
  return peer === null || peer === undefined
    ? undefined
    : inputs.find(
        (candidate) =>
          candidate.field === peer &&
          JSON.stringify(candidate.branches) === JSON.stringify(input.branches),
      );
};
const SelectedFact = ({
  input,
  peer,
  ...props
}: Props & { input: CatalogInput; peer: CatalogInput | undefined }) => {
  const p = usePresentation();
  const inputs = peer === undefined ? [input] : [input, peer];
  const paths = inputs.map(bindingPath);
  const selected = paths.some((path) => Object.hasOwn(props.values, path));
  const label =
    peer === undefined
      ? renderCatalogKey(p, input.labelId)
      : p.text("ui.pairedFacts", {
          first: renderCatalogKey(p, input.labelId),
          second: renderCatalogKey(p, peer.labelId),
        });
  return (
    <fieldset className="selected-fact">
      <legend>
        <label>
          <input
            type="checkbox"
            data-selects-fields={inputs
              .map((item) => item.field)
              .filter((name) => name !== null)
              .join(" ")}
            disabled={props.disabled}
            checked={selected}
            onChange={(event) => {
              props.onFieldsChange(
                event.target.checked
                  ? Object.fromEntries(
                      inputs.map((item) => [
                        bindingPath(item),
                        prefilledValue(props.current, item.prefill),
                      ]),
                    )
                  : {},
                event.target.checked ? [] : paths,
              );
            }}
          />
          {p.text("ui.changeFact", { field: label })}
        </label>
      </legend>
      {selected
        ? inputs.map((item) => <BindingInput key={item.identity} input={item} {...props} />)
        : null}
    </fieldset>
  );
};
const pairedChild = (input: CatalogInput, inputs: ReadonlyArray<CatalogInput>, props: Props) =>
  inputs.some(
    (candidate) => selectableFact(props.entry, candidate) && peerInput(candidate, inputs) === input,
  );
const factNode = (input: CatalogInput, inputs: ReadonlyArray<CatalogInput>, props: Props) =>
  selectableFact(props.entry, input) ? (
    <SelectedFact key={input.identity} input={input} peer={peerInput(input, inputs)} {...props} />
  ) : (
    <BindingInput key={input.identity} input={input} {...props} />
  );
const factNodes = (inputs: ReadonlyArray<CatalogInput>, props: Props) =>
  inputs
    .filter((input) => !pairedChild(input, inputs, props))
    .map((input) => factNode(input, inputs, props));
const CorrectionGroup = ({
  input,
  children,
  ...props
}: Props & { input: CatalogInput; children: ReadonlyArray<CatalogInput> }) => (
  <section className="correction-group">
    <ChoiceInput input={input} {...props} />
    {children.length === 0 ? null : (
      <CorrectionInputs group={input.adviceGroup} current={props.current}>
        {factNodes(children, props)}
      </CorrectionInputs>
    )}
  </section>
);
export const CatalogInputs = (props: Props) => {
  const visible = visibleOperationInputs(props.entry, props.values, props.current);
  const groups = visible.filter((input) => input.kind.kind === "GROUP");
  const prefix = (input: CatalogInput) => `${input.path.slice(0, -1).join(".")}.`;
  const children = (group: CatalogInput) =>
    visible.filter((input) => input !== group && bindingPath(input).startsWith(prefix(group)));
  const parents = visible.filter(
    (input) =>
      !groups.some((group) => group !== input && bindingPath(input).startsWith(prefix(group))),
  );
  return (
    <>
      {parents
        .filter((input) => !pairedChild(input, parents, props))
        .map((input) =>
          input.kind.kind === "GROUP" ? (
            <CorrectionGroup key={input.identity} input={input} {...props}>
              {children(input)}
            </CorrectionGroup>
          ) : (
            factNode(input, parents, {
              ...props,
              disabled:
                props.disabled || (props.fixedTarget === true && input.path[0] === "target"),
            })
          ),
        )}
    </>
  );
};
