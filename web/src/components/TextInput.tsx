type TextInputProps = {
  id: string;
  label: string;
  value: string;
  onChange: (value: string) => void;
  error?: string;
  type?: "text" | "password";
};

export const TextInput = ({ id, label, value, onChange, error, type = "text" }: TextInputProps) => (
  <TextField
    className="text-field"
    isInvalid={error !== undefined}
    value={value}
    onChange={onChange}
  >
    <Label htmlFor={id}>{label}</Label>
    <Input id={id} type={type} autoComplete="off" />
    {error === undefined ? null : <FieldError>{error}</FieldError>}
  </TextField>
);
import { FieldError } from "react-aria-components/FieldError";
import { Input } from "react-aria-components/Input";
import { Label } from "react-aria-components/Label";
import { TextField } from "react-aria-components/TextField";
