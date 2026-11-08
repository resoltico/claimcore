import { useEffect, useRef, useState, useId } from "react";
import type {
  HistoryDisclosure,
  PresentedView,
  ConflictChangePage,
  CaseSelector,
} from "@contracts/client";
import {
  requestEndpoint,
  resultNotice,
  type ApiResult,
  type ServiceResponse,
} from "../../api/service";
import { permitsHistory } from "../../api/historyDisclosure";
import { isDisclosureRefused } from "../../api/outcomes";
import { TableView } from "../../ui/TableView";
import { RecordView } from "../../ui/RecordView";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
import type { Notice } from "../../protocol/notices";
import type { RevisionConflictSnapshot } from "../../operation/operationTypes";
import type { OperationModel } from "../../hooks/operation/useOperation";
import { LifecycleStatus } from "../LifecycleStatus";
import "./conflict.css";
type Scope = {
  target: CaseSelector;
  credential: string;
  onRefused: () => void;
  onDisclosureChanged: () => void;
};
const ConflictPage = ({
  initial,
  detail,
  scope,
  timeZone,
}: {
  initial: ConflictChangePage;
  detail: "CURRENT" | "SUMMARY" | "ERASED";
  scope: Scope;
  timeZone: string;
}) => {
  const p = usePresentation();
  const ceiling: HistoryDisclosure = detail === "CURRENT" ? "FULL" : detail;
  const initialAllowed = permitsHistory(ceiling, initial.disclosure);
  const [history, setHistory] = useState<{
    detail: typeof detail;
    pages: ReadonlyArray<{ view: PresentedView; disclosure: HistoryDisclosure }>;
  }>({
    detail,
    pages: initialAllowed ? [{ view: initial.view, disclosure: initial.disclosure }] : [],
  });
  if (history.detail !== detail) {
    setHistory({
      detail,
      pages: history.pages.filter((page) => permitsHistory(ceiling, page.disclosure)),
    });
  }
  const previousDisclosure = useRef(initial.disclosure);
  const [cursor, setCursor] = useState(initial.nextCursor);
  const [loading, setLoading] = useState(false);
  const [message, setMessage] = useState<Notice | null>(null);
  const pending = useRef<AbortController | null>(null);
  useEffect(
    () => () => {
      pending.current?.abort();
    },
    [],
  );
  const refusedPage = (result: ApiResult<ServiceResponse<"case.timeline">>) => {
    const invalidCursor =
      result.kind === "outcome" &&
      result.value.outcome.kind === "REJECTED" &&
      result.value.outcome.data.code === "INVALID_CURSOR";
    const refused = isDisclosureRefused(result);
    if (invalidCursor || refused) {
      setHistory((prior) => ({ ...prior, pages: [] }));
      setCursor(null);
    }
    setMessage(resultNotice(result));
    if (refused) {
      scope.onRefused();
    }
  };
  const load = async () => {
    if (cursor === null || pending.current !== null) {
      return;
    }
    const controller = new AbortController();
    pending.current = controller;
    setLoading(true);
    setMessage(null);
    try {
      const result = await requestEndpoint(
        "case.timeline",
        scope.credential,
        { target: scope.target, cursor, limit: 50 },
        controller.signal,
      );
      if (controller.signal.aborted) {
        return;
      }
      if (result.kind === "outcome" && result.value.outcome.kind === "SUCCEEDED") {
        const { outcome } = result.value;
        const page = outcome.data;
        const changed = previousDisclosure.current !== page.disclosure;
        previousDisclosure.current = page.disclosure;
        const presented = { view: outcome.view, disclosure: page.disclosure };
        setHistory((prior) => ({
          ...prior,
          pages: changed ? [presented] : [...prior.pages, presented],
        }));
        setCursor(page.nextCursor);
        if (changed) {
          scope.onDisclosureChanged();
        }
      } else {
        refusedPage(result);
      }
    } finally {
      if (pending.current === controller) {
        pending.current = null;
        if (!controller.signal.aborted) {
          setLoading(false);
        }
      }
    }
  };
  return (
    <div aria-busy={loading}>
      {initialAllowed ? null : <p>{p.text("ui.timeline.withheld")}</p>}
      {history.pages
        .filter((page) => permitsHistory(ceiling, page.disclosure))
        .map(({ view }, index) =>
          view.kind === "TABLE" ? (
            // oxlint-disable-next-line react/no-array-index-key -- Append-only supplied pages clear together on refusal.
            <TableView key={index} view={view} timeZone={timeZone} />
          ) : (
            // oxlint-disable-next-line react/no-array-index-key -- Append-only supplied pages clear together on refusal.
            <RecordView key={index} view={view} timeZone={timeZone} />
          ),
        )}
      {message === null ? null : (
        <p role="alert">
          <NoticeView value={message} />
        </p>
      )}
      {cursor === null ? null : (
        <button type="button" disabled={loading} onClick={() => void load()}>
          {p.text("ui.timeline.more")}
        </button>
      )}
    </div>
  );
};
const detailFor = (snapshot: RevisionConflictSnapshot) => {
  if (snapshot.latest === null) {
    return "CURRENT";
  }
  if (snapshot.latest.current.state.kind === "ERASED") {
    return "ERASED";
  }
  return snapshot.latest.current.advice.history === "FULL" ? "CURRENT" : "SUMMARY";
};
export const ConflictChanges = ({
  model,
  snapshot,
}: {
  model: OperationModel;
  snapshot: RevisionConflictSnapshot;
}) => {
  const p = usePresentation();
  const heading = useRef<HTMLHeadingElement>(null);
  const title = useId();
  useEffect(() => {
    heading.current?.focus();
  }, []);
  const { changes } = snapshot.diagnostic;
  const metadataAllowed =
    snapshot.latest === null || snapshot.latest.current.advice.history !== "NONE";
  const detail = detailFor(snapshot);
  const onDisclosureChanged = () => {
    model.invalidateDisclosure();
    model.revalidateDisclosure();
  };
  return (
    <section className="revision-conflict" aria-labelledby={title}>
      <h2 ref={heading} id={title} tabIndex={-1}>
        {p.text("ui.conflict.title")}
      </h2>
      <p>{p.text("ui.conflict.kept")}</p>
      {snapshot.latest === null ? null : <LifecycleStatus value={snapshot.latest.current.stages} />}
      {changes.kind === "WITHHELD" || !metadataAllowed ? (
        <p>{p.text("ui.timeline.withheld")}</p>
      ) : (
        <ConflictPage
          key={snapshot.identity.id}
          initial={changes.page}
          detail={detail}
          scope={{
            target: snapshot.source.prepared.input.target,
            credential: snapshot.identity.credential,
            onRefused: onDisclosureChanged,
            onDisclosureChanged: model.revalidateDisclosure,
          }}
          timeZone={snapshot.latest?.current.work.basis.timeZone ?? "UTC"}
        />
      )}
      {model.revalidating ? <p role="status">{p.text("ui.conflict.loading")}</p> : null}
      {snapshot.latest === null ? (
        <button type="button" disabled={model.revalidating} onClick={model.revalidateDisclosure}>
          {p.text("ui.reloadCurrentCase")}
        </button>
      ) : (
        <button
          type="button"
          className="primary"
          disabled={model.busy || snapshot.latest.revision === null}
          onClick={() => void model.reviewLatest()}
        >
          {p.text("ui.conflict.reviewLatest")}
        </button>
      )}
    </section>
  );
};
