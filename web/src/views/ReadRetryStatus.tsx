import { useReadRetrying } from "../hooks/useReadRetrying";
import { useRequesterContext } from "../hooks/useRequesterContext";
import { Message } from "../presentation/Message";
/** Read admission feedback never blocks editing or acts as a mutation retry control. */
export const ReadRetryStatus = () => {
  const waiting = useReadRetrying(useRequesterContext());
  return waiting ? (
    <p className="shell-read-status" role="status">
      <Message id="ui.shell.retryingRead" />
    </p>
  ) : null;
};
