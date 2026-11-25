import { useId, type ReactNode } from "react";
import { FieldGroup, FieldRule } from "./Field";
import { ControlInput } from "./TextField";
import { ComboBox } from "./ComboBox";
import "./money-field.css";

export const MoneyField = ({
  label,
  groupLabel,
  optional,
  presence,
  hint,
  rule,
  amount,
  currency,
  error,
  disabled,
}: {
  label: string;
  groupLabel: string;
  optional: string | null;
  presence: string;
  hint: string;
  rule: { name: string; id: string | null };
  amount: { id: string; fieldName: string; value: string; onChange: (value: string) => void };
  currency: {
    id: string;
    label: string;
    hint: string;
    value: string;
    fieldName: string;
    options: ReadonlyArray<{ id: string; label: string }>;
    onChange: (value: string) => void;
  };
  error?: ReactNode;
  disabled: boolean;
}) => {
  const id = useId();
  const hintId = `${id}-hint`;
  const errorId = `${id}-error`;
  const currencyHintId = `${id}-currency-hint`;
  const presenceId = `${id}-presence`;
  const invalid = error !== undefined;
  return (
    <FieldGroup>
      <div
        className="text-field"
        role="group"
        aria-label={groupLabel}
        aria-describedby={presenceId}
      >
        <div className="field-label-row">
          <label htmlFor={amount.id}>{label}</label>
          {optional === null ? null : <span className="field-hint">{optional}</span>}
        </div>
        <div className="control-box field-control-group money-field">
          <ControlInput
            id={amount.id}
            name={amount.id}
            value={amount.value}
            data-field-name={amount.fieldName}
            inputMode="decimal"
            autoComplete="off"
            dir="ltr"
            disabled={disabled}
            aria-invalid={invalid || undefined}
            aria-describedby={
              invalid ? `${hintId} ${presenceId} ${errorId}` : `${hintId} ${presenceId}`
            }
            onChange={(event) => {
              amount.onChange(event.currentTarget.value);
            }}
          />
          <ComboBox
            compact
            id={currency.id}
            inputName={currency.id}
            label={currency.label}
            invalid={invalid}
            describedBy={
              invalid
                ? `${currencyHintId} ${presenceId} ${errorId}`
                : `${currencyHintId} ${presenceId}`
            }
            value={currency.value}
            items={currency.options.filter(
              (item) =>
                currency.value.length === 3 || item.id.startsWith(currency.value.toUpperCase()),
            )}
            disabled={disabled}
            fieldName={currency.fieldName}
            onChange={currency.onChange}
            onSelect={currency.onChange}
          />
        </div>
        <span id={currencyHintId} className="sr-only">
          {currency.hint}
        </span>
        <div id={hintId} className="field-description">
          {hint}
          <FieldRule name={rule.name} ruleId={rule.id} />
        </div>
        <div id={presenceId} className="field-description">
          {presence}
        </div>
        {invalid ? (
          <div id={errorId} className="field-error">
            {error}
          </div>
        ) : null}
      </div>
    </FieldGroup>
  );
};
