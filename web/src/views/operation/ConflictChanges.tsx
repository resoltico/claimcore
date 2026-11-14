import { useEffect, useRef, useState, useId } from "react";
import type { HistoryDisclosure, Screen, HumanDocument, CaseSelector } from "@contracts/client";
import {
  requestEndpoint,
  resultNotice,
  type ApiResult,
  type ServiceResponse,
} from "../../api/service";
import { permitsHistory } from "../../api/historyDisclosure";
import { isDisclosureRefused } from "../../api/outcomes";
import { ScreenView } from "../../ui/ScreenView";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
import type { Notice } from "../../protocol/notices";
import type { RevisionConflictSnapshot } from "../../operation/operationTypes";
import type { OperationModel } from "../../hooks/operation/useOperation";
import {} from "./conflict.css";
const historyDisclosure = (document: HumanDocument): HistoryDisclosure | null => {
  switch (document.disclosure) {
    case "FULL":
    case "SUMMARY":
    case "ERASED":
      return document.disclosure;
    default:
      return null;
  }
};
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
  initial: HumanDocument;
  detail: "CURRENT" | "SUMMARY" | "ERASED";
  scope: Scope;
  timeZone: string;
}) => {
  const p = usePresentation();
  const ceiling: HistoryDisclosure = detail === "CURRENT" ? "FULL" : detail;
  const initialDisclosure = historyDisclosure(initial);
  const initialAllowed = initialDisclosure !== null && permitsHistory(ceiling, initialDisclosure);
  const [history, setHistory] = useState<{
    detail: typeof detail;
    pages: ReadonlyArray<{ view: Screen; disclosure: HistoryDisclosure }>;
  }>({
    detail,
    pages: initialAllowed ? [{ view: initial.screen, disclosure: initialDisclosure }] : [],
  });
  if (history.detail !== detail) {
    setHistory({
      detail,
      pages: history.pages.filter((page) => permitsHistory(ceiling, page.disclosure)),
    });
  }
  const previousDisclosure = useRef(initialDisclosure);
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
    const rejected = result.kind === "outcome" && result.value.outcome.kind === "REJECTED";
    const refused = isDisclosureRefused(result);
    if (rejected || refused) {
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
        const page = outcome.document;
        const disclosure = historyDisclosure(page);
        if (disclosure === null) {
          setHistory((prior) => ({ ...prior, pages: [] }));
          setCursor(null);
          return;
        }
        const changed = previousDisclosure.current !== disclosure;
        previousDisclosure.current = disclosure;
        const presented = { view: page.screen, disclosure };
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
      {initialAllowed ? null : <p>{p.text("label.changesWithheld")}</p>}
      {history.pages
        .filter((page) => permitsHistory(ceiling, page.disclosure))
        .map(({ view }, index) => (
          // oxlint-disable-next-line react/no-array-index-key -- Append-only pages clear on disclosure refusal.
          <ScreenView key={index} view={view} timeZone={timeZone} />
        ))}
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
  if (snapshot.latest.current.disclosure === "ERASED") {
    return "ERASED";
  }
  return snapshot.latest.current.history === "FULL" ? "CURRENT" : "SUMMARY";
};
const conflictDocument = (snapshot: RevisionConflictSnapshot) =>
  snapshot.diagnostic?.document ?? null;
export const ConflictChanges = ({
  model,
  snapshot,
}: {
  model: Pick<
    OperationModel,
    "busy" | "revalidating" | "invalidateDisclosure" | "revalidateDisclosure" | "reviewLatest"
  >;
  snapshot: RevisionConflictSnapshot;
}) => {
  const p = usePresentation();
  const heading = useRef<HTMLHeadingElement>(null);
  const title = useId();
  useEffect(() => {
    heading.current?.focus();
  }, []);
  const changes = conflictDocument(snapshot);
  const metadataAllowed = snapshot.latest === null || snapshot.latest.current.history !== "NONE";
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
      {snapshot.latest === null ? null : (
        <ScreenView view={snapshot.latest.current.screen} roles={["NOTICE", "PROGRESS"]} />
      )}
      {changes === null || historyDisclosure(changes) === null || !metadataAllowed ? (
        <p>{p.text("label.changesWithheld")}</p>
      ) : (
        <ConflictPage
          key={snapshot.identity.id}
          initial={changes}
          detail={detail}
          scope={{
            target: snapshot.source.prepared.input.binding.target,
            credential: snapshot.identity.credential,
            onRefused: onDisclosureChanged,
            onDisclosureChanged: model.revalidateDisclosure,
          }}
          timeZone={
            snapshot.latest?.current.basis.kind === "SNAPSHOT"
              ? snapshot.latest.current.basis.observation.timeZone
              : "UTC"
          }
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
