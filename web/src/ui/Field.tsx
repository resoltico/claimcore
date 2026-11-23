import { Label } from "react-aria-components/Label";
import { Text } from "react-aria-components/Text";
import { FieldError } from "react-aria-components/FieldError";
import { TextField } from "react-aria-components/TextField";
import {
  createContext,
  useContext,
  useEffect,
  useRef,
  useState,
  type ReactNode,
  type ComponentProps,
} from "react";
import { usePresentation } from "../presentation/context";
import "./field.css";

const FieldFocus = createContext(false);

export const FieldGroup = ({ children }: { children: ReactNode }) => {
  const [focused, setFocused] = useState(false);
  const field = useRef<HTMLDivElement>(null);
  const gesture = useRef<number | null>(null);
  useEffect(() => {
    const element = field.current;
    if (!focused || element === null) {
      return;
    }
    const document = element.ownerDocument;
    let frame: number | null = null;
    const capture = (event: PointerEvent) => {
      if (event.isPrimary && event.target instanceof Node && !element.contains(event.target)) {
        gesture.current = event.pointerId;
      }
    };
    const release = (event: PointerEvent) => {
      if (event.pointerId !== gesture.current) {
        return;
      }
      gesture.current = null;
      // A disappearing rule must not move the destination before its click is delivered.
      frame = requestAnimationFrame(() => {
        setFocused(element.contains(document.activeElement));
      });
    };
    document.addEventListener("pointerdown", capture, true);
    document.addEventListener("pointerup", release, true);
    document.addEventListener("pointercancel", release, true);
    return () => {
      document.removeEventListener("pointerdown", capture, true);
      document.removeEventListener("pointerup", release, true);
      document.removeEventListener("pointercancel", release, true);
      if (frame !== null) {
        cancelAnimationFrame(frame);
      }
    };
  }, [focused]);
  return (
    <div
      ref={field}
      className="field-group"
      onFocusCapture={() => {
        setFocused(true);
      }}
      onBlurCapture={(event) => {
        if (gesture.current === null && !event.currentTarget.contains(event.relatedTarget)) {
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
