import type { Notice } from "../api/notices";
import { PageStatus } from "../components/PageStatus";
import { useCallback, useState } from "react";
import { Button } from "react-aria-components/Button";
import type { CatalogDocument, HistoryEntry, CaseReceipt, ServiceResponse } from "../api/service";
import { service } from "../api/service";
import { useRetryablePage } from "../hooks/useRead";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import { ReceiptFields } from "../components/ReceiptFields";

const historyPage = (response: ServiceResponse<"case.history">) =>
  response.outcome.tag === "SUCCEEDED"
    ? { items: response.outcome.data.entries, nextCursor: response.outcome.data.nextCursor }
    : null;

const HistoryReceipt = ({
  receipt,
  fields,
}: {
  receipt: CaseReceipt;
  fields: CatalogDocument["fields"];
}) => {
  const p = usePresentation();
  return (
    <details>
      <summary>
        {p.text("ui.historySummary", {
          act: p.token(receipt.scope.act),
          revision: p.integer(receipt.scope.revision),
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
      <ReceiptFields
        scope={receipt.scope}
        fields={fields}
        context={p.text("ui.historyContext", { operationId: receipt.operationId })}
      />
    </details>
  );
};

const Entry = ({ entry, fields }: { entry: HistoryEntry; fields: CatalogDocument["fields"] }) => {
  const p = usePresentation();
  if (entry.tag === "FULL") {
    return <HistoryReceipt receipt={entry.receipt} fields={fields} />;
  }
  const { change } = entry;
  return (
    <>
      <p>
        {p.text("ui.historySummary", {
          act: p.token(change.act),
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

const HistoryEntries = ({
  items,
  fields,
}: {
  items: ReadonlyArray<HistoryEntry>;
  fields: CatalogDocument["fields"];
}) => (
  <ol className="history-list">
    {items.map((entry) => (
      <li key={entry.tag === "FULL" ? entry.receipt.operationId : entry.change.operationId}>
        <Entry entry={entry} fields={fields} />
      </li>
    ))}
  </ol>
);

type HistoryMode = "SUMMARY" | "FULL";
const HistoryModePicker = ({
  detail,
  onChange,
  modes,
}: {
  modes: ReadonlyArray<HistoryMode>;
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
        {modes.map((mode) => (
          <option key={mode} value={mode}>
            {p.text(mode === "FULL" ? "ui.fullHistory" : "ui.summaryHistory")}
          </option>
        ))}
      </select>
    </label>
  );
};

const HistoryPageStatus = ({
  loading,
  loaded,
  message,
  empty,
  cursor,
}: {
  loading: boolean;
  loaded: boolean;
  message: Notice | null;
  empty: boolean;
  cursor: string | null;
}) => {
  const p = usePresentation();
  return (
    <>
      {message === null ? null : (
        <p className="error" role="alert">
          <NoticeView value={message} />
        </p>
      )}
      {loading ? <p role="status">{p.text("ui.loadingHistory")}</p> : null}
      {loaded && !loading && message === null ? (
        <PageStatus empty={empty} cursor={cursor} emptyMessage="ui.noAccessibleHistory" />
      ) : null}
    </>
  );
};

const summaryModes: ReadonlyArray<HistoryMode> = ["SUMMARY"];
export const CaseHistory = ({
  token,
  caseReference,
  fields,
  modes = summaryModes,
  onDisclosureRefused,
}: {
  modes?: ReadonlyArray<HistoryMode>;
  onDisclosureRefused?: () => void;
  token: string;
  caseReference: string;
  fields: CatalogDocument["fields"];
}) => {
  const p = usePresentation();
  const [detail, setDetail] = useState<"SUMMARY" | "FULL">("SUMMARY");
  const requestedDetail = modes.includes(detail) ? detail : (modes[0] ?? "SUMMARY");
  const request = useCallback(
    (cursor: string | null, signal: AbortSignal) =>
      service.history({ caseReference, cursor, limit: 50, detail: requestedDetail }, token, signal),
    [caseReference, token, requestedDetail],
  );
  const { items, cursor, message, loading, load, page } = useRetryablePage<
    ServiceResponse<"case.history">,
    HistoryEntry
  >(request, historyPage, onDisclosureRefused);
  return (
    <section aria-labelledby="history-title">
      <h2 id="history-title">{p.text("ui.acceptedHistory")}</h2>
      <HistoryModePicker detail={requestedDetail} onChange={setDetail} modes={modes} />
      <p>{p.text("ui.historyHint")}</p>
      <HistoryEntries items={items} fields={fields} />
      <HistoryPageStatus
        loading={loading}
        loaded={page !== null}
        message={message}
        empty={items.length === 0}
        cursor={cursor}
      />
      <Button onPress={() => void load(null)} isDisabled={loading}>
        {p.text("ui.reloadHistory")}
      </Button>
      {cursor === null ? null : (
        <Button onPress={() => void load(cursor)} isDisabled={loading}>
          {p.text("ui.moreHistory")}
        </Button>
      )}
    </section>
  );
};
