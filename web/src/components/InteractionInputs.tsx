import type { CurrentCase } from "../api/types";
import type { FormValues } from "../protocol/forms";
import type { Notice } from "../api/notices";
import { NoticeView } from "../presentation/Message";
import { renderKey } from "../presentation/messages";
import { usePresentation } from "../presentation/context";
import { DescriptorField } from "./DescriptorField";
import { productMetadata } from "../protocol/productMetadata";
import {
  bindingPath,
  visibleOperationInputs,
  type InteractionInput,
} from "../interaction/operationForm";

type Props = {
  values: FormValues;
  current: CurrentCase | null;
  disabled: boolean;
  errorPath: string | null;
  errorNotice: Notice | null;
  onChange: (path: string, value: string | boolean) => void;
};
const permittedOptions = (input: InteractionInput, props: Props): ReadonlyArray<string> => {
  if (input.kind.kind !== "CHOICE" && input.kind.kind !== "GROUP") {
    return [];
  }
  const offer = props.current?.advice.acts.find((item) => item.act === props.values["act.kind"]);
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
const ChoiceInput = ({ input, ...props }: Props & { input: InteractionInput }) => {
  const p = usePresentation();
  const path = bindingPath(input);
  const group = input.kind.kind === "GROUP";
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
          aria-describedby={
            props.errorPath === path && props.errorNotice ? `${input.identity}-error` : undefined
          }
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
      {props.errorPath === path && props.errorNotice ? (
        <small id={`${input.identity}-error`}>
          <NoticeView value={props.errorNotice} />
        </small>
      ) : null}
    </fieldset>
  );
};
const ScalarInput = ({ input, ...props }: Props & { input: InteractionInput }) => {
  const p = usePresentation();
  const path = bindingPath(input);
  const field = [
    ...productMetadata.descriptors.fields,
    ...productMetadata.descriptors.inputFields,
  ].find((item) => item.name === input.field);
  if (field !== undefined) {
    return (
      <DescriptorField
        field={field}
        inputName={path}
        value={String(props.values[path] ?? "")}
        disabled={props.disabled}
        error={props.errorPath === path ? (props.errorNotice ?? undefined) : undefined}
        onChange={(value) => {
          props.onChange(path, value);
        }}
      />
    );
  }
  return (
    <label>
      {renderKey(p, input.labelId)}
      <input
        name={path}
        type="text"
        autoComplete="off"
        dir="auto"
        value={String(props.values[path] ?? "")}
        disabled={props.disabled}
        aria-invalid={props.errorPath === path || undefined}
        onChange={(event) => {
          props.onChange(path, event.target.value);
        }}
      />
      {props.errorPath === path && props.errorNotice ? (
        <small id={`${input.identity}-error`}>
          <NoticeView value={props.errorNotice} />
        </small>
      ) : null}
    </label>
  );
};
const BindingInput = ({ input, ...props }: Props & { input: InteractionInput }) => {
  const p = usePresentation();
  const path = bindingPath(input);
  if (input.kind.kind === "BOOLEAN") {
    return (
      <label>
        <input
          type="checkbox"
          name={path}
          disabled={props.disabled}
          checked={props.values[path] === true}
          onChange={(event) => {
            props.onChange(path, event.target.checked);
          }}
        />
        {renderKey(p, input.labelId)}
      </label>
    );
  }
  return input.kind.kind === "GROUP" || input.kind.kind === "CHOICE" ? (
    <ChoiceInput {...props} input={input} />
  ) : (
    <ScalarInput {...props} input={input} />
  );
};
export const InteractionInputs = (props: Props) => (
  <>
    {visibleOperationInputs(props.values, props.current).map((input) => (
      <BindingInput key={input.identity} input={input} {...props} />
    ))}
  </>
);
