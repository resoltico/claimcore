import { ComboBox as AriaComboBox, Popover } from "react-aria-components/ComboBox";
import { ListBox, ListBoxItem } from "react-aria-components/ListBox";
import { Button } from "./Button";
import { Icon } from "./icons";
import { TextInput } from "./TextField";
import "./select.css";

export type PickerItem = { id: string; label: string };

export const ComboBox = ({
  id,
  label,
  hint,
  value,
  items,
  onChange,
  onSelect,
  disabled,
  compact = false,
  fieldName,
  inputName,
  invalid = false,
  describedBy,
  onSubmit,
}: {
  id: string;
  label: string;
  hint?: string | undefined;
  value: string;
  items: ReadonlyArray<PickerItem>;
  onChange: (value: string) => void;
  onSelect: (value: string) => void;
  disabled?: boolean | undefined;
  compact?: boolean;
  fieldName?: string | undefined;
  inputName?: string | undefined;
  invalid?: boolean;
  describedBy?: string | undefined;
  onSubmit?: (() => void) | undefined;
}) => (
  <AriaComboBox
    className={
      compact ? "control-box field-control-group compact-picker" : "control-box field-control-group"
    }
    aria-label={label}
    inputValue={value}
    onInputChange={onChange}
    items={items}
    allowsCustomValue
    menuTrigger="focus"
    isDisabled={disabled ?? false}
    isInvalid={invalid}
    onChange={(key) => {
      if (key !== null) {
        onSelect(String(key));
      }
    }}
  >
    <TextInput
      id={id}
      name={inputName}
      data-field-name={fieldName}
      aria-label={label}
      aria-description={hint}
      aria-invalid={invalid || undefined}
      aria-describedby={describedBy}
      autoComplete="off"
      onKeyDownCapture={(event) => {
        if (event.key === "Enter" && onSubmit !== undefined) {
          event.preventDefault();
          onSubmit();
        }
      }}
    />
    <Button variant="quiet" aria-label={label}>
      <Icon name="chevron-down" />
    </Button>
    <Popover className="ui-select-popover">
      <ListBox<PickerItem> className="ui-select-options">
        {(item) => (
          <ListBoxItem id={item.id} className="ui-select-option" textValue={item.label}>
            {item.label}
          </ListBoxItem>
        )}
      </ListBox>
    </Popover>
  </AriaComboBox>
);
