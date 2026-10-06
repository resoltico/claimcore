import { cpSync, mkdirSync } from "node:fs";
import { join } from "node:path";
import { verifyPublished } from "../publish/main.mjs";

/** One Linux publication supplies every image in this experiment. @param {import("./browser-trust-qualification.mjs").Docker} docker @param {string} state @param {string | undefined} supplied */
export function publicationContext(docker, state, supplied) {
  const context = join(state, "publication-context");
  mkdirSync(context, { mode: 0o700 });
  if (supplied === undefined) {
    docker([
      "build",
      "--file",
      "deployment/Dockerfile",
      "--target",
      "publication-export",
      "--output",
      `type=local,dest=${context}`,
      ".",
    ]);
  } else {
    verifyPublished(supplied);
    cpSync(supplied, join(context, "published"), { recursive: true });
  }
  verifyPublished(join(context, "published"));
  return context;
}
