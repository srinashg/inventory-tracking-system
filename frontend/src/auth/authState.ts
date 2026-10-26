import { createContext, useContext } from 'react'
import type { CurrentUser, Permission } from '../types'

export type AuthStatus = 'loading' | 'anonymous' | 'authenticated' | 'error'

export interface AuthState {
  status: AuthStatus
  user?: CurrentUser
  /** Why the visitor is signed out, for example "Your session has ended". */
  notice?: string
  /** The server message when the session could not be checked at all (status "error"). */
  error?: string
}

export interface AuthContextValue extends AuthState {
  login: (email: string, password: string) => Promise<void>
  logout: () => Promise<void>
  /** Asks the server again who is signed in (used by the retry button after a network error). */
  retry: () => void
  /** True when the signed-in user holds the permission. This only decides what to SHOW; the server enforces it. */
  can: (permission: Permission) => boolean
}

export const AuthContext = createContext<AuthContextValue | null>(null)

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext)
  if (!context) {
    throw new Error('useAuth must be used inside an AuthProvider')
  }
  return context
}
