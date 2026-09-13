import { FieldError } from "react-aria-components/FieldError";
import { Input } from "react-aria-components/Input";
import { Label } from "react-aria-components/Label";
import { Text } from "react-aria-components/Text";
import { TextField } from "react-aria-components/TextField";
import type { FieldDescriptor } from "../api/v2";
import { fieldHint, presentationInputType } from "../domain/metadata";

type DescriptorFieldProps = {
  field: FieldDescriptor;
  value: string;
  error?: string | undefined;
  onChange: (value: string) => void;
};

export const DescriptorField = ({ field, value, error, onChange }: DescriptorFieldProps) => (
  <TextField
    className="text-field"
    isInvalid={error !== undefined}
    value={value}
    onChange={onChange}
  >
    <Label>{field.label}</Label>
    <Input name={field.name} type={presentationInputType(field)} autoComplete="off" />
    <Text slot="description" className="field-description">
      <small>{field.meaning}</small>
      <small>{fieldHint(field)}</small>
    </Text>
    {error === undefined ? null : <FieldError>{error}</FieldError>}
  </TextField>
);
