import type { Notice } from "../api/notices";
import { usePresentation } from "./context";
import type { MessageArgs, MessageKey, Values } from "./types";

type Props<K extends MessageKey> = { id: K } & (MessageArgs[K] extends Readonly<
  Record<string, never>
>
  ? { values?: never }
  : { values: MessageArgs[K] });
export const Message = <K extends MessageKey>({ id, values }: Props<K>) => {
  const p = usePresentation();
  // The prop union is checked at call sites; rendering uses the same catalog shape validator.
  return <>{p.text(id, ...([values] as Values<K>))}</>;
};
export const NoticeView = ({ value }: { value: Notice }) => {
  const p = usePresentation();
  return <>{p.notice(value)}</>;
};
