const money = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' })

/** 1299.99 -> "$1,299.99" */
export function formatMoney(amount: number): string {
  return money.format(amount)
}

/** "2026-10-25T18:30:00Z" -> a short local date and time. */
export function formatDateTime(iso: string): string {
  return new Date(iso).toLocaleString('en-US', { dateStyle: 'medium', timeStyle: 'short' })
}
