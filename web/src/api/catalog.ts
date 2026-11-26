import { strictByteProfile } from "@contracts/client";
import { admitBytes } from "../protocol/schema";
import { getCatalogHttp } from "./transport";
import { responseBytes } from "./responseBytes";
import { withinResponseDeadline } from "./responseDeadline";

let paths: Readonly<Record<string, string>> = {};

export const installCatalogAssets = (assets: Readonly<Record<string, string>>): void => {
  paths = assets;
};

export const readCatalog = async (language: string): Promise<unknown> => {
  const path = paths[language];
  if (path === undefined) {
    throw new Error("Language is absent from this build");
  }
  const result = await withinResponseDeadline(async (signal) => {
    const response = await getCatalogHttp(path, signal);
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
