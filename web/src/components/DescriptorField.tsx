import { FieldError } from "react-aria-components/FieldError";
import { Input } from "react-aria-components/Input";
import { Label } from "react-aria-components/Label";
import { Text } from "react-aria-components/Text";
import { TextField } from "react-aria-components/TextField";
import type { FieldDescriptor } from "../api/v2";
import type { Notice } from "../api/notices";
import { usePresentation } from "../presentation/context";

type DescriptorFieldProps = {
  field: FieldDescriptor;
  value: string;
  inputName?: string;
  error?: Notice | undefined;
  onChange: (value: string) => void;
};
export const DescriptorField = ({
  field,
  value,
  inputName,
  error,
  onChange,
}: DescriptorFieldProps) => {
  const p = usePresentation();
  return (
    <TextField
      className="text-field"
      isInvalid={error !== undefined}
      value={value}
      onChange={onChange}
    >
      <Label>{p.fieldLabel(field.name)}</Label>
      <Input
        name={inputName ?? field.name}
        data-field-name={field.name}
        type="text"
        dir={field.scalar.kind === "TEXT" ? "auto" : "ltr"}
        autoComplete="off"
      />
      <Text slot="description" className="field-description">
        <small>{p.fieldMeaning(field.name)}</small>
        <small>{p.hint(field)}</small>
      </Text>
      {error === undefined ? null : <FieldError>{p.notice(error)}</FieldError>}
    </TextField>
  );
};
