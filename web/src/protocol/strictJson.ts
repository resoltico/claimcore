import { requireUniqueJsonMembers } from "./jsonMemberNames";
export const isRepresentable = (content: string): boolean =>
  Array.from(content).every((character) => {
    const scalar = character.codePointAt(0)!;
    return (
      scalar !== 0 &&
      !(scalar >= 0xd800 && scalar <= 0xdfff) &&
      !(scalar >= 0xfdd0 && scalar <= 0xfdef) &&
      (scalar & 0xffff) < 0xfffe
    );
  });

export const strictJsonBytes = (bytes: Uint8Array): unknown => {
  const text = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes);
  requireUniqueJsonMembers(text);
  const value: unknown = JSON.parse(text);
  const pending: unknown[] = [value];
  while (pending.length > 0) {
    const current = pending.pop();
    if (typeof current === "string" && !isRepresentable(current)) {
      throw new Error("JSON text is not representable.");
    }
    if (Array.isArray(current)) {
      for (const item of current) {
        pending.push(item);
      }
    } else if (typeof current === "object" && current !== null) {
      for (const [key, item] of Object.entries(current)) {
        if (!isRepresentable(key)) {
          throw new Error("JSON member name is not representable.");
        }
        pending.push(item);
      }
    }
  }
  return value;
};
