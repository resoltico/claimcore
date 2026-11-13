import { useOperationObservation } from "../../hooks/useOperationObservation";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
import { ScreenView } from "../../ui/ScreenView";

/** Every disclosure comes from the current caller's operation observation. */
export const ObservedReceipt = ({ operationId }: { operationId: string }) => {
  const observation = useOperationObservation(operationId);
  const p = usePresentation();
  const notice = observation.message;
  const retryable =
    notice !== null &&
    ((notice.kind === "diagnostic" && notice.diagnostic.code === "RATE_LIMITED") ||
      (notice.kind === "local" &&
        (notice.reason === "unreachable" || notice.reason === "responseTimeout")));
  return (
    <>
      {observation.receipt === null ? null : (
        <ScreenView
          embedded
          view={observation.receipt.screen}
          timeZone={observation.receipt.basis.observation.timeZone}
        />
      )}
      {observation.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      {observation.message === null ? null : (
        <p role="alert">
          <NoticeView value={observation.message} />
        </p>
      )}
      {retryable ? (
        <button
          type="button"
          disabled={observation.loading}
          onClick={() => void observation.observe()}
        >
          {p.text("ui.tryAgain")}
        </button>
      ) : null}
    </>
  );
};
