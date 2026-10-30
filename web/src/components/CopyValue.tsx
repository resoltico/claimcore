import { CharacterWarning } from "./BusinessValue";
import { useLayoutEffect, useRef, useState } from "react";
import { Button } from "react-aria-components/Button";
import { usePresentation } from "../presentation/context";

type CopyValueProps = { label: string; value: string; icon?: boolean };
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

export const CopyValue = ({ label, value, icon = false }: CopyValueProps) => {
  const p = usePresentation();
  const { status, copy } = useClipboardFeedback({ label, value });
  return (
    <span className={icon ? "copy-value copy-value-icon" : "copy-value"}>
      <Button
        aria-description={p.text("ui.canonicalCopyHint")}
        aria-label={p.text("ui.copy", { label })}
        className={`copy-button ${icon ? "copy-icon" : ""}`}
        onPress={() => void copy()}
      >
        {icon ? (
          <svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true" focusable="false">
            <rect x="8" y="8" width="12" height="13" rx="2" fill="none" stroke="currentColor" />
            <path
              d="M16 8V4a1 1 0 0 0-1-1H4a1 1 0 0 0-1 1v12a1 1 0 0 0 1 1h4"
              fill="none"
              stroke="currentColor"
            />
          </svg>
        ) : (
          <span title={p.text("ui.canonicalCopyHint")}>{p.text("ui.copy", { label })}</span>
        )}
      </Button>
      <span className={icon ? "sr-only" : undefined} aria-live="polite">
        {status === "copied" ? p.text("ui.copied", { label }) : ""}
      </span>
      {status === "fallback" ? (
        <span>
          <CharacterWarning value={value} />
          <textarea
            aria-label={p.text("ui.copyFallback", { label })}
            readOnly
            dir="auto"
            value={value}
          />
        </span>
      ) : null}
    </span>
  );
};
