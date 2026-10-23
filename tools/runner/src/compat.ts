// Contract version compatibility (FR-002): a host supporting H.h.p accepts a source requiring M.m
// iff M == H and m <= h.
import { readFileSync } from "node:fs";
import { paths } from "./paths.ts";

export const HOST_CONTRACT_VERSION: string = JSON.parse(readFileSync(paths.contractPackage, "utf8")).version;

export function isCompatible(hostVersion: string, required: string): boolean {
  const [hMajor, hMinor] = hostVersion.split(".").map(Number);
  const m = /^(\d+)\.(\d+)$/.exec(required);
  if (!m) return false;
  return Number(m[1]) === hMajor && Number(m[2]) <= hMinor;
}

export function compatibilityError(required: unknown): string | null {
  if (typeof required !== "string" || !/^\d+\.\d+$/.test(required)) {
    return `manifest.contract must be "MAJOR.MINOR", got ${JSON.stringify(required)}`;
  }
  if (isCompatible(HOST_CONTRACT_VERSION, required)) return null;
  const [major, minor] = HOST_CONTRACT_VERSION.split(".");
  return `source requires contract ${required}, host supports ${major}.${minor}`;
}
