import { useOperationObservation } from "../../hooks/useOperationObservation";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
import { ScreenView } from "../../ui/ScreenView";

/** Every disclosure comes from the current caller's operation observation. */
export const ObservedReceipt = ({ operationId }: { operationId: string }) => {
  const observation = useOperationObservation(operationId);
  const p = usePresentation();
  return (
    <>
      {observation.receipt === null ? null : (
        <ScreenView
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
    </>
  );
};
