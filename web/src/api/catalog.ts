import { strictByteProfile } from "@contracts/client";
import { admitBytes } from "../protocol/schema";
import { getCatalogHttp } from "./transport";
import { responseBytes } from "./responseBytes";
import { withinResponseDeadline } from "./responseDeadline";

export const readCatalog = async (language: string): Promise<unknown> => {
  const result = await withinResponseDeadline(async (signal) => {
    const response = await getCatalogHttp(language, signal);
    if (
      !response.ok ||
      response.headers.get("content-type")?.split(";", 1)[0]?.trim() !== "application/json"
    ) {
      void response.body?.cancel().catch(() => undefined);
      throw new Error("Language catalog unavailable");
    }
    const value = admitBytes(
      await responseBytes(response, strictByteProfile.maximumResponseBytes, signal),
    );
    return { kind: "outcome", value, status: response.status };
  });
  if (result.kind !== "outcome") {
    throw new Error("Language catalog unavailable");
  }
  return result.value;
};
