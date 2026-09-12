import { useState } from 'react'
import './App.css'

const API_BASE = 'http://localhost:7173/api'

// Simple client-side id so SaveComplaint has something to key on.
// In a real system this would come from wherever the complaint is first created.
function newComplaintId() {
  return `complaint-${Date.now()}`
}

function App() {
  const [complaintId] = useState(newComplaintId)
  const [rawText, setRawText] = useState('')
  const [redactedText, setRedactedText] = useState(null)
  const [redactLoading, setRedactLoading] = useState(false)
  const [redactError, setRedactError] = useState(null)

  const [letter, setLetter] = useState(null)
  const [wasBlocked, setWasBlocked] = useState(false)
  const [blockReason, setBlockReason] = useState(null)
  const [letterLoading, setLetterLoading] = useState(false)
  const [letterError, setLetterError] = useState(null)

  const [rating, setRating] = useState(3)
  const [handlerFeedback, setHandlerFeedback] = useState('')
  const [relevanceScore, setRelevanceScore] = useState('')
  const [saveStatus, setSaveStatus] = useState(null)
  const [saveLoading, setSaveLoading] = useState(false)
  const [saveError, setSaveError] = useState(null)

  async function handleRedact(e) {
    e.preventDefault()
    setRedactError(null)
    setRedactLoading(true)
    setRedactedText(null)
    setLetter(null)
    setWasBlocked(false)
    setBlockReason(null)
    setSaveStatus(null)

    try {
      const res = await fetch(`${API_BASE}/RedactComplaint`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ComplaintId: complaintId, ComplaintText: rawText }),
      })
      const data = await res.json()
      if (!res.ok) throw new Error(data?.Error ?? `Request failed (${res.status})`)
      // Adjust this field name if your RedactComplaint response uses a different key
      setRedactedText(data.RedactedText ?? data.redactedText ?? JSON.stringify(data))
    } catch (err) {
      setRedactError(err.message)
    } finally {
      setRedactLoading(false)
    }
  }

  async function handleGenerateLetter() {
    setLetterError(null)
    setLetterLoading(true)
    setLetter(null)
    setWasBlocked(false)
    setBlockReason(null)

    try {
      const res = await fetch(`${API_BASE}/GenerateLetter`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ComplaintId: complaintId, RedactedText: redactedText }),
      })
      const data = await res.json()
      if (!res.ok) throw new Error(data?.Error ?? `Request failed (${res.status})`)

      if (data.WasBlocked) {
  setWasBlocked(true)
  setBlockReason(data.BlockReason ?? 'Blocked by safety check')
} else {
  setLetter(data.DraftLetter ?? JSON.stringify(data))
}
    } catch (err) {
      setLetterError(err.message)
    } finally {
      setLetterLoading(false)
    }
  }

  async function handleSave(e) {
    e.preventDefault()
    setSaveError(null)
    setSaveLoading(true)
    setSaveStatus(null)

    try {
      const res = await fetch(`${API_BASE}/SaveComplaint`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          complaintId,
          finalLetter: letter,
          rating: Number(rating),
          handlerFeedback: handlerFeedback || null,
          relevanceScore: relevanceScore === '' ? null : Number(relevanceScore),
        }),
      })
      const data = await res.json()
      if (!res.ok) throw new Error(data?.Error ?? `Request failed (${res.status})`)
      setSaveStatus(`Saved (${data.Status ?? data.status ?? 'OK'}) — id: ${data.ComplaintId ?? data.complaintId}`)
    } catch (err) {
      setSaveError(err.message)
    } finally {
      setSaveLoading(false)
    }
  }

  return (
    <div className="page">
      <h1>Complaint Handler</h1>
      <p className="complaint-id">Complaint ID: <code>{complaintId}</code></p>

      <section className="step">
        <h2>1. Submit complaint</h2>
        <form onSubmit={handleRedact}>
          <textarea
            rows={6}
            value={rawText}
            onChange={(e) => setRawText(e.target.value)}
            placeholder="Paste or type the customer's complaint here..."
            required
          />
          <button type="submit" disabled={redactLoading || !rawText.trim()}>
            {redactLoading ? 'Redacting...' : 'Redact complaint'}
          </button>
        </form>
        {redactError && <p className="error">Error: {redactError}</p>}
        {redactedText && (
          <div className="result">
            <h3>Redacted text</h3>
            <pre>{redactedText}</pre>
          </div>
        )}
      </section>

      {redactedText && (
        <section className="step">
          <h2>2. Generate letter</h2>
          <button onClick={handleGenerateLetter} disabled={letterLoading}>
            {letterLoading ? 'Checking + generating...' : 'Generate letter'}
          </button>
          {letterError && <p className="error">Error: {letterError}</p>}
          {wasBlocked && (
            <div className="blocked">
              <h3>Blocked</h3>
              <p>{blockReason}</p>
            </div>
          )}
          {letter && (
            <div className="result">
              <h3>Generated letter</h3>
              <pre>{letter}</pre>
            </div>
          )}
        </section>
      )}

      {letter && (
        <section className="step">
          <h2>3. Review &amp; save</h2>
          <form onSubmit={handleSave}>
            <label>
              Rating (1-5)
              <input
                type="number"
                min={1}
                max={5}
                value={rating}
                onChange={(e) => setRating(e.target.value)}
              />
            </label>
            <label>
              Handler feedback
              <textarea
                rows={3}
                value={handlerFeedback}
                onChange={(e) => setHandlerFeedback(e.target.value)}
                placeholder="Optional notes on the generated letter"
              />
            </label>
            <label>
              Relevance score (0-1)
              <input
                type="number"
                min={0}
                max={1}
                step={0.01}
                value={relevanceScore}
                onChange={(e) => setRelevanceScore(e.target.value)}
                placeholder="Optional"
              />
            </label>
            <button type="submit" disabled={saveLoading}>
              {saveLoading ? 'Saving...' : 'Save complaint'}
            </button>
          </form>
          {saveError && <p className="error">Error: {saveError}</p>}
          {saveStatus && <p className="success">{saveStatus}</p>}
        </section>
      )}
    </div>
  )
}

export default App
