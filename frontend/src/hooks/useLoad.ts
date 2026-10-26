import { useCallback, useEffect, useState, type DependencyList } from 'react'
import { errorMessage } from '../api/client'

interface LoadState<T> {
  data?: T
  loading: boolean
  error: string | null
}

/**
 * Loads data from the API when the component appears and whenever `deps` change.
 * Gives the page what it needs to show a loading indicator, an error message, or the data.
 * The previous data stays visible while a reload is in progress.
 */
export function useLoad<T>(loader: () => Promise<T>, deps: DependencyList) {
  const [state, setState] = useState<LoadState<T>>({ loading: true, error: null })
  const [version, setVersion] = useState(0)

  useEffect(() => {
    let cancelled = false // ignore the answer if the page moved on before it arrived
    setState((current) => ({ ...current, loading: true, error: null }))

    loader()
      .then((data) => {
        if (!cancelled) setState({ data, loading: false, error: null })
      })
      .catch((error: unknown) => {
        if (!cancelled) setState({ loading: false, error: errorMessage(error) })
      })

    return () => {
      cancelled = true
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the caller lists the real dependencies
  }, [...deps, version])

  const reload = useCallback(() => setVersion((v) => v + 1), [])

  return { ...state, reload }
}
