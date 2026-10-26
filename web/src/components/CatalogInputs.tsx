import { offerAct } from "../operation/metadata";
import type { CurrentCase } from "../api/types";
import type { FormValues } from "../protocol/forms";
import type { Notice } from "../api/notices";
import { NoticeView } from "../presentation/Message";
import { renderKey } from "../presentation/messages";
import { usePresentation } from "../presentation/context";
import { CatalogField } from "./CatalogField";
import { productMetadata } from "../protocol/productMetadata";
import { bindingPath, visibleOperationInputs, type CatalogInput } from "../operation/operationForm";

type Props = {
  values: FormValues;
  current: CurrentCase | null;
  disabled: boolean;
  errorPath: string | null;
  errorNotice: Notice | null;
  onChange: (path: string, value: string | boolean) => void;
};
const permittedOptions = (input: CatalogInput, props: Props): ReadonlyArray<string> => {
  if (input.kind.kind !== "CHOICE" && input.kind.kind !== "GROUP") {
    return [];
  }
  const offer = props.current?.advice.acts.find(
    (item) => offerAct(item) === props.values["act.kind"],
  );
  const group =
    offer !== undefined && "correction" in offer
      ? Object.entries(offer.correction).find(([name]) =>
          input.diagnosticTargets.includes(name),
        )?.[1]
      : undefined;
  return group === undefined
    ? input.kind.options
    : input.kind.options.filter((mode) => group.some((value) => value === mode));
};
const inputError = (input: CatalogInput, props: Props) =>
  props.errorPath === bindingPath(input) ? props.errorNotice : null;
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
        <small id={`${input.identity}-meaning`}>{renderKey(p, input.meaningId)}</small>
      )}
      {error === null ? null : (
        <small id={`${input.identity}-error`}>
          <NoticeView value={error} />
        </small>
      )}
    </>
  );
};
const ChoiceInput = ({ input, ...props }: Props & { input: CatalogInput }) => {
  const p = usePresentation();
  const path = bindingPath(input);
  const group = input.kind.kind === "GROUP";
  const error = inputError(input, props);
  return (
    <fieldset>
      {group ? <legend>{renderKey(p, input.labelId)}</legend> : null}
      <label>
        {group ? p.text("ui.action") : renderKey(p, input.labelId)}
        <select
          name={path}
          id={
            group && input.diagnosticTargets[0] !== undefined
              ? `correction-${input.diagnosticTargets[0]}-mode`
              : input.identity
          }
          disabled={props.disabled}
          value={String(props.values[path] ?? "")}
          aria-invalid={props.errorPath === path || undefined}
          aria-describedby={descriptionIds(input, error)}
          onChange={(event) => {
            props.onChange(path, event.target.value);
          }}
        >
          {permittedOptions(input, props).map((value) => (
            <option key={value} value={value}>
              {p.token(value)}
            </option>
          ))}
        </select>
      </label>
      <InputDescription input={input} error={error} />
    </fieldset>
  );
};
const ScalarInput = ({ input, ...props }: Props & { input: CatalogInput }) => {
  const p = usePresentation();
  const path = bindingPath(input);
  const error = inputError(input, props);
  const field = [...productMetadata.catalog.fields, ...productMetadata.catalog.inputFields].find(
    (item) => item.name === input.field,
  );
  if (field !== undefined) {
    return (
      <CatalogField
        field={field}
        inputName={path}
        value={String(props.values[path] ?? "")}
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
      <label htmlFor={input.identity}>{renderKey(p, input.labelId)}</label>
      <input
        id={input.identity}
        name={path}
        type="text"
        autoComplete="off"
        dir="auto"
        value={String(props.values[path] ?? "")}
        disabled={props.disabled}
        aria-invalid={props.errorPath === path || undefined}
        aria-describedby={descriptionIds(input, error)}
        onChange={(event) => {
          props.onChange(path, event.target.value);
        }}
      />
      <InputDescription input={input} error={error} />
    </div>
  );
};
const BindingInput = ({ input, ...props }: Props & { input: CatalogInput }) => {
  const p = usePresentation();
  const path = bindingPath(input);
  const error = inputError(input, props);
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
            aria-invalid={props.errorPath === path || undefined}
            aria-describedby={descriptionIds(input, error)}
            onChange={(event) => {
              props.onChange(path, event.target.checked);
            }}
          />
          {renderKey(p, input.labelId)}
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
export const CatalogInputs = (props: Props) => (
  <>
    {visibleOperationInputs(props.values, props.current).map((input) => (
      <BindingInput key={input.identity} input={input} {...props} />
    ))}
  </>
);
