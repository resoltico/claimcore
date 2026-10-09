import { useCallback, useState } from "react";
import type { WebV3Response } from "../api/v3";
import { v3 } from "../api/v3";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import { useRead } from "../hooks/useRead";

type Review = Extract<WebV3Response<"lifecycle.review">["outcome"], { tag: "AVAILABLE" }>["data"];

const available = (response: WebV3Response<"lifecycle.review">): Review | null =>
  response.outcome.tag === "AVAILABLE" ? response.outcome.data : null;

const privacyLabel = (phase: Review["privacyPhase"], p: ReturnType<typeof usePresentation>) => {
  switch (phase) {
    case "ERASURE_REQUESTED":
      return p.text("ui.lifecycleErasureRequested");
    case "ERASURE_PENDING":
      return p.text("ui.lifecycleErasurePending");
    case "PAYLOAD_ERASED_SUPPRESSION_RETAINED":
      return p.text("ui.lifecycleSuppressionRetained");
    case "ERASURE_FINAL":
      return p.text("ui.lifecycleErasureFinal");
    default:
      return p.text("ui.lifecycleActive");
  }
};

const LifecycleDetails = ({ value }: { value: Review }) => {
  const p = usePresentation();
  const disposition =
    value.disposition === "VOIDED_DATA_ENTRY_ERROR"
      ? p.text("ui.lifecycleVoided")
      : p.text("ui.lifecycleActive");
  return (
    <>
      <p>{p.text("ui.lifecycleDisposition", { value: disposition })}</p>
      <p>{p.text("ui.lifecyclePrivacy", { value: privacyLabel(value.privacyPhase, p) })}</p>
      {value.activeHolds.length === 0 ? (
        <p>{p.text("ui.lifecycleNoHolds")}</p>
      ) : (
        <>
          <p>{p.text("ui.lifecycleHeld")}</p>
          <ul>
            {value.activeHolds.map((hold) => (
              <li key={hold.holdId}>
                {p.text("ui.lifecycleHoldReview", { date: p.date(hold.reviewOn) })}
              </li>
            ))}
          </ul>
        </>
      )}
      {value.voidRequiresTwoApprovals ? <p>{p.text("ui.lifecycleVoidApproval")}</p> : null}
    </>
  );
};

const AuthorizedLifecycleStatus = ({
  caseReference,
  token,
  reloadSignal,
}: {
  caseReference: string;
  token: string;
  reloadSignal: number;
}) => {
  const p = usePresentation();
  const request = useCallback(
    (signal: AbortSignal) => v3.lifecycleReview(caseReference, token, signal),
    [caseReference, token],
  );
  const review = useRead(request, available, `${caseReference}:${reloadSignal}`);
  return (
    <section aria-labelledby="lifecycle-status-title">
      <h3 id="lifecycle-status-title">{p.text("ui.lifecycleStatus")}</h3>
      {review.loading ? <p role="status">{p.text("ui.loadingLifecycle")}</p> : null}
      {review.value === null && !review.loading ? (
        <p role="status">{p.text("ui.lifecycleUnavailable")}</p>
      ) : null}
      {review.message?.kind === "diagnostic" ? (
        <p className="error" role="alert">
          <NoticeView value={review.message} />
        </p>
      ) : null}
      {review.value === null ? null : <LifecycleDetails value={review.value} />}
    </section>
  );
};

export const LifecycleStatus = (props: Parameters<typeof AuthorizedLifecycleStatus>[0]) => {
  const p = usePresentation();
  const [open, setOpen] = useState(false);
  return (
    <details
      onToggle={(event) => {
        setOpen(event.currentTarget.open);
      }}
    >
      <summary>{p.text("ui.lifecycleStatus")}</summary>
      {open ? <AuthorizedLifecycleStatus {...props} /> : null}
    </details>
  );
};
