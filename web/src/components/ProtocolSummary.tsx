import { CharacterWarning } from "./BusinessValue";
import { usePresentation } from "../presentation/context";
const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);
/** Exact authored values remain isolated text; the summary never rewrites the prepared body. */
export const ProtocolSummary = ({ value, name = "" }: { value: unknown; name?: string }) => {
  const p = usePresentation();
  if (!isRecord(value)) {
    if (value === "") {
      return <span>{p.text("ui.emptyInput")}</span>;
    }
    if (typeof value === "string" && (name === "kind" || name === "role")) {
      return (
        <>
          <span>{p.token(value)}</span>{" "}
          <code>
            <bdi>{value}</bdi>
          </code>
        </>
      );
    }
    return (
      <span>
        <bdi style={{ whiteSpace: "pre-wrap" }}>
          {typeof value === "boolean" ? p.text(value ? "ui.yes" : "ui.no") : String(value)}
        </bdi>
        {typeof value === "string" ? <CharacterWarning value={value} /> : null}
      </span>
    );
  }
  return (
    <dl>
      {Object.entries(value).map(([childName, child]) => (
        <div key={childName}>
          <dt>{p.fieldLabel(childName)}</dt>
          <dd>
            <ProtocolSummary value={child} name={childName} />
          </dd>
        </div>
      ))}
    </dl>
  );
};
