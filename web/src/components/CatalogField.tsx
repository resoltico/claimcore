import { CatalogDate } from "./CatalogDate";
import { CatalogCode } from "./CatalogCode";
import { CountryHint } from "./CountryHint";
import { FieldError } from "react-aria-components/FieldError";
import { Input } from "react-aria-components/Input";
import { Label } from "react-aria-components/Label";
import { Text } from "react-aria-components/Text";
import { TextField } from "react-aria-components/TextField";
import type { FieldCatalogEntry } from "../api/service";
import type { Notice } from "../protocol/notices";
import { usePresentation } from "../presentation/context";

type CatalogFieldProps = {
  field: FieldCatalogEntry;
  value: string;
  inputName?: string;
  error?: Notice | undefined;
  disabled?: boolean;
  onChange: (value: string) => void;
};
export const CatalogField = ({
  field,
  value,
  inputName,
  error,
  disabled = false,
  onChange,
}: CatalogFieldProps) => {
  const p = usePresentation();
  const name = inputName ?? field.name;
  const typed = { field, value, inputName: name, disabled, error, onChange };
  if (field.scalar.kind === "CALENDAR_DATE") {
    return <CatalogDate {...typed} />;
  }
  if (
    field.scalar.kind === "CURRENCY" ||
    (field.scalar.kind === "TEXT" && field.scalar.format.unknownCode !== undefined)
  ) {
    return <CatalogCode {...typed} />;
  }
  return (
    <TextField
      id={`field-${name}`}
      className="text-field"
      isInvalid={error !== undefined}
      isDisabled={disabled}
      value={value}
      onChange={onChange}
    >
      <Label>{p.fieldLabel(field.name)}</Label>
      <Input
        name={inputName ?? field.name}
        data-field-name={field.name}
        type="text"
        inputMode={field.scalar.kind === "AMOUNT" ? "decimal" : "text"}
        autoCapitalize="off"
        spellCheck={false}
        dir={field.scalar.kind === "TEXT" ? "auto" : "ltr"}
        autoComplete="off"
      />
      <Text slot="description" className="field-description">
        <small>{p.fieldMeaning(field.name)}</small>
        <small>{p.hint(field)}</small>
        {field.name === "incidentCountry" ? <CountryHint code={value} /> : null}
      </Text>
      {error === undefined ? null : <FieldError>{p.notice(error)}</FieldError>}
    </TextField>
  );
};
