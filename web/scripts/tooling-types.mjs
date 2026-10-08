// Boundary models for generated artifacts and sanitized verification reports.
/** @typedef {{ path: string, bytes: number, sha256: string }} ContractFile */
/** @typedef {{ endpoint: string, exportName: string, responseDefinition?: string }} ValidatorEndpoint */
/** @typedef {{ schemaVersion: string, framework: {name: string, version: string}, thresholds: {high: number, low: number, break: number}, config: Record<string, unknown>, files: Record<string, {source: string, mutants: {status: string, statusReason?: string}[]}>, testFiles: Record<string, unknown> }} MutationReport */
/** @typedef {{ passed: number, failed: number, skipped: number, timedOut: number, interrupted: number }} BrowserTotals */
/** @typedef {{ id: string, outcome: string | undefined, durationMs: number }} TestSummary */
/** @typedef {Record<string, any>} JsonRecord Parsed external/generated JSON whose consumed fields are checked by its caller. */
/** @typedef {{version?: string, name?: string, dependencies?: Record<string, string>, license?: string}} LockedPackage */
/** @typedef {{packages: Record<string, LockedPackage>}} PackageLock */
/** @typedef {{ "bom-ref": string, purl: string, name: string, licenses?: {license?: {id?: string}}[] }} BomComponent */
/** @typedef {{ ref: string, dependsOn?: string[] }} BomDependency */
/** @typedef {{ metadata?: { component?: BomComponent }, components: BomComponent[], dependencies: BomDependency[] }} BomDocument */
/** @typedef {{host: ValidatorEndpoint[], discovery: ValidatorEndpoint[], core: ValidatorEndpoint[], recovery: ValidatorEndpoint[]}} ValidatorGroups */
export {};
