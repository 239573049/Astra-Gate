import type { UiNode } from './index.js';
export const LIMITS: { nodes: number; bytes: number; state: number };
export const schema: { version: string; common: Record<string, string>; components: Record<string, { props: Record<string, string>; input?: boolean; required?: string[]; children: string }> };
/** The host normalizer's checks as errors / warnings ("path: message"). */
export function validateTree(tree: UiNode | UiNode[], o?: { actions?: string[] | null; slot?: string | null; state?: unknown }): { errors: string[]; warnings: string[]; nodes: number; bytes: number };
