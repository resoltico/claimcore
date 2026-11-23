import { Label } from "react-aria-components/Label";
import { Text } from "react-aria-components/Text";
import { FieldError } from "react-aria-components/FieldError";
import { TextField } from "react-aria-components/TextField";
import type { ReactNode, ComponentProps } from "react";
import { useEffect, useRef, useState } from "react";
import { usePresentation } from "../presentation/context";
import "./field.css";

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
  const placeholder = useRef<HTMLElement>(null);
  const [focus, setFocus] = useState(false);
  useEffect(() => {
    const field = placeholder.current?.closest(".text-field, .assignee-input");
    if (field === null || field === undefined) {
      return;
    }
    const enter = () => {
      setFocus(true);
    };
    const leave = (event: Event) => {
      if (
        event instanceof FocusEvent &&
        event.relatedTarget instanceof Node &&
        field.contains(event.relatedTarget)
      ) {
        return;
      }
      setFocus(false);
    };
    field.addEventListener("focusin", enter);
    field.addEventListener("focusout", leave);
    return () => {
      field.removeEventListener("focusin", enter);
      field.removeEventListener("focusout", leave);
    };
  }, []);
  return ruleId === null ? null : (
    <small id={id} ref={placeholder} className="field-rule" hidden={!focus && !invalid}>
      {focus || invalid ? p.rule(ruleId, name) : null}
    </small>
  );
};

export const Field = ({
  label,
  description,
  error,
  children,
  ...control
}: Omit<ComponentProps<typeof TextField>, "children"> & {
  label: string;
  description: ReactNode;
  error?: ReactNode;
  children: ReactNode;
}) => (
  <TextField {...control} className={["text-field", control.className].filter(Boolean).join(" ")}>
    <div className="field-label-row">
      <Label>{label}</Label>
    </div>
    {children}
    <Text slot="description" className="field-description">
      {description}
    </Text>
    {error === undefined ? null : <FieldError>{error}</FieldError>}
  </TextField>
);
