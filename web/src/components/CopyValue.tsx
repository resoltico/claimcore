import { useLayoutEffect, useRef, useState } from "react";
import { Button } from "react-aria-components/Button";
import { usePresentation } from "../presentation/context";

type CopyValueProps = { label: string; value: string };
const useClipboardFeedback = ({ label, value }: CopyValueProps) => {
  const [feedback, setFeedback] = useState<{
    value: string;
    label: string;
    status: "copied" | "fallback";
  } | null>(null);
  const active = useRef<{ value: string; label: string } | null>(null);
  useLayoutEffect(() => {
    active.current = { value, label };
    return () => {
      active.current = null;
    };
  }, [value, label]);
  if (
    feedback !== null &&
    (feedback.value !== value || (feedback.status === "copied" && feedback.label !== label))
  ) {
    setFeedback(null);
  }
  const status =
    feedback?.value === value && (feedback.status === "fallback" || feedback.label === label)
      ? feedback.status
      : null;
  const copy = async (): Promise<void> => {
    const identity = { value, label };
    active.current = identity;
    setFeedback(null);
    try {
      await navigator.clipboard.writeText(value);
      if (identity === active.current) {
        setFeedback({ value, label, status: "copied" });
      }
    } catch {
      if (identity === active.current) {
        setFeedback({ value, label, status: "fallback" });
      }
    }
  };
  return { status, copy };
};

export const CopyValue = ({ label, value }: CopyValueProps) => {
  const p = usePresentation();
  const { status, copy } = useClipboardFeedback({ label, value });
  return (
    <span className="copy-value">
      <Button className="copy-button" onPress={() => void copy()}>
        {p.text("ui.copy", { label })}
      </Button>
      <span aria-live="polite">{status === "copied" ? p.text("ui.copied", { label }) : ""}</span>
      {status === "fallback" ? (
        <textarea
          aria-label={p.text("ui.copyFallback", { label })}
          readOnly
          dir="auto"
          value={value}
        />
      ) : null}
    </span>
  );
};
