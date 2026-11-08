import type { PresentedMessage } from "@contracts/client";
import { PresentedValue } from "../components/PresentedValue";
import { usePresentation } from "../presentation/context";
import { hasMessage, renderIsolatedValues } from "../presentation/messages";

/** Catalog word order with server-supplied typed arguments, independent of business fields. */
export const SuppliedMessage = ({
  message,
  timeZone,
}: {
  message: PresentedMessage;
  timeZone: string;
}) => {
  const p = usePresentation();
  if (!hasMessage(message.messageId)) {
    return p.text("notice.unknownDiagnostic");
  }
  const values = Object.fromEntries(message.arguments.map((argument) => [argument.key, ""]));
  const references = Object.fromEntries(
    message.arguments.map((argument) => [
      argument.key,
      <PresentedValue key={argument.key} value={argument.value} timeZone={timeZone} />,
    ]),
  );
  return renderIsolatedValues(p, message.messageId, values, references);
};
