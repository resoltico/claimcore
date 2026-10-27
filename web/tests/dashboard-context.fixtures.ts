import { vi } from "vitest";
import { service } from "../src/api/service";
export const stubDashboardContext = () =>
  vi.spyOn(service, "context").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      outcome: {
        kind: "SUCCEEDED",
        data: {
          productVersion: "0.10.0",
          businessDate: "2026-09-09",
          timeZone: "Etc/UTC",
          capabilities: ["REGISTER"],
          views: ["CASES", "AUTHORITY", "OPERATIONS"],
          offers: [{ kind: "OPEN" }],
        },
      },
    },
  });
