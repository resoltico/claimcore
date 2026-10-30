import { ActorIdentity, OperationIdentity } from "../components/ActorIdentity";
import { useRequesterContext } from "../hooks/useRequesterContext";
import { IdentifierMessage } from "../presentation/IdentifierMessage";
import type { Notice } from "../protocol/notices";
import { PageStatus } from "../components/PageStatus";
import { useCallback, useState } from "react";
import { Button } from "react-aria-components/Button";
import type { CatalogDocument, HistoryEntry, CaseReceipt, ServiceResponse } from "../api/service";
import { useEndpointPage } from "../hooks/useEndpointPage";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import { HistoryChanges } from "./case/HistoryChanges";
import { ReceiptFields } from "../components/ReceiptFields";
const historyPage = (response: ServiceResponse<"case.history">) =>
  response.outcome.kind === "SUCCEEDED"
    ? {
        items: response.outcome.data.entries,
        nextCursor: response.outcome.data.nextCursor,
      }
    : null;
type HistoryPage = NonNullable<ReturnType<typeof historyPage>>;
const ReceiptAttribution = ({ operationId, actor }: { operationId: string; actor: string }) => (
  <p className="history-attribution">
    <ActorIdentity actorId={actor} />
    <OperationIdentity operationId={operationId} />
  </p>
);
const ReceiptMetadata = ({ receipt }: { receipt: CaseReceipt }) => {
  const p = usePresentation();
  return (
    <>
      <p>
        {p.text("ui.exactUtc")}: <time dateTime={receipt.recordedAt}>{receipt.recordedAt}</time>
      </p>
      <ReceiptAttribution operationId={receipt.operationId} actor={receipt.recordedBy} />
      <dl className="history-exact">
        <dt>{p.fieldLabel("operationId")}</dt>
        <dd>
          <bdi dir="ltr">{receipt.operationId}</bdi>
        </dd>
        <dt>{p.text("ui.actor")}</dt>
        <dd>
          <bdi dir="ltr">{receipt.recordedBy}</bdi>
        </dd>
      </dl>
    </>
  );
};
const HistoryReceipt = ({
  receipt,
  fields,
  previous,
  loadPrevious,
}: {
  receipt: CaseReceipt;
  fields: CatalogDocument["fields"];
  previous: CaseReceipt | undefined;
  loadPrevious: (() => void) | undefined;
}) => {
  const p = usePresentation();
  const context = useRequesterContext();
  return (
    <details>
      <summary>
        {p.text("ui.historySummary", {
          act: p.term("act", receipt.subject.act),
          revision: p.integer(receipt.subject.revision),
          timestamp:
            context === null
              ? receipt.recordedAt
              : p.recordedTime(receipt.recordedAt, context.timeZone),
        })}
      </summary>
      <ReceiptMetadata receipt={receipt} />
      <HistoryChanges
        receipt={receipt}
        previous={previous}
        fields={fields}
        loadPrevious={loadPrevious}
      />
      <ReceiptFields
        subject={receipt.subject}
        fields={fields}
        context={
          <IdentifierMessage
            id="ui.historyContext"
            values={{
              operationId: receipt.operationId.slice(0, 8),
            }}
            argument="operationId"
          />
        }
      />
    </details>
  );
};
const Entry = ({
  entry,
  fields,
  previous,
  loadPrevious,
}: {
  entry: HistoryEntry;
  fields: CatalogDocument["fields"];
  previous: HistoryEntry | undefined;
  loadPrevious: (() => void) | undefined;
}) => {
  const p = usePresentation();
  const context = useRequesterContext();
  if (entry.kind === "FULL") {
    return (
      <HistoryReceipt
        receipt={entry.receipt}
        fields={fields}
        previous={previous?.kind === "FULL" ? previous.receipt : undefined}
        loadPrevious={loadPrevious}
      />
    );
  }
  const { change } = entry;
  return (
    <>
      <p>
        {p.text("ui.historySummary", {
          act: p.term("act", change.act),
          revision: p.integer(change.revision),
          timestamp:
            context === null
              ? change.recordedAt
              : p.recordedTime(change.recordedAt, context.timeZone),
        })}
      </p>
      <ReceiptAttribution operationId={change.operationId} actor={change.recordedBy} />
      <details>
        <summary>{p.text("ui.technicalDetails")}</summary>
        <p>
          {p.text("ui.exactUtc")}: <time dateTime={change.recordedAt}>{change.recordedAt}</time>
        </p>
        <bdi dir="ltr">{change.operationId}</bdi>
        <bdi dir="ltr">{change.recordedBy}</bdi>
      </details>
    </>
  );
};
const HistoryEntries = ({
  items,
  fields,
  loadPrevious,
}: {
  items: ReadonlyArray<HistoryEntry>;
  fields: CatalogDocument["fields"];
  loadPrevious: (() => void) | undefined;
}) => (
  <ol className="history-list case-timeline">
    {items.map((entry, index) => (
      <li key={entry.kind === "FULL" ? entry.receipt.operationId : entry.change.operationId}>
        <Entry
          entry={entry}
          fields={fields}
          previous={items[index + 1]}
          loadPrevious={loadPrevious}
        />
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
            {p.text(mode === "FULL" ? "historyDetail.FULL.label" : "historyDetail.SUMMARY.label")}
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
type CaseHistoryProps = {
  modes?: ReadonlyArray<HistoryMode>;
  onDisclosureRefused?: () => void;

  caseReference: string;
  fields: CatalogDocument["fields"];
};
const HistoryPageActions = ({
  cursor,
  loading,
  load,
}: {
  cursor: string | null;
  loading: boolean;
  load: (cursor: string | null) => Promise<void>;
}) => {
  const p = usePresentation();
  return (
    <>
      <Button onPress={() => void load(null)} isDisabled={loading}>
        {p.text("ui.reloadHistory")}
      </Button>
      {cursor === null ? null : (
        <Button onPress={() => void load(cursor)} isDisabled={loading}>
          {p.text("ui.moreHistory")}
        </Button>
      )}
    </>
  );
};
const precedingPage = (page: {
  cursor: string | null;
  loading: boolean;
  load: (cursor: string | null) => Promise<void>;
}) =>
  page.cursor === null || page.loading
    ? undefined
    : () => {
        void page.load(page.cursor);
      };
export const CaseHistory = ({
  caseReference,
  fields,
  modes = summaryModes,
  onDisclosureRefused,
}: CaseHistoryProps) => {
  const p = usePresentation();
  const [detail, setDetail] = useState<"SUMMARY" | "FULL">("SUMMARY");
  const requestedDetail = modes.includes(detail) ? detail : (modes[0] ?? "SUMMARY");
  const input = useCallback(
    (cursor: string | null) => ({
      caseReference,
      ...(cursor === null ? {} : { cursor }),
      limit: 50,
      detail: requestedDetail,
    }),
    [caseReference, requestedDetail],
  );
  const { items, cursor, message, loading, load, page } = useEndpointPage<
    "case.history",
    HistoryEntry,
    HistoryPage
  >("case.history", input, historyPage, onDisclosureRefused);

  return (
    <section className="case-history" aria-labelledby="history-title">
      <h2 id="history-title">{p.text("ui.acceptedHistory")}</h2>
      <HistoryModePicker detail={requestedDetail} onChange={setDetail} modes={modes} />
      <p>{p.text("ui.historyHint")}</p>
      <HistoryEntries
        items={items}
        fields={fields}
        loadPrevious={precedingPage({ cursor, loading, load })}
      />
      <HistoryPageStatus
        loading={loading}
        loaded={page !== null}
        message={message}
        empty={items.length === 0}
        cursor={cursor}
      />
      <HistoryPageActions cursor={cursor} loading={loading} load={load} />
    </section>
  );
};
