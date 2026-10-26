import type { OrderStatus } from '../types'

/** A coloured label for an order's status. */
export function StatusBadge({ status }: { status: OrderStatus }) {
  return <span className={`badge badge--${status.toLowerCase()}`}>{status}</span>
}
