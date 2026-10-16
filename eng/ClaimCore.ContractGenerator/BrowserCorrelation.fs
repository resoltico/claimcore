namespace ClaimCore.ContractGeneration

open System.Text.Json
open ClaimCore.ClientProtocol

module BrowserCorrelation =
    let private json value =
        JsonSerializer.Serialize(
            value,
            JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
        )

    let source =
        "const correlationRules = "
        + json CorrelationPolicy.rules
        + " as const;\n"
        + "const correlationSelectors = "
        + json CorrelationPolicy.selectors
        + " as const;\n"
        + "const correlationTagPath = "
        + json CorrelationPolicy.tagPath
        + " as const;\n"
        + """
function correlationPath(value: unknown, path: ReadonlyArray<string>): unknown {
  let current = value;
  for (const part of path) {
    if (!isObject(current) || !(part in current)) return undefined;
    current = current[part];
  }
  return current;
}
function sameReference(left: unknown, right: unknown): boolean {
  const fold = (value: string) => value.replace(/[A-Z]/g, character => String.fromCharCode(character.charCodeAt(0) + 32));
  return typeof left === 'string' && typeof right === 'string' && fold(left) === fold(right);
}
export function correlateReply(endpoint: keyof RequestByEndpoint, request: unknown, response: unknown): boolean {
  const definition = endpoints.find(item => item.id === endpoint);
  if (definition === undefined) return false;
  const tag = correlationPath(response, correlationTagPath);
  return correlationRules.every(rule => {
    const selector = correlationSelectors.find(item => item.name === rule.selector);
    if (selector === undefined) throw new Error('Unsupported correlation selector');
    const scopes: ReadonlyArray<string> = selector.receiptScopes;
    const tags: ReadonlyArray<string> = rule.tags;
    const selected = (scopes.length === 0 || scopes.includes(definition.receiptScope))
      && selector.requestPresent.every(path => correlationPath(request, path) !== undefined)
      && selector.requestAbsent.every(path => correlationPath(request, path) === undefined)
      && (tags.length === 0 || (typeof tag === 'string' && tags.includes(tag)));
    if (!selected) return true;
    const left = correlationPath(response, rule.replyPath);
    const right = correlationPath(request, rule.requestPath);
    switch (rule.comparison) {
      case 'EXACT': return typeof left === 'string' && typeof right === 'string' && left === right;
      case 'RECEIPT_SCOPE': return left === definition.receiptScope;
      case 'REFERENCE': return sameReference(left, right);
      case 'SUCCESSOR': return typeof left === 'number' && typeof right === 'number' && Number.isSafeInteger(right + 1) && left === right + 1;
    }
  });
}
"""
