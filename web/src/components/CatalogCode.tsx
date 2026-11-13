import {
  codeSuggestions,
  rankCodeSuggestions,
  type CodeSuggestion,
} from "../presentation/codeSuggestions";
import { NoticeView } from "../presentation/Message";
import { FieldHelp } from "./FieldHelp";
import { useMemo } from "react";
import { ComboBox, Popover } from "react-aria-components/ComboBox";
import { Input } from "react-aria-components/Input";
import { Label } from "react-aria-components/Label";
import { ButtonControl as Button } from "../ui/ButtonControl";
import { ListBox, ListBoxItem } from "react-aria-components/ListBox";
import { Text } from "react-aria-components/Text";
import { FieldError } from "react-aria-components/FieldError";
import { usePresentation } from "../presentation/context";
import { CountryHint } from "./CountryHint";
import type { TypedFieldProps } from "./CatalogDate";
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
const CodeDescription = ({ field, value, error }: TypedFieldProps) => {
  const p = usePresentation();
  return (
    <>
      <Text slot="description" className="field-description">
        <small>{p.hint(field)}</small>
        {field.valueKind === "COUNTRY" ? <CountryHint code={value} field={field} /> : null}
      </Text>
      {error === undefined ? null : (
        <FieldError>
          <NoticeView value={error} />
        </FieldError>
      )}
    </>
  );
};
export const CatalogCode = (props: TypedFieldProps) => {
  const { field, value, inputName, disabled, error, onChange } = props;
  const p = usePresentation();
  const currency = field.scalar.kind === "CURRENCY";
  const locale = p.language === "en-XA" ? "en" : p.language;
  const unknown = p.text("scalar.country.unknown");
  const items = useMemo(
    () => codeSuggestions(locale, currency, unknown),
    [locale, currency, unknown],
  );
  const filtered = rankCodeSuggestions(items, value, locale);
  return (
    <ComboBox
      id={`field-${inputName}`}
      className="text-field code-control"
      name={inputName}
      items={filtered}
      allowsCustomValue
      inputValue={value}
      onInputChange={onChange}
      isDisabled={disabled}
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
        <FieldHelp name={field.name} label={p.fieldLabel(field.name)} ruleId={field.ruleId} />
      </div>
      <div className="code-input">
        <Input data-field-name={field.name} autoComplete="off" spellCheck={false} dir="ltr" />
        <Button aria-label={p.fieldLabel(field.name)}>⌄</Button>
      </div>
      <CodeSuggestions items={filtered} />
      <CodeDescription {...props} />
    </ComboBox>
  );
};
