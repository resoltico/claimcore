import { useCallback, useState } from "react";
import type {
  CaseSelector,
  CaseActivityPage,
  CaseActivity as Activity,
  ActivitySubject,
  ResponseByEndpoint,
} from "@contracts/client";
import { useEndpointPage } from "../../hooks/useEndpointPage";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { NoticeView } from "../../presentation/Message";
import { productMetadata } from "../../protocol/productMetadata";
import { ProtocolSummary } from "../../components/ProtocolSummary";
import { IdentifierValue } from "../../components/IdentifierValue";
const revisionLabel = (subject: ActivitySubject) => {
  if (subject.kind === "WORK") {
    return "ui.workRevision";
  }
  return subject.kind === "REMINDER" ? "ui.reminderRevision" : "ui.revision";
};
const select = (reply: ResponseByEndpoint["case.activity"]): CaseActivityPage | null =>
  reply.outcome.kind === "SUCCEEDED" ? reply.outcome.data : null;
const CaseActivity = ({
  target,
  modes,
  onDisclosureRefused,
}: {
  target: CaseSelector;
  modes: ReadonlyArray<"SUMMARY" | "FULL">;
  onDisclosureRefused: () => void;
}) => {
  const p = usePresentation();
  const [selectedDetail, setSelectedDetail] = useState<"SUMMARY" | "FULL">("SUMMARY");
  const detail = modes.includes(selectedDetail) ? selectedDetail : (modes[0] ?? "SUMMARY");
  const input = useCallback(
    (cursor: string | null) => ({
      target,
      detail,
      limit: 50,
      ...(cursor === null ? {} : { cursor }),
    }),
    [target, detail],
  );
  const result = useEndpointPage<"case.activity", Activity, CaseActivityPage>(
    "case.activity",
    input,
    select,
    onDisclosureRefused,
  );
  return (
    <section aria-labelledby="case-activity-title" aria-busy={result.loading}>
      <h2 id="case-activity-title">{p.text("ui.caseActivity")}</h2>
      {modes.includes("FULL") ? (
        <label>
          {p.text("ui.caseActivity")}
          <select
            value={detail}
            onChange={(event) => {
              const { value } = event.target;
              if (value === "SUMMARY" || value === "FULL") {
                setSelectedDetail(value);
              }
            }}
          >
            {modes.map((mode) => (
              <option key={mode} value={mode}>
                {p.term("historyDetail", mode)}
              </option>
            ))}
          </select>
        </label>
      ) : null}
      {result.message === null ? null : (
        <p role="alert">
          <NoticeView value={result.message} />
        </p>
      )}
      {result.loading ? <p role="status">{p.text("ui.loadingHistory")}</p> : null}
      <p className="observation-basis">{p.text("ui.liveWorkPages")}</p>
      <ol>
        {result.items.map((event) => {
          const entry = productMetadata.catalog.entries.find(
            (candidate) =>
              candidate.subject === event.subject.kind &&
              candidate.constants.some(
                (constant) =>
                  constant.path.join(".") === "act.kind" && constant.value === event.subject.act,
              ),
          );
          return (
            <li key={event.operationId}>
              <p>
                {entry === undefined ? event.subject.act : renderCatalogKey(p, entry.labelId)} ·{" "}
                <bdi dir="ltr">{event.recordedAt}</bdi>
              </p>
              <details>
                <summary>{p.text("ui.technicalDetails")}</summary>
                <p>
                  {p.text("ui.recordedBy")}: <IdentifierValue value={event.recordedBy} />
                </p>
                <IdentifierValue value={event.operationId} />
                <p>
                  {p.text(revisionLabel(event.subject), {
                    revision: p.integer(event.subject.revision),
                  })}
                </p>
                {event.subject.kind === "REMINDER" ? (
                  <>
                    <IdentifierValue value={event.subject.reminderId} />
                    {event.subject.next === null ? null : (
                      <p>
                        <IdentifierValue value={event.subject.next.reminderId} /> ·{" "}
                        {p.text("ui.reminderRevision", {
                          revision: p.integer(event.subject.next.revision),
                        })}
                      </p>
                    )}
                  </>
                ) : null}
              </details>
              {detail !== "FULL" ||
              !modes.includes("FULL") ||
              event.detail === null ||
              entry === undefined ? null : (
                <ProtocolSummary value={event.detail} entry={entry} />
              )}
            </li>
          );
        })}
      </ol>
      {result.cursor === null ? null : (
        <button
          type="button"
          disabled={result.loading}
          onClick={() => void result.load(result.cursor)}
        >
          {p.text("ui.moreHistory")}
        </button>
      )}
    </section>
  );
};

export const CaseActivityDisclosure = (props: Parameters<typeof CaseActivity>[0]) => {
  const p = usePresentation();
  const [open, setOpen] = useState(false);
  if (props.modes.length === 0) {
    return null;
  }
  return (
    <details
      className="case-activity-disclosure"
      open={open}
      onToggle={(event) => {
        setOpen(event.currentTarget.open);
      }}
    >
      <summary>{p.text("ui.caseActivity")}</summary>
      {open ? <CaseActivity {...props} /> : null}
    </details>
  );
};
