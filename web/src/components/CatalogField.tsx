import { CatalogDate } from "./CatalogDate";
import { CatalogCode } from "./CatalogCode";
import { CountryHint } from "./CountryHint";
import { FieldError } from "react-aria-components/FieldError";
import { TextArea } from "react-aria-components/TextArea";
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
const referenceAppearance = (field: FieldCatalogEntry) =>
  field.name === "caseReference" ? { className: "identifier-value" } : {};

const isDate = (field: FieldCatalogEntry) =>
  field.scalar.kind === "CALENDAR_DATE" || field.scalar.kind === "PLANNED_DATE";
const isCode = (field: FieldCatalogEntry) =>
  field.scalar.kind === "CURRENCY" ||
  (field.scalar.kind === "TEXT" && field.scalar.format.unknownCode !== undefined);

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
  if (isDate(field)) {
    return <CatalogDate {...typed} />;
  }
  if (isCode(field)) {
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
      {field.scalar.kind === "PLAIN_TEXT" && field.scalar.allowsLineFeed ? (
        <TextArea
          name={name}
          data-field-name={field.name}
          rows={5}
          autoCapitalize="off"
          spellCheck
          dir="auto"
          autoComplete="off"
        />
      ) : (
        <Input
          name={name}
          {...referenceAppearance(field)}
          data-field-name={field.name}
          type="text"
          inputMode={field.scalar.kind === "AMOUNT" ? "decimal" : "text"}
          autoCapitalize="off"
          spellCheck={false}
          dir={field.scalar.kind === "TEXT" || field.scalar.kind === "PLAIN_TEXT" ? "auto" : "ltr"}
          autoComplete="off"
        />
      )}
      <Text slot="description" className="field-description">
        <small>{p.fieldMeaning(field.name)}</small>
        <small>{p.hint(field)}</small>
        {field.scalar.kind === "PLAIN_TEXT" ? (
          <small
            className="scalar-counter"
            data-over-limit={[...value].length > field.scalar.maximumScalars || undefined}
          >
            {p.text("ui.scalarCount", {
              count: p.integer([...value].length),
              maximum: p.integer(field.scalar.maximumScalars),
            })}
          </small>
        ) : null}
        {field.name === "incidentCountry" ? <CountryHint code={value} /> : null}
      </Text>
      {error === undefined ? null : <FieldError>{p.notice(error)}</FieldError>}
    </TextField>
  );
};
