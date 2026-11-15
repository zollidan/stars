export interface SourceFeed {
  providerConnectionId: string
  providerName: string
  connectorKey: string
  sourceFeedId: string
  feedKey: string
  entityKind: string
  datasetRef: string | null
  syncMode: string
  enabled: boolean
  cursor: string | null
  lastSuccessAt: string | null
  lastRunStatus: string | null
  lastRunAt: string | null
  entityCount: number
  eventCount: number
  lastError: string | null
  hasChanges: boolean
}

export interface SourceLink {
  sourceEntityId: string
  sourceFeedId: string
  feedKey: string
  providerName: string
  displayName: string
  externalId: string | null
  firstSeenAt: string
  lastSeenAt: string
  names: string[]
}

export interface DirectoryEntry {
  masterEntityId: string
  entityKind: string
  preferredName: string
  normalizedName: string
  sportCode: string | null
  countryCode: string | null
  status: string
  createdAt: string
  names: string[]
  sources: SourceLink[]
}

export interface Evidence {
  key: string
  evidenceType: string
  weight: number | null
  explanation: string
  details: string
}

export interface ReviewItem {
  candidateId: string
  sourceEntityId: string
  sourceName: string
  sourceFeedKey: string
  providerName: string
  targetName: string
  candidateMasterEntityId: string | null
  candidateSourceEntityId: string | null
  score: number | null
  status: string
  generatedAt: string
  inputHash: string
  evidence: Evidence[]
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, {
    headers: { 'Content-Type': 'application/json', ...(init?.headers ?? {}) },
    ...init,
  })
  if (!response.ok) {
    const body = await response.text()
    throw new Error(`${response.status} ${response.statusText}: ${body}`)
  }
  return (await response.json()) as T
}

export const api = {
  sources: () => request<SourceFeed[]>('/api/sources'),
  directory: () => request<DirectoryEntry[]>('/api/directory'),
  reviews: () => request<ReviewItem[]>('/api/review'),
  syncFeed: (feedId: string) =>
    request<{ status: string; recordsRead: number; recordsChanged: number }>(
      `/api/sources/${feedId}/sync`,
      { method: 'POST' },
    ),
  syncAll: () => request<unknown[]>('/api/sync', { method: 'POST' }),
  confirm: (candidateId: string) =>
    request<{ masterEntityId: string; status: string }>(
      `/api/review/${candidateId}/confirm`,
      { method: 'POST', body: JSON.stringify({ reviewer: 'admin-ui' }) },
    ),
  reject: (candidateId: string, reason: string) =>
    request<{ candidateId: string; status: string }>(
      `/api/review/${candidateId}/reject`,
      { method: 'POST', body: JSON.stringify({ reviewer: 'admin-ui', reason }) },
    ),
  unlink: (masterId: string, sourceEntityId: string) =>
    request<unknown>(`/api/masters/${masterId}/unlink/${sourceEntityId}`, {
      method: 'POST',
      body: JSON.stringify({ reviewer: 'admin-ui', reason: 'manual unlink' }),
    }),
}

export async function waitForWorker(): Promise<void> {
  for (let attempt = 0; attempt < 40; attempt += 1) {
    await new Promise((resolve) => setTimeout(resolve, 350))
    const sources = await api.sources()
    if (sources.every((source) => !source.hasChanges)) {
      return
    }
  }
}
