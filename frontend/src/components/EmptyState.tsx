import type { ReactNode } from 'react'

/** Shown instead of a table when there is nothing to list yet. */
export function EmptyState({ children }: { children: ReactNode }) {
  return <p className="empty-state">{children}</p>
}
