import { Label } from "react-aria-components/Label";
import { Text } from "react-aria-components/Text";
import { FieldError } from "react-aria-components/FieldError";
import { TextField } from "react-aria-components/TextField";
import type { ReactNode, ComponentProps } from "react";
import { usePresentation } from "../presentation/context";
import "./field.css";

export const FieldGroup = ({ children }: { children: ReactNode }) => (
  <div className="field-group">{children}</div>
);

export const FieldRule = ({ ruleId, name }: { ruleId: string | null; name: string | null }) => {
  const p = usePresentation();
  return ruleId === null ? null : <span className="field-rule">{p.rule(ruleId, name)}</span>;
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
