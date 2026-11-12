import type { PresentedMessage } from "@contracts/client";
import { diagnosticMessageKeys, diagnosticGuiMessageKeys } from "@contracts/messages";
import { PresentedValue } from "../components/PresentedValue";
import { usePresentation } from "../presentation/context";
import { hasMessage, renderIsolatedValues } from "../presentation/messages";

const primaryMessages: Readonly<Record<string, string>> = diagnosticMessageKeys;
const inputVariants: Readonly<Record<string, string>> = Object.fromEntries(
  Object.entries(diagnosticGuiMessageKeys).flatMap(([code, variant]) => {
    const primary = primaryMessages[code];
    return primary === undefined ? [] : [[primary, variant]];
  }),
);

/** Catalog word order with server-supplied typed arguments, independent of business fields. */
export const SuppliedMessage = ({
  message,
  timeZone,
  inputIssue = false,
}: {
  message: PresentedMessage;
  timeZone: string;
  inputIssue?: boolean;
}) => {
  const p = usePresentation();
  const identity = inputIssue
    ? (inputVariants[message.messageId] ?? message.messageId)
    : message.messageId;
  if (!hasMessage(identity)) {
    return p.text("notice.unknownDiagnostic");
  }
  const values = Object.fromEntries(message.arguments.map((argument) => [argument.key, ""]));
  const references = Object.fromEntries(
    message.arguments.map((argument) => [
      argument.key,
      <PresentedValue key={argument.key} value={argument.value} timeZone={timeZone} />,
    ]),
  );
  return renderIsolatedValues(p, identity, values, references);
};
