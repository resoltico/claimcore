import { TextInput } from "./TextField";
import {
  codeSuggestions,
  rankCodeSuggestions,
  type CodeSuggestion,
} from "../presentation/codeSuggestions";
import { NoticeView } from "../presentation/Message";
import { FieldGroup, FieldRule } from "./Field";
import { useMemo } from "react";
import { ComboBox, Popover } from "react-aria-components/ComboBox";
import { Label } from "react-aria-components/Label";
import { ButtonControl as Button } from "./ButtonControl";
import { ListBox, ListBoxItem } from "react-aria-components/ListBox";
import { Text } from "react-aria-components/Text";
import { FieldError } from "react-aria-components/FieldError";
import { usePresentation } from "../presentation/context";
import { CountryHint } from "../components/CountryHint";
import type { TypedFieldProps } from "./DateField";
const CodeSuggestions = ({ items }: { items: CodeSuggestion[] }) => (
  <Popover className="code-popover">
    <ListBox items={items}>
      {(item) => (
        <ListBoxItem id={item.code} textValue={item.code}>
          <bdi>{item.code}</bdi> · {item.name}
        </ListBoxItem>
      )}
    </ListBox>
  </Popover>
);
const CodeDescription = ({ field, value, error, sharedErrorId }: TypedFieldProps) => {
  const p = usePresentation();
  return (
    <>
      <Text slot="description" className="field-description">
        <span className="field-hint">{p.hint(field)}</span>
        {field.valueKind === "COUNTRY" ? <CountryHint code={value} field={field} /> : null}{" "}
        <FieldRule name={field.name} ruleId={field.ruleId} invalid={error !== undefined} />
      </Text>
      {error === undefined || sharedErrorId !== undefined ? null : (
        <FieldError>
          <NoticeView value={error} />
        </FieldError>
      )}
    </>
  );
};
export const CodePicker = (props: TypedFieldProps) => {
  const { field, value, inputName, disabled, error, sharedErrorId, onChange, optional } = props;
  const p = usePresentation();
  const currency = field.scalar.kind === "CURRENCY";
  const locale = p.language === "en-XA" ? "en" : p.language;
  const items = useMemo(() => codeSuggestions(locale, currency), [locale, currency]);
  const filtered = rankCodeSuggestions(items, value, locale);
  return (
    <FieldGroup>
      <ComboBox
        id={`field-${inputName}`}
        className="text-field code-control"
        name={inputName}
        items={filtered}
        allowsCustomValue
        inputValue={value}
        onInputChange={onChange}
        isDisabled={disabled}
        {...(error === undefined || sharedErrorId === undefined
          ? {}
          : { "aria-describedby": sharedErrorId })}
        isInvalid={error !== undefined}
        validationBehavior="aria"
        menuTrigger="focus"
        onChange={(key) => {
          if (key !== null) {
            onChange(String(key));
          }
        }}
      >
        <div className="field-label-row">
          <Label>{p.fieldLabel(field.name)}</Label>
          {optional === true ? (
            <span className="field-hint">{p.text("label.optional")}</span>
          ) : null}
        </div>
        <div className="control-box field-control-group code-input">
          <TextInput data-field-name={field.name} autoComplete="off" spellCheck={false} dir="ltr" />
          <Button aria-label={p.fieldLabel(field.name)}>⌄</Button>
        </div>
        <CodeSuggestions items={filtered} />
        <CodeDescription {...props} />
      </ComboBox>
    </FieldGroup>
  );
};
