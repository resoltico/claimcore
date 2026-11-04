import { caseViewsFixture, reminderViewsFixture } from "./view-counts.fixtures";
import { actorContextInput, workBasis } from "./inputs";
import { mockEndpoint } from "./endpoint-mocks";
export const stubViewCounts = () => {
  mockEndpoint("case.views").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: { outcome: { kind: "SUCCEEDED", data: caseViewsFixture } },
  });
  mockEndpoint("reminder.views").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: { outcome: { kind: "SUCCEEDED", data: reminderViewsFixture } },
  });
};
export const stubDashboardContext = () => {
  stubViewCounts();
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
          businessDate: workBasis.businessDate,
          timeZone: "Etc/UTC",
          capabilities: ["REGISTER"],
          views: ["CASES", "AUTHORITY", "OPERATIONS"],
          offers: [{ kind: "OPEN" }],
        }),
      },
    },
  });
};
