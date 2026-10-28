import { useMemo } from "react";
import { ComboBox, Popover } from "react-aria-components/ComboBox";
import { Input } from "react-aria-components/Input";
import { Label } from "react-aria-components/Label";
import { Button } from "react-aria-components/Button";
import { ListBox, ListBoxItem } from "react-aria-components/ListBox";
import { Text } from "react-aria-components/Text";
import { FieldError } from "react-aria-components/FieldError";
import { usePresentation } from "../presentation/context";
import { CountryHint } from "./CountryHint";
import type { TypedFieldProps } from "./CatalogDate";
type Suggestion = { code: string; name: string };
const regionCodes = () =>
  Array.from({ length: 26 * 26 }, (_, index) =>
    String.fromCharCode(65 + Math.floor(index / 26), 65 + (index % 26)),
  );
const suggestions = (locale: string, currency: boolean, unknown: string) => {
  const names = new Intl.DisplayNames(locale, {
    type: currency ? "currency" : "region",
    fallback: "none",
  });
  const codes = currency
    ? Intl.supportedValuesOf("currency")
    : regionCodes().filter((code) => code !== "ZZ");
  const values = codes.flatMap((code) => {
    const name = names.of(code);
    return name === undefined ? [] : [{ code, name }];
  });
  return currency ? values : [{ code: "ZZ", name: unknown }, ...values];
};
const CodeSuggestions = ({ items }: { items: Suggestion[] }) => (
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
        <small>{p.fieldMeaning(field.name)}</small>
        <small>{p.hint(field)}</small>
        {field.scalar.kind === "CURRENCY" ? null : <CountryHint code={value} />}
      </Text>
      {error === undefined ? null : <FieldError>{p.notice(error)}</FieldError>}
    </>
  );
};
export const CatalogCode = (props: TypedFieldProps) => {
  const { field, value, inputName, disabled, error, onChange } = props;
  const p = usePresentation();
  const currency = field.scalar.kind === "CURRENCY";
  const locale = p.language === "en-XA" ? "en" : p.language;
  const unknown = p.text("scalar.country.unknown");
  const items = useMemo(() => suggestions(locale, currency, unknown), [locale, currency, unknown]);
  const filtered = items.filter((item) =>
    `${item.code} ${item.name}`.toLocaleLowerCase(locale).includes(value.toLocaleLowerCase(locale)),
  );
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
      <Label>{p.fieldLabel(field.name)}</Label>
      <div className="code-input">
        <Input data-field-name={field.name} autoComplete="off" spellCheck={false} dir="ltr" />
        <Button aria-label={p.fieldLabel(field.name)}>⌄</Button>
      </div>
      <CodeSuggestions items={filtered} />
      <CodeDescription {...props} />
    </ComboBox>
  );
};
