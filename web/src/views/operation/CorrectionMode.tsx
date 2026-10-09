import type { Notice } from "../../api/notices";
import type { CorrectionGroupName } from "../../domain/metadata";
import { NoticeView } from "../../presentation/Message";
import { usePresentation } from "../../presentation/context";

export const CorrectionMode = ({
  name,
  mode,
  actions,
  locked,
  error,
  onChange,
}: {
  name: CorrectionGroupName;
  mode: string;
  actions: ReadonlyArray<"KEEP" | "REPLACE" | "CLEAR">;
  locked: boolean;
  error: Notice | undefined;
  onChange: (next: string) => void;
}) => {
  const p = usePresentation();
  return (
    <>
      <label htmlFor={`correction-${name}-mode`}>{p.text("ui.action")}</label>
      <select
        id={`correction-${name}-mode`}
        name={`${name}.action`}
        data-field-name={name}
        aria-invalid={error !== undefined || undefined}
        aria-describedby={error === undefined ? undefined : `correction-${name}-error`}
        value={mode}
        disabled={locked}
        onChange={(event) => {
          onChange(event.target.value);
        }}
      >
        {actions.map((action) => (
          <option key={action} value={action}>
            {p.token(action)}
          </option>
        ))}
      </select>
      {error === undefined ? null : (
        <p id={`correction-${name}-error`} className="error">
          <NoticeView value={error} />
        </p>
      )}
    </>
  );
};
