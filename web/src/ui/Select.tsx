import { useCompact } from "../hooks/useCompact";
import "./select.css";
import "./button.css";
import { useId, useRef, useState, type ReactNode, type KeyboardEvent } from "react";
import { ListBox, ListBoxItem } from "react-aria-components/ListBox";
import { Popover } from "react-aria-components/ComboBox";
import { Icon } from "./icons";

type SelectOption = Readonly<{ value: string; label: string; lang?: string }>;
type Props = {
  id?: string;
  label: string;
  value: string;
  help?: ReactNode;
  description?: string;
  inlineLabel?: boolean;
  options: ReadonlyArray<SelectOption>;
  disabled?: boolean;
  onChange: (value: string) => void;
};

const SelectLabel = ({
  label,
  labelId,
  controlId,
  inline,
  help,
}: {
  label: string;
  labelId: string;
  controlId: string;
  inline: boolean;
  help: ReactNode;
}) => {
  if (inline) {
    return (
      <span id={labelId} className="sr-only">
        {label}
      </span>
    );
  }
  return (
    <div className="ui-select-label">
      <label id={labelId} htmlFor={controlId}>
        {label}
      </label>
      {help}
    </div>
  );
};

/** A select-only picker without native select/HiddenSelect, which violates CSP in qualified WebKit. */
export const Select = ({
  id,
  label,
  value,
  options,
  disabled = false,
  onChange,
  help,
  description,
  inlineLabel = false,
}: Props) => {
  const compact = useCompact();
  const inline = inlineLabel && !compact;
  const generated = useId();
  const controlId = id ?? generated;
  const labelId = `${controlId}-label`;
  const descriptionId = `${controlId}-hint`;
  const listId = `${controlId}-options`;
  const valueId = `${controlId}-value`;
  const trigger = useRef<HTMLButtonElement>(null);
  // Positioning preserves the focused option when the available scroll height changes.
  const optionsRef = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);
  const [draft, setDraft] = useState(value);
  const [focus, setFocus] = useState<true | "first" | "last">(true);
  const typeahead = useRef({ text: "", at: 0 });
  const cancelOnEscape = (event: KeyboardEvent<HTMLElement>) => {
    if (open && event.key === "Escape") {
      event.preventDefault();
      event.stopPropagation();
      setOpen(false);
      trigger.current?.focus();
    }
  };
  const selected = options.find((option) => option.value === value);
  const begin = (strategy: true | "first" | "last", pending = value) => {
    if (disabled || options.length === 0) {
      return;
    }
    if (strategy === "first") {
      pending = options[0]?.value ?? pending;
    }
    if (strategy === "last") {
      pending = options.at(-1)?.value ?? pending;
    }
    setDraft(pending);
    setFocus(strategy);
    setOpen(true);
  };
  return (
    <div className="ui-select-field">
      <SelectLabel
        label={label}
        labelId={labelId}
        controlId={controlId}
        inline={inline}
        help={help}
      />
      <button
        type="button"
        id={controlId}
        ref={trigger}
        className="control-box ui-select-trigger"
        role="combobox"
        value={value}
        aria-labelledby={labelId}
        aria-describedby={description === undefined ? valueId : `${valueId} ${descriptionId}`}
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={listId}
        disabled={disabled}
        onClick={() => {
          if (open) {
            setOpen(false);
          } else {
            begin(true);
          }
        }}
        onKeyDownCapture={cancelOnEscape}
        onKeyDown={(event) => {
          if (["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) {
            event.preventDefault();
            let strategy: true | "first" | "last" = true;
            if (event.key === "Home") {
              strategy = "first";
            }
            if (event.key === "End") {
              strategy = "last";
            }
            begin(strategy);
          } else if (
            event.key.length === 1 &&
            event.key !== " " &&
            !event.ctrlKey &&
            !event.metaKey &&
            !event.altKey
          ) {
            event.preventDefault();
            const now = performance.now();
            const prefix =
              (now - typeahead.current.at < 700 ? typeahead.current.text : "") + event.key;
            typeahead.current = { text: prefix, at: now };
            const match = options.find((option) =>
              option.label.toLocaleLowerCase().startsWith(prefix.toLocaleLowerCase()),
            );
            begin(true, match?.value ?? value);
          }
        }}
      >
        {inline ? (
          <span aria-hidden="true" className="select-inline-label">
            {label}:
          </span>
        ) : null}
        <span id={valueId} lang={selected?.lang}>
          {selected?.label ?? value}
        </span>
        <Icon name="chevron-down" />
      </button>
      {description === undefined ? null : (
        <span id={descriptionId} className={inline ? "sr-only" : "field-hint"}>
          {description}
        </span>
      )}
      <Popover
        className="ui-select-popover"
        triggerRef={trigger}
        scrollRef={optionsRef}
        isOpen={open}
        isNonModal
        placement="bottom start"
        shouldCloseOnInteractOutside={(element) => !(trigger.current?.contains(element) ?? false)}
        onOpenChange={(next) => {
          setOpen(next);
        }}
      >
        <div className="ui-select-collection" onKeyDownCapture={cancelOnEscape}>
          <ListBox
            ref={optionsRef}
            id={listId}
            className="ui-select-options"
            aria-labelledby={labelId}
            items={options}
            selectionMode="single"
            selectionBehavior="replace"
            disallowEmptySelection
            selectedKeys={[draft]}
            // eslint-disable-next-line jsx-a11y/no-autofocus -- Focus follows explicit popup opening; the listbox is absent on initial load.
            autoFocus={focus}
            escapeKeyBehavior="none"
            onSelectionChange={(keys) => {
              if (keys !== "all") {
                const [key] = [...keys];
                if (typeof key === "string") {
                  setDraft(key);
                }
              }
            }}
          >
            {(option) => (
              <ListBoxItem
                id={option.value}
                textValue={option.label}
                lang={option.lang}
                className="ui-select-option"
                onPress={() => {
                  if (!disabled) {
                    if (option.value !== value) {
                      onChange(option.value);
                    }
                    setOpen(false);
                    trigger.current?.focus();
                  }
                }}
              >
                {option.label}
              </ListBoxItem>
            )}
          </ListBox>
        </div>
      </Popover>
    </div>
  );
};
