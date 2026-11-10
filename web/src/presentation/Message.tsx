import { PresentedValue } from "../components/PresentedValue";
import { renderCatalogKey } from "./messages";
import { SuppliedMessage } from "../ui/SuppliedMessage";
import { IdentifierValue } from "../components/IdentifierValue";
import { CopyValue } from "../components/CopyValue";
import type { Notice } from "../protocol/notices";
import { usePresentation } from "./context";
import type { MessageArgs, MessageKey, Values } from "./types";

type Props<K extends MessageKey> = { id: K } & (MessageArgs[K] extends Readonly<
  Record<string, never>
>
  ? { values?: never }
  : { values: MessageArgs[K] });
export const Message = <K extends MessageKey>({ id, values }: Props<K>) => {
  const p = usePresentation();
  // The prop union is checked at call sites; rendering uses the same catalog shape validator.
  // oxlint-disable-next-line typescript/no-unsafe-type-assertion
  return <>{p.text(id, ...([values] as Values<K>))}</>;
};
export const NoticeView = ({ value }: { value: Notice }) => {
  const p = usePresentation();
  if (value.kind === "humanIssues") {
    return (
      <>
        {value.issues.map((issue) => (
          <span key={JSON.stringify(issue)} className="input-issue">
            <SuppliedMessage message={issue.message} timeZone="UTC" inputIssue />
            {issue.related === null ? null : (
              <span>
                {" "}
                {renderCatalogKey(p, issue.related.labelId)}:{" "}
                <PresentedValue value={issue.related.value} timeZone="UTC" />
              </span>
            )}
          </span>
        ))}
      </>
    );
  }
  if (value.kind === "human") {
    return <SuppliedMessage message={value.advice.message} timeZone="UTC" />;
  }
  return (
    <>
      {value.kind === "accepted" ? (
        <>
          <span className="recovery-state" data-state="RECORDED">
            {p.text("label.recorded")}
          </span>
          <details data-region="DETAILS">
            <summary>{p.text("label.recordDetails")}</summary>
            <IdentifierValue value={value.operationId} />
            <CopyValue label={p.fieldLabel("operationId")} value={value.operationId} />
          </details>
        </>
      ) : (
        p.notice(value)
      )}
      {value.kind === "local" && value.reason === "definitionMismatch" ? (
        <button
          type="button"
          onClick={() => {
            window.location.reload();
          }}
        >
          {p.text("ui.reloadContract")}
        </button>
      ) : null}
    </>
  );
};
