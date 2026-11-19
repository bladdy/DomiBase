/**
 * Public SDK surface for phase 1: project-scoped data access only.
 * Additional modules remain in the package for later phases but are not part of the
 * supported API and are not advertised in the documentation.
 */
export { createClient } from "./client.js";
export type { DomiBaseClient } from "./client.js";
export { DomiBaseError } from "./errors.js";
export type { DomiBaseErrorShape } from "./errors.js";
export type { QueryBuilder } from "./query-builder.js";
export type {
  DomiBaseClientOptions,
  DomiBaseConfig,
  DomiBaseResponse,
  FilterValue,
  OrderOptions,
  Row
} from "./types.js";
