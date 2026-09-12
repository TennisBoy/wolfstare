// Mirrors Wolfstare.Contracts. Kept small and hand-written; the surface is stable.

export type RuleKind =
  | "domain"
  | "app.imageName"
  | "app.publisher"
  | "app.fileDescription";

export interface RuleDto {
  kind: RuleKind;
  value: string;
}

export interface BlockListDto {
  id: string;
  name: string;
  rules: RuleDto[];
  allowlist: RuleDto[];
}

export type LockKind = "none" | "password" | "timed";

export interface ActiveSessionDto {
  id: string;
  blockListId: string;
  blockListName: string;
  lockKind: LockKind;
  remainingSeconds: number | null;
  elapsedSeconds: number;
  canBeStopped: boolean;
}

export interface StatusDto {
  activeSessions: ActiveSessionDto[];
  health: string;
}
