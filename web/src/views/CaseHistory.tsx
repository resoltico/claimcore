import { useCallback, useState } from "react";
import { Button } from "react-aria-components/Button";
import type { FieldDescriptor, HistoryEntry, Receipt, ServiceResponse } from "../api/service";
import { service } from "../api/service";
import { useRetryablePage } from "../hooks/useRead";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import { CaseFieldsView } from "../components/CaseFieldsView";

const historyPage = (response: ServiceResponse<"case.history">) => {
  const { outcome } = response;
  if (outcome.tag !== "SUCCEEDED") {
    return null;
  }
  if (outcome.data.tag === "NOT_FOUND") {
    return { items: [], nextCursor: null };
  }
  return { items: outcome.data.entries, nextCursor: outcome.data.nextCursor };
};

const HistoryReceipt = ({
  receipt,
  fields,
}: {
  receipt: Receipt;
  fields: ReadonlyArray<FieldDescriptor>;
}) => {
  const p = usePresentation();
  return (
    <details>
      <summary>
        {p.text("ui.historySummary", {
          command: p.actLabel(receipt.act),
          revision: p.integer(receipt.revision),
          timestamp: receipt.recordedAt,
        })}
      </summary>
      <p>
        {p.text("ui.receiptAttribution", {
          operationId: receipt.operationId,
          actor: receipt.recordedBy,
          state: p.text("ui.accepted"),
        })}
      </p>
      {receipt.snapshot === null ? (
        <p>{p.text("ui.lifecycleErased")}</p>
      ) : (
        <CaseFieldsView
          caseView={receipt.snapshot}
          fields={fields}
          context={p.text("ui.historyContext", { operationId: receipt.operationId })}
        />
      )}
    </details>
  );
};

const Entry = ({
  entry,
  fields,
}: {
  entry: HistoryEntry;
  fields: ReadonlyArray<FieldDescriptor>;
}) => {
  const p = usePresentation();
  if (entry.tag === "FULL") {
    return <HistoryReceipt receipt={entry.receipt} fields={fields} />;
  }
  const { change } = entry;
  return (
    <>
      <p>
        {p.text("ui.historySummary", {
          command: p.actLabel(change.act),
          revision: p.integer(change.revision),
          timestamp: change.recordedAt,
        })}
      </p>
      <p>
        {p.text("ui.receiptAttribution", {
          operationId: change.operationId,
          actor: change.recordedBy,
          state: p.text("ui.accepted"),
        })}
      </p>
    </>
  );
};

type HistoryMode = "SUMMARY" | "FULL";
const HistoryModePicker = ({
  detail,
  onChange,
}: {
  detail: HistoryMode;
  onChange: (mode: HistoryMode) => void;
}) => {
  const p = usePresentation();
  return (
    <label>
      {p.text("ui.historyDetail")}
      <select
        aria-label={p.text("ui.historyDetail")}
        value={detail}
        onChange={(event) => {
          onChange(event.target.value === "FULL" ? "FULL" : "SUMMARY");
        }}
      >
        <option value="SUMMARY">{p.text("ui.summaryHistory")}</option>
        <option value="FULL">{p.text("ui.fullHistory")}</option>
      </select>
    </label>
  );
};

export const CaseHistory = ({
  token,
  caseReference,
  fields,
}: {
  token: string;
  caseReference: string;
  fields: ReadonlyArray<FieldDescriptor>;
}) => {
  const p = usePresentation();
  const [detail, setDetail] = useState<"SUMMARY" | "FULL">("SUMMARY");
  const request = useCallback(
    (cursor: string | null, signal: AbortSignal) =>
      service.history({ caseReference, cursor, limit: 50, detail }, token, signal),
    [caseReference, token, detail],
  );
  const { items, cursor, message, loading, load } = useRetryablePage<
    ServiceResponse<"case.history">,
    HistoryEntry
  >(request, historyPage);
  return (
    <section aria-labelledby="history-title">
      <h2 id="history-title">{p.text("ui.acceptedHistory")}</h2>
      <HistoryModePicker detail={detail} onChange={setDetail} />
      <p>{p.text("ui.historyHint")}</p>
      <ol className="history-list">
        {items.map((entry) => (
          <li key={entry.tag === "FULL" ? entry.receipt.operationId : entry.change.operationId}>
            <Entry entry={entry} fields={fields} />
          </li>
        ))}
      </ol>
      {message === null ? null : (
        <p className="error" role="alert">
          <NoticeView value={message} />
        </p>
      )}
      {loading ? <p role="status">{p.text("ui.loadingHistory")}</p> : null}
      {cursor === null ? null : (
        <Button onPress={() => void load(cursor)} isDisabled={loading}>
          {p.text("ui.moreHistory")}
        </Button>
      )}
    </section>
  );
};
