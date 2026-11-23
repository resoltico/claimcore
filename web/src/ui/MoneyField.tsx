import { useId, type ReactNode } from "react";
import "./money-field.css";

export const MoneyField = ({
  label,
  error,
  children,
}: {
  label: ReactNode;
  error?: ReactNode;
  children: ReactNode;
}) => {
  const feedback = useId();
  return (
    <fieldset className="money-field" aria-describedby={error === undefined ? undefined : feedback}>
      <legend>{label}</legend>
      <div className="money-field-inputs">{children}</div>
      {error === undefined ? null : <div id={feedback}>{error}</div>}
    </fieldset>
  );
};
