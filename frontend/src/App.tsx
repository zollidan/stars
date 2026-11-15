import { useCallback, useEffect, useState } from 'react'
import { api, waitForWorker, type DirectoryEntry, type ReviewItem, type SourceFeed } from './api'
import './styles.css'

type Tab = 'sources' | 'directory' | 'review'

const tabs: Array<{ id: Tab; label: string }> = [
  { id: 'sources', label: 'Sources' },
  { id: 'directory', label: 'Directory' },
  { id: 'review', label: 'Review' },
]

function score(value: number | null): string {
  return value === null ? '—' : `${Math.round(value * 100)}%`
}

function when(value: string | null): string {
  if (!value) return 'never'
  return new Date(value).toLocaleString()
}

function ProviderBadge({ source }: { source: SourceFeed }) {
  return (
    <span className={`badge ${source.connectorKey.startsWith('eva') ? 'eva' : 'rest'}`}>
      {source.connectorKey}
    </span>
  )
}

export default function App() {
  const [tab, setTab] = useState<Tab>('sources')
  const [sources, setSources] = useState<SourceFeed[]>([])
  const [directory, setDirectory] = useState<DirectoryEntry[]>([])
  const [reviews, setReviews] = useState<ReviewItem[]>([])
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState<string | null>(null)

  const load = useCallback(async () => {
    const [nextSources, nextDirectory, nextReviews] = await Promise.all([
      api.sources(),
      api.directory(),
      api.reviews(),
    ])
    setSources(nextSources)
    setDirectory(nextDirectory)
    setReviews(nextReviews)
  }, [])

  useEffect(() => {
    let cancelled = false
    load()
      .catch((reason: unknown) => {
        if (!cancelled) setError(String(reason))
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [load])

  async function run(key: string, action: () => Promise<unknown>) {
    setBusy(key)
    setError(null)
    try {
      await action()
      await load()
    } catch (reason) {
      setError(String(reason))
    } finally {
      setBusy(null)
    }
  }

  async function sync(feed: SourceFeed) {
    await run(`sync:${feed.sourceFeedId}`, async () => {
      await api.syncFeed(feed.sourceFeedId)
      await waitForWorker(load)
    })
  }

  async function syncAll() {
    await run('sync-all', async () => {
      await api.syncAll()
      await waitForWorker(load)
    })
  }

  async function confirm(item: ReviewItem) {
    await run(`confirm:${item.candidateId}`, () => api.confirm(item.candidateId))
  }

  async function reject(item: ReviewItem) {
    await run(`reject:${item.candidateId}`, () => api.reject(item.candidateId, 'manual rejection'))
  }

  async function unlink(master: DirectoryEntry, sourceId: string) {
    await run(`unlink:${sourceId}`, () => api.unlink(master.masterEntityId, sourceId))
  }

  return (
    <div className="shell">
      <header className="topbar">
        <div>
          <p className="eyebrow">STARS · source registry</p>
          <h1>One team, many providers</h1>
          <p className="lede">
            No silent merges. Every link has an audit trail, a score and evidence.
          </p>
        </div>
        <div className="actions">
          <button className="ghost" onClick={() => run('refresh', load)} disabled={busy !== null}>
            Refresh
          </button>
          <button className="primary" onClick={syncAll} disabled={busy !== null}>
            {busy === 'sync-all' ? 'Syncing…' : 'Sync all'}
          </button>
        </div>
      </header>

      {error && (
        <div className="error" role="alert">
          <strong>Request failed.</strong> {error}
        </div>
      )}

      <section className="stats">
        <div className="stat">
          <span>Providers</span>
          <strong>{new Set(sources.map((source) => source.providerName)).size}</strong>
        </div>
        <div className="stat">
          <span>Canonical teams</span>
          <strong>{directory.length}</strong>
        </div>
        <div className="stat">
          <span>Source records</span>
          <strong>{directory.reduce((total, master) => total + master.sources.length, 0)}</strong>
        </div>
        <div className="stat accent">
          <span>Waiting for review</span>
          <strong>{reviews.length}</strong>
        </div>
      </section>

      <nav className="tabs">
        {tabs.map((item) => (
          <button
            key={item.id}
            className={tab === item.id ? 'active' : ''}
            onClick={() => setTab(item.id)}
          >
            {item.label}
            {item.id === 'review' && reviews.length > 0 && <em>{reviews.length}</em>}
          </button>
        ))}
      </nav>

      {loading ? (
        <p className="empty">Loading registry…</p>
      ) : tab === 'sources' ? (
        <section className="panel">
          <div className="panel-head">
            <h2>Sources</h2>
            <p>Snapshots are bounded and replay-safe. Select a feed to import manually.</p>
          </div>
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Provider</th>
                  <th>Feed</th>
                  <th>Entities</th>
                  <th>Events</th>
                  <th>Last run</th>
                  <th>Cursor</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {sources.map((source) => (
                  <tr key={source.sourceFeedId}>
                    <td>
                      <div className="provider">
                        <strong>{source.providerName}</strong>
                        <ProviderBadge source={source} />
                      </div>
                      {source.lastError && <p className="inline-error">{source.lastError}</p>}
                    </td>
                    <td>
                      <code>{source.feedKey}</code>
                      <small>{source.entityKind}{source.datasetRef ? ` · ${source.datasetRef}` : ''}</small>
                    </td>
                    <td>{source.entityCount}</td>
                    <td>{source.eventCount}</td>
                    <td>
                      <span className={`status ${source.lastRunStatus ?? 'idle'}`}>
                        {source.lastRunStatus ?? 'idle'}
                      </span>
                      <small>{when(source.lastRunAt)}</small>
                    </td>
                    <td><code className="cursor">{source.cursor ?? '—'}</code></td>
                    <td className="right">
                      <button
                        className="small"
                        onClick={() => sync(source)}
                        disabled={busy !== null || source.hasChanges}
                      >
                        {busy === `sync:${source.sourceFeedId}`
                          ? 'Syncing…'
                          : source.hasChanges
                            ? 'Matching…'
                            : 'Sync'}
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      ) : tab === 'directory' ? (
        <section className="grid">
          {directory.length === 0 && <p className="empty">No canonical entities yet. Run a sync.</p>}
          {directory.map((master) => (
            <article className="card" key={master.masterEntityId}>
              <header>
                <div>
                  <p className="eyebrow">{master.entityKind}{master.countryCode ? ` · ${master.countryCode}` : ''}</p>
                  <h3>{master.preferredName}</h3>
                  <code>{master.normalizedName}</code>
                </div>
                <span className="count">{master.sources.length} sources</span>
              </header>
              <div className="names">
                {master.names.map((name) => <span key={name}>{name}</span>)}
              </div>
              <ul className="links">
                {master.sources.map((source) => (
                  <li key={source.sourceEntityId}>
                    <div>
                      <strong>{source.providerName}</strong>
                      <span>{source.displayName}</span>
                      {source.externalId && <code>#{source.externalId}</code>}
                      {source.names.length > 1 && <small>also: {source.names.slice(1).join(', ')}</small>}
                    </div>
                    <button
                      className="link danger"
                      onClick={() => unlink(master, source.sourceEntityId)}
                      disabled={busy !== null}
                    >
                      unlink
                    </button>
                  </li>
                ))}
              </ul>
            </article>
          ))}
        </section>
      ) : (
        <section className="review-list">
          {reviews.length === 0 && <p className="empty">Nothing waiting. Ambiguous teams stay out of the directory.</p>}
          {reviews.map((item) => (
            <article className="card review" key={item.candidateId}>
              <header>
                <div>
                  <p className="eyebrow">{item.providerName} · {item.sourceFeedKey}</p>
                  <h3>{item.sourceName} <span className="arrow">→</span> {item.targetName}</h3>
                </div>
                <span className="score">{score(item.score)}</span>
              </header>
              <details>
                <summary>Evidence</summary>
                <ul className="evidence">
                  {item.evidence.map((evidence) => (
                    <li key={evidence.key}>
                      <div>
                        <strong>{evidence.evidenceType}</strong>
                        <p>{evidence.explanation}</p>
                      </div>
                      <code>{score(evidence.weight)}</code>
                    </li>
                  ))}
                </ul>
              </details>
              <footer>
                <button
                  className="ghost"
                  onClick={() => reject(item)}
                  disabled={busy !== null}
                >
                  Reject
                </button>
                <button
                  className="primary"
                  onClick={() => confirm(item)}
                  disabled={busy !== null}
                >
                  Confirm link
                </button>
              </footer>
            </article>
          ))}
        </section>
      )}
    </div>
  )
}
