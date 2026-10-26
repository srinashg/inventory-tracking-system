import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { api, ApiError, errorMessage, onUnauthorized } from '../api/client'
import { AuthContext, type AuthContextValue, type AuthState } from './authState'

/**
 * Holds who is signed in. The session itself is an HttpOnly cookie managed by the browser; here we only keep the user's
 * name, roles and permissions that the server reports, in memory. Nothing is written to localStorage or sessionStorage.
 */
export function AuthProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<AuthState>({ status: 'loading' })
  const statusRef = useRef(state.status)
  useEffect(() => {
    statusRef.current = state.status
  }, [state.status])

  /** Asks the server who is signed in. */
  const load = useCallback(() => {
    api
      .me()
      .then((user) => setState({ status: 'authenticated', user }))
      .catch((failure: unknown) => {
        if (failure instanceof ApiError && failure.status === 401) {
          setState((current) => ({ status: 'anonymous', notice: current.notice }))
        } else {
          setState({ status: 'error', error: errorMessage(failure) })
        }
      })
  }, [])

  useEffect(load, [load]) // check once when the app starts

  const retry = useCallback(() => {
    setState({ status: 'loading' })
    load()
  }, [load])

  // Any request the server refuses with 401 while we think someone is signed in means the session has ended.
  useEffect(
    () =>
      onUnauthorized(() => {
        if (statusRef.current === 'authenticated') {
          setState({ status: 'anonymous', notice: 'Your session has ended. Please sign in again.' })
        }
      }),
    [],
  )

  const login = useCallback(async (email: string, password: string) => {
    const user = await api.login(email, password) // throws the server's message if the credentials are refused
    setState({ status: 'authenticated', user })
  }, [])

  const logout = useCallback(async () => {
    try {
      await api.logout()
    } catch {
      // Whether or not the server could be told, this browser is signed out as far as the screen goes.
    }
    setState({ status: 'anonymous' })
  }, [])

  const value = useMemo<AuthContextValue>(
    () => ({ ...state, login, logout, retry, can: (permission) => state.user?.permissions.includes(permission) ?? false }),
    [state, login, logout, retry],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}
