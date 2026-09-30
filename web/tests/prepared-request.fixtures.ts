/** Echoes a synthetic preparation's identity from the request the editor actually sent. */
const record = (value: unknown): Record<string, unknown> => {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new Error("Synthetic prepared fixture is invalid.");
  }
  return value as Record<string, unknown>;
};

const requestBody = (init?: RequestInit): Record<string, unknown> => {
  if (typeof init?.body !== "string") {
    throw new Error("Synthetic draft body is missing.");
  }
  return record(JSON.parse(init.body));
};

export const preparedForRequest =
  (reply: Response) =>
  async (_input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const request = requestBody(init);
    const response = record(await reply.json());
    const outcome = record(response["outcome"]);
    if (outcome["tag"] !== "PREPARED" || typeof request["operationId"] !== "string") {
      throw new Error("Synthetic prepared fixture has no exact identity.");
    }
    const summary = record(record(record(outcome["data"])["details"])["summary"]);
    summary["operationId"] = request["operationId"];
    return new Response(JSON.stringify(response), {
      status: reply.status,
      headers: { "content-type": "application/json" },
    });
  };
