import { usePresentation } from "../presentation/context";
const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);
/** Exact authored values remain isolated text; the summary never rewrites the prepared body. */
export const ProtocolSummary = ({ value }: { value: unknown }) => {
  const p = usePresentation();
  if (!isRecord(value)) {
    return (
      <bdi>{typeof value === "boolean" ? p.text(value ? "ui.yes" : "ui.no") : String(value)}</bdi>
    );
  }
  return (
    <dl>
      {Object.entries(value).map(([name, child]) => (
        <div key={name}>
          <dt>{p.fieldLabel(name)}</dt>
          <dd>
            <ProtocolSummary value={child} />
          </dd>
        </div>
      ))}
    </dl>
  );
};
