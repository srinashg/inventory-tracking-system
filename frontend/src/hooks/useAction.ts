import { useCallback, useEffect, useRef, useState } from 'react'
import { errorMessage, fieldErrors as fieldErrorsOf } from '../api/client'

/**
 * Wraps a user action that calls the API (create, submit, ship, ...).
 * - `pending` is true while the request is in flight, so buttons can be disabled.
 * - A second call while one is in flight is ignored, which prevents duplicate submissions.
 * - On failure, `error` holds the server's message and `fieldErrors` the per-field problems; `run` returns undefined.
 */
export function useAction<Args extends unknown[], Result>(action: (...args: Args) => Promise<Result>) {
  const [pending, setPending] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})

  const inFlight = useRef(false) // a ref, because state updates are not visible until the next render
  const latestAction = useRef(action)
  useEffect(() => {
    latestAction.current = action // always call the newest version of the action
  })

  const run = useCallback(async (...args: Args): Promise<Result | undefined> => {
    if (inFlight.current) {
      return undefined
    }

    inFlight.current = true
    setPending(true)
    setError(null)
    setFieldErrors({})

    try {
      return await latestAction.current(...args)
    } catch (failure) {
      setError(errorMessage(failure))
      setFieldErrors(fieldErrorsOf(failure))
      return undefined
    } finally {
      inFlight.current = false
      setPending(false)
    }
  }, [])

  const clearError = useCallback(() => {
    setError(null)
    setFieldErrors({})
  }, [])

  return { run, pending, error, fieldErrors, clearError }
}
