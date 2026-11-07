import { useEffect, useRef, useState, useId } from "react";
import type { CaseTimelineEntry, ConflictChangePage, CaseSelector } from "@contracts/client";
import {
  requestEndpoint,
  resultNotice,
  type ApiResult,
  type ServiceResponse,
} from "../../api/service";
import { isDisclosureRefused } from "../../api/outcomes";
import { TimelineEntries } from "../case/CaseTimeline";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
import type { Notice } from "../../protocol/notices";
import type { RevisionConflictSnapshot } from "../../operation/operationTypes";
import type { OperationModel } from "../../hooks/operation/useOperation";
import { LifecycleStatus } from "../LifecycleStatus";
import "./conflict.css";
type Scope = { target: CaseSelector; credential: string; onRefused: () => void };
const ConflictPage = ({
  initial,
  scope,
  detail,
  observedAt,
}: {
  initial: ConflictChangePage;
  scope: Scope;
  detail: "CURRENT" | "SUMMARY" | "ERASED";
  observedAt: string | null;
}) => {
  const p = usePresentation();
  const [items, setItems] = useState<ReadonlyArray<CaseTimelineEntry>>(initial.entries);
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
      setItems([]);
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
        const page = result.value.outcome.data;
        setItems((prior) => [...prior, ...page.items]);
        setCursor(page.nextCursor);
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
      <TimelineEntries items={items} observedAt={observedAt} detail={detail} />
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
  const onRefused = () => {
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
          key={`${snapshot.identity.id}:${detail}`}
          initial={changes.page}
          scope={{
            target: snapshot.source.prepared.input.target,
            credential: snapshot.identity.credential,
            onRefused,
          }}
          detail={detail}
          observedAt={snapshot.latest?.current.work.basis.observedAt ?? null}
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
