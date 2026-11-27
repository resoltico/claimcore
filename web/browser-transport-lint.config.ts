import type { OxlintConfig } from "oxlint";

type Override = NonNullable<OxlintConfig["overrides"]>[number];

const transportPath = "src/api/transport.ts";
const displayLifecyclePath = "src/hooks/EndpointProvider.tsx";
const transportGlobals = ["fetch", "XMLHttpRequest", "WebSocket", "EventSource"].map((name) => ({
  name,
  message: "Use the validated browser API boundary in src/api/transport.ts.",
}));
const presentationGlobals = ["Intl", "localStorage", "sessionStorage", "navigator"].map((name) => ({
  name,
  message: "Operation state and transport must remain presentation-independent.",
}));

const restrictedGlobals = (globals: typeof transportGlobals): NonNullable<Override["rules"]> => ({
  "no-restricted-globals": [
    "error",
    {
      globals,
      checkGlobalObject: true,
    },
  ],
});

export const transportImport = {
  group: ["**/api/transport", "**/api/transport.ts"],
  message: "Use an owned service, catalog, attachment or CSV adapter.",
};

export const siblingTransportImport = {
  ...transportImport,
  group: ["./transport", "./transport.ts"],
};

export const transportAdapters = [
  "src/api/service.ts",
  "src/api/catalog.ts",
  "src/api/attachments.ts",
  "src/api/csvExport.ts",
];

export const coordinatorFiles = [
  "src/api/**",
  "src/hooks/**",
  "src/operation/**",
  "src/features/people/usePeople*.ts",
];

export const browserTransportPolicy: Override[] = [
  {
    files: ["src/**/*.ts", "src/**/*.tsx"],
    excludeFiles: [transportPath],
    rules: restrictedGlobals(transportGlobals),
  },
  {
    files: coordinatorFiles,
    excludeFiles: [transportPath, displayLifecyclePath],
    rules: restrictedGlobals([...transportGlobals, ...presentationGlobals]),
  },
  {
    files: [displayLifecyclePath],
    // The display lifecycle observes browser connectivity; operation state and transport do not.
    rules: restrictedGlobals([
      ...transportGlobals,
      ...presentationGlobals.filter(({ name }) => name !== "navigator"),
    ]),
  },
  {
    files: [transportPath],
    rules: restrictedGlobals(presentationGlobals),
  },
];
