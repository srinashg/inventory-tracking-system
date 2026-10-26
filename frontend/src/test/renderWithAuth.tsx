import { render } from '@testing-library/react'
import type { ReactElement } from 'react'
import { AuthContext, type AuthContextValue } from '../auth/authState'
import type { Permission } from '../types'

export const ALL_PERMISSIONS: Permission[] = ['ManageProducts', 'ManageStock', 'ManageOrders', 'FulfillOrders']

/** Renders a page as a signed-in user who holds exactly the given permissions (all of them by default). */
export function renderWithAuth(ui: ReactElement, permissions: Permission[] = ALL_PERMISSIONS) {
  const value: AuthContextValue = {
    status: 'authenticated',
    user: { userId: 1, email: 'tester@example.com', displayName: 'Tester', roles: ['Tester'], permissions },
    login: async () => {},
    logout: async () => {},
    retry: () => {},
    can: (permission) => permissions.includes(permission),
  }
  return render(<AuthContext.Provider value={value}>{ui}</AuthContext.Provider>)
}
