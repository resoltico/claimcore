import { TextInput } from "../ui/TextField";
import { TextArea } from "../ui/TextArea";
import { NoticeView } from "../presentation/Message";
import { Field, FieldRule } from "../ui/Field";
import { CatalogDate } from "./CatalogDate";
import { CatalogCode } from "./CatalogCode";
import { CountryHint } from "./CountryHint";
import type { FieldCatalogEntry } from "../api/service";
import type { Notice } from "../protocol/notices";
import { usePresentation } from "../presentation/context";

type CatalogFieldProps = {
  field: FieldCatalogEntry;
  value: string;
  inputName?: string;
  description?: string;
  error?: Notice | undefined;
  sharedErrorId?: string | undefined;
  disabled?: boolean;
  optional?: boolean | undefined;
  onChange: (value: string) => void;
};
const referenceAppearance = (field: FieldCatalogEntry) =>
  field.valueKind === "REFERENCE" ? { className: "identifier-value" } : {};

const isDate = (field: FieldCatalogEntry) =>
  field.scalar.kind === "CALENDAR_DATE" || field.scalar.kind === "PLANNED_DATE";
const isCode = (field: FieldCatalogEntry) =>
  field.scalar.kind === "CURRENCY" || field.valueKind === "COUNTRY";

const ScalarControl = ({ field, name }: { field: FieldCatalogEntry; name: string }) =>
  field.scalar.kind === "PLAIN_TEXT" ? (
    <TextArea
      name={name}
      data-field-name={field.name}
      rows={field.scalar.allowsLineFeed ? 5 : 1}
      autoCapitalize="off"
      spellCheck
      dir="auto"
      autoComplete="off"
    />
  ) : (
    <TextInput
      name={name}
      {...referenceAppearance(field)}
      data-field-name={field.name}
      type="text"
      inputMode={field.scalar.kind === "AMOUNT" ? "decimal" : "text"}
      autoCapitalize="off"
      spellCheck={false}
      dir={field.scalar.kind === "TEXT" ? "auto" : "ltr"}
      autoComplete="off"
    />
  );
const ScalarCounter = ({ field, value }: { field: FieldCatalogEntry; value: string }) => {
  const p = usePresentation();
  if (field.scalar.kind !== "PLAIN_TEXT") {
    return null;
  }
  const count = Array.from(value).length;
  return (
    <span
      className="scalar-counter"
      data-over-limit={count > field.scalar.maximumScalars || undefined}
    >
      {p.text("ui.scalarCount", {
        count: p.integer(count),
        maximum: p.integer(field.scalar.maximumScalars),
      })}
    </span>
  );
};
export const CatalogField = ({
  field,
  value,
  inputName,
  description,
  error,
  sharedErrorId,
  disabled = false,
  optional,
  onChange,
}: CatalogFieldProps) => {
  const p = usePresentation();
  const name = inputName ?? field.name;
  const typed = {
    field,
    value,
    inputName: name,
    disabled,
    error,
    sharedErrorId,
    onChange,
    optional,
  };
  if (isDate(field)) {
    return <CatalogDate {...typed} />;
  }
  if (isCode(field)) {
    return <CatalogCode {...typed} />;
  }
  return (
    <Field
      id={`field-${name}`}
      label={p.fieldLabel(field.name)}
      optional={optional === true ? p.text("label.optional") : undefined}
      {...(error === undefined || sharedErrorId === undefined
        ? {}
        : { "aria-describedby": sharedErrorId })}
      isInvalid={error !== undefined}
      isDisabled={disabled}
      value={value}
      onChange={onChange}
      description={
        <>
          <span className="field-hint">{description ?? p.hint(field)}</span>{" "}
          <FieldRule name={field.name} ruleId={field.ruleId} invalid={error !== undefined} />
          <ScalarCounter field={field} value={value} />
          {field.valueKind === "COUNTRY" ? <CountryHint code={value} field={field} /> : null}
        </>
      }
      {...(error === undefined || sharedErrorId !== undefined
        ? {}
        : { error: <NoticeView value={error} /> })}
    >
      <ScalarControl field={field} name={name} />
    </Field>
  );
};
