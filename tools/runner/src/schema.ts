// Runtime validation of source data against JSON Schemas generated from contract/host-api.d.ts.
import { existsSync, readFileSync } from "node:fs";
import { Ajv, type ValidateFunction } from "ajv";
import { paths } from "./paths.ts";
import { UsageError } from "./errors.ts";

export type ContractType =
  | "SourceManifest"
  | "SearchQuery"
  | "SearchPage"
  | "Gallery"
  | "PageImage"
  | "RetryHint"
  | "TagSuggestion[]";

/** Which contract type each source function returns. */
export const RESULT_TYPES: Record<string, ContractType> = {
  search: "SearchPage",
  getGallery: "Gallery",
  getPageUrl: "PageImage",
  suggestTags: "TagSuggestion[]",
};

let ajv: Ajv | null = null;
const compiled = new Map<ContractType, ValidateFunction>();

function instance(): Ajv {
  if (ajv) return ajv;
  if (!existsSync(paths.schema)) {
    throw new UsageError(`contract schema not found at ${paths.schema} — run \`npm run schema\``);
  }
  ajv = new Ajv({ allErrors: true, strict: false });
  ajv.addSchema(JSON.parse(readFileSync(paths.schema, "utf8")), "contract");
  return ajv;
}

function validator(type: ContractType): ValidateFunction {
  let fn = compiled.get(type);
  if (!fn) {
    const schema = type.endsWith("[]")
      ? { type: "array", items: { $ref: `contract#/definitions/${type.slice(0, -2)}` } }
      : { $ref: `contract#/definitions/${type}` };
    fn = instance().compile(schema);
    compiled.set(type, fn);
  }
  return fn;
}

/** Returns violations as `<field path>: <message>`; empty when valid (FR-019). */
export function validate(type: ContractType, value: unknown): string[] {
  const fn = validator(type);
  if (fn(value)) return [];
  return (fn.errors ?? []).map((e) => {
    const path = e.instancePath ? e.instancePath.slice(1).replace(/\//g, ".") : "(root)";
    const extra =
      e.keyword === "additionalProperties"
        ? ` '${(e.params as { additionalProperty: string }).additionalProperty}'`
        : e.keyword === "enum"
          ? ` ${JSON.stringify((e.params as { allowedValues: unknown[] }).allowedValues)}`
          : "";
    return `${path}: ${e.message}${extra}`;
  });
}
