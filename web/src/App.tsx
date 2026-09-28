import { useEffect, useState } from 'react'

type Version = { name: string; version: string }

// Временная стартовая страница: проверяет связь с control plane.
// Полноценный интерфейс — неделя 7 календарного плана.
export default function App() {
  const [backend, setBackend] = useState<Version | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    fetch('/api/version')
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error(`HTTP ${r.status}`))))
      .then(setBackend)
      .catch((e: Error) => setError(e.message))
  }, [])

  return (
    <main>
      <h1>Infrastructure Control Center</h1>
      <p>
        Control plane:{' '}
        {backend ? `${backend.name} ${backend.version}` : error ? `недоступен (${error})` : 'проверка…'}
      </p>
    </main>
  )
}
