import { SearchField as AriaSearchField } from "react-aria-components/SearchField";
import { Button } from "./Button";
import { Icon } from "./icons";
import { ControlInput } from "./TextField";
import type { Ref } from "react";
import "./field.css";

export const SearchField = ({
  label,
  clearLabel,
  description,
  value,
  onChange,
  onSubmit,
  onClear,
  inputRef,
  id,
  invalid,
}: {
  label: string;
  clearLabel: string;
  description?: string | undefined;
  value: string;
  onChange: (value: string) => void;
  onSubmit: (value: string) => void;
  onClear?: (() => void) | undefined;
  inputRef?: Ref<HTMLInputElement> | undefined;
  id?: string | undefined;
  invalid?: boolean | undefined;
}) => (
  <div role="search" aria-label={label}>
    <AriaSearchField
      className="control-box field-search"
      aria-label={label}
      value={value}
      onChange={onChange}
      onSubmit={onSubmit}
      {...(onClear === undefined ? {} : { onClear })}
      {...(invalid === undefined ? {} : { isInvalid: invalid })}
    >
      <ControlInput
        id={id}
        ref={inputRef}
        aria-description={description}
        placeholder={label}
        autoComplete="off"
        dir="auto"
      />
      {value === "" ? null : (
        <Button variant="quiet" slot="clear" aria-label={clearLabel}>
          <Icon name="close" />
        </Button>
      )}
    </AriaSearchField>
  </div>
);
