import { actorContextInput } from "./inputs";
import { mockEndpoint } from "./endpoint-mocks";
export const stubDashboardContext = () => {
  // Most dashboard scenarios control another pane. Keep the separate overview explicitly unknown.
  mockEndpoint("work.overview").mockResolvedValue({
    kind: "deliveryFailure",
    notice: { kind: "local", reason: "unreachable" },
    dispatched: false,
  });
  return mockEndpoint("context").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      outcome: {
        kind: "SUCCEEDED",
        data: actorContextInput({
          productVersion: "0.10.0",
          businessDate: "2026-09-09",
          timeZone: "Etc/UTC",
          capabilities: ["REGISTER"],
          views: ["CASES", "AUTHORITY", "OPERATIONS"],
          offers: [{ kind: "OPEN" }],
        }),
      },
    },
  });
};
