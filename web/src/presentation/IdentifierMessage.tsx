import { IdentifierValue } from "../components/IdentifierValue";
import { usePresentation } from "./context";
import { renderIsolatedValue } from "./messages";
import type { MessageArgs, MessageKey } from "./types";
type Argument = "operationId" | "digest";
type IdentifierKey<A extends Argument> = {
  [K in MessageKey]: MessageArgs[K] extends Readonly<Record<A, string>> ? K : never;
}[MessageKey];
type Props<A extends Argument, K extends IdentifierKey<A>> = {
  id: K;
  argument: A;
  values: MessageArgs[K] & Readonly<Record<A, string>>;
};
export const IdentifierMessage = <A extends Argument, K extends IdentifierKey<A>>({
  id,
  argument,
  values,
}: Props<A, K>) => {
  const p = usePresentation();
  return renderIsolatedValue(
    p,
    id,
    values,
    <IdentifierValue key={argument} value={values[argument]} />,
    argument,
  );
};
