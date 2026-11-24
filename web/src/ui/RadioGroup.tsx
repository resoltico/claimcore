import type { ReactNode } from "react";
import { ChoiceControl } from "./TextField";
import "./radio-group.css";

export const RadioGroup = ({
  id,
  name,
  label,
  disabled,
  invalid,
  describedBy,
  value,
  options,
  onChange,
  children,
}: {
  id: string;
  name: string;
  label: string;
  disabled: boolean;
  invalid: boolean;
  describedBy?: string | undefined;
  value: string;
  options: ReadonlyArray<{ value: string; label: string; unavailable?: string | undefined }>;
  onChange: (value: string) => void;
  children?: ReactNode;
}) => (
  <div
    id={id}
    role="radiogroup"
    className="text-field task-choice"
    aria-labelledby={`${id}-label`}
    aria-invalid={invalid || undefined}
    aria-describedby={describedBy}
  >
    <span className="field-label-row" id={`${id}-label`}>
      {label}
    </span>
    <div className="radio-options">
      {options.map((option, index) => {
        const hint = option.unavailable === undefined ? undefined : `${id}-option-${index}`;
        return (
          <div className="radio-option" key={option.value}>
            <label>
              <ChoiceControl
                type="radio"
                name={name}
                value={option.value}
                checked={value === option.value}
                disabled={disabled || hint !== undefined}
                aria-describedby={hint}
                onChange={() => {
                  onChange(option.value);
                }}
              />
              {option.label}
            </label>
            {hint === undefined ? null : (
              <span className="field-hint" id={hint}>
                {option.unavailable}
              </span>
            )}
          </div>
        );
      })}
    </div>
    {children}
  </div>
);
