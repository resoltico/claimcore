import { useState } from "react";
import { Button } from "react-aria-components/Button";

type CopyValueProps = { label: string; value: string };

export const CopyValue = ({ label, value }: CopyValueProps) => {
  const [copied, setCopied] = useState(false);
  const [showFallback, setShowFallback] = useState(false);

  const copy = async (): Promise<void> => {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(true);
      setShowFallback(false);
    } catch {
      setCopied(false);
      setShowFallback(true);
    }
  };

  return (
    <span className="copy-value">
      <Button className="copy-button" onPress={() => void copy()}>
        Copy {label}
      </Button>
      <span aria-live="polite">{copied ? `${label} copied.` : ""}</span>
      {showFallback ? (
        <textarea aria-label={`${label} copy fallback`} readOnly value={value} />
      ) : null}
    </span>
  );
};
