// Boundary models for generated contract artifacts.
/** @typedef {{ path: string, bytes: number, sha256: string }} ContractFile */
/** @typedef {{ endpoint: string, exportName: string, responseDefinition?: string }} ValidatorEndpoint */
/** @typedef {Record<string, any>} JsonRecord Parsed external/generated JSON whose consumed fields are checked by its caller. */
/** @typedef {{version?: string, name?: string, dependencies?: Record<string, string>, license?: string}} LockedPackage */
/** @typedef {{packages: Record<string, LockedPackage>}} PackageLock */
/** @typedef {{host: ValidatorEndpoint[], discovery: ValidatorEndpoint[], core: ValidatorEndpoint[], authority: ValidatorEndpoint[]}} ValidatorGroups */
export {};
