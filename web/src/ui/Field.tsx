import { Label } from "react-aria-components/Label";
import { Text } from "react-aria-components/Text";
import { FieldError } from "react-aria-components/FieldError";
import { TextField } from "react-aria-components/TextField";
import { createContext, useContext, useState, type ReactNode, type ComponentProps } from "react";
import { usePresentation } from "../presentation/context";
import "./field.css";

const FieldFocus = createContext(false);

export const FieldGroup = ({ children }: { children: ReactNode }) => {
  const [focused, setFocused] = useState(false);
  return (
    <div
      className="field-group"
      onFocusCapture={() => {
        setFocused(true);
      }}
      onBlurCapture={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget)) {
          setFocused(false);
        }
      }}
    >
      <FieldFocus.Provider value={focused}>{children}</FieldFocus.Provider>
    </div>
  );
};

export const FieldRule = ({
  ruleId,
  name,
  id,
  invalid = false,
}: {
  ruleId: string | null;
  name: string | null;
  id?: string;
  invalid?: boolean;
}) => {
  const p = usePresentation();
  const focus = useContext(FieldFocus);
  return ruleId === null || (!focus && !invalid) ? null : (
    <span id={id} className="field-rule">
      {p.rule(ruleId, name)}
    </span>
  );
};

export const Field = ({
  label,
  description,
  error,
  optional,
  children,
  ...control
}: Omit<ComponentProps<typeof TextField>, "children"> & {
  label: string;
  description: ReactNode;
  error?: ReactNode;
  optional?: string | undefined;
  children: ReactNode;
}) => (
  <FieldGroup>
    <TextField {...control} className={["text-field", control.className].filter(Boolean).join(" ")}>
      <div className="field-label-row">
        <Label>{label}</Label>
        {optional === undefined ? null : <span className="field-hint">{optional}</span>}
      </div>
      {children}
      <Text slot="description" className="field-description">
        {description}
      </Text>
      {error === undefined ? null : <FieldError>{error}</FieldError>}
    </TextField>
  </FieldGroup>
);
