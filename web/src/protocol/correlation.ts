import { correlationPolicy, type EndpointId } from "@contracts/client";
const object = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);
const at = (value: unknown, path: ReadonlyArray<string>): unknown =>
  path.reduce<unknown>((current, name) => (object(current) ? current[name] : undefined), value);
const fold = (value: unknown): unknown =>
  typeof value === "string" && /^[!-~]{1,80}$/u.test(value)
    ? value.replace(/[A-Z]/gu, (character) => String.fromCharCode(character.charCodeAt(0) + 32))
    : undefined;
const compare = (comparison: string, expected: unknown, actual: unknown): boolean => {
  if (expected === undefined || actual === undefined) {
    return false;
  }
  if (comparison === "EXACT") {
    return expected === actual;
  }
  if (comparison === "ASCII_REFERENCE") {
    return fold(expected) !== undefined && fold(expected) === fold(actual);
  }
  return (
    comparison === "SUCCESSOR" &&
    typeof expected === "number" &&
    expected >= 0 &&
    Number.isSafeInteger(expected + 1) &&
    actual === expected + 1
  );
};
export const correlateReply = (
  endpoint: EndpointId,
  request: unknown,
  response: unknown,
): boolean => {
  if (at(response, ["endpoint"]) !== endpoint) {
    return false;
  }
  const rule = correlationPolicy.find(
    (item) => item.endpoint === endpoint && item.outcome === at(response, ["outcome", "tag"]),
  );
  return (
    rule !== undefined &&
    (rule.scope === null || at(response, ["outcome", "data", "scope"]) === rule.scope) &&
    rule.bindings.every((binding) =>
      compare(binding.comparison, at(request, binding.request), at(response, binding.reply)),
    )
  );
};
