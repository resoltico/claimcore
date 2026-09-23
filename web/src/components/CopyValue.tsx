import { useState } from "react";
import { Button } from "react-aria-components/Button";
import { usePresentation } from "../presentation/context";

type CopyValueProps = { label: string; value: string };
export const CopyValue = ({ label, value }: CopyValueProps) => {
  const p = usePresentation();
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
        {p.text("ui.copy", { label })}
      </Button>
      <span aria-live="polite">{copied ? p.text("ui.copied", { label }) : ""}</span>
      {showFallback ? (
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
