import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api, ApiError } from '../api/client'
import { summary, warehouse } from '../test/fixtures'
import { DashboardPage } from './DashboardPage'

vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>()
  return { ...actual, api: Object.fromEntries(Object.keys(actual.api).map((name) => [name, vi.fn()])) }
})
const mocked = vi.mocked(api)

beforeEach(() => {
  vi.resetAllMocks()
  mocked.listWarehouses.mockResolvedValue([warehouse()])
})

describe('DashboardPage', () => {
  it('shows a loading indicator while the summary is being fetched', () => {
    mocked.getDashboardSummary.mockReturnValue(new Promise(() => {}))

    render(<DashboardPage />)

    expect(screen.getByRole('status')).toHaveTextContent(/loading/i)
  })

  it('shows the totals returned by the server', async () => {
    mocked.getDashboardSummary.mockResolvedValue(summary({ totalAvailableUnits: 245, openOrders: 4, shippedOrders: 12 }))

    render(<DashboardPage />)

    expect(await screen.findByText('245')).toBeInTheDocument()
    expect(screen.getByText('Units available')).toBeInTheDocument()
    expect(within(screen.getByText('Open orders').closest('div')!).getByText('4')).toBeInTheDocument()
    expect(within(screen.getByText('Shipped orders').closest('div')!).getByText('12')).toBeInTheDocument()
  })

  it('lists low-stock items with the warehouse name and the threshold', async () => {
    mocked.getDashboardSummary.mockResolvedValue(summary())

    render(<DashboardPage />)

    const row = (await screen.findByText('LAP-1')).closest('tr')!
    expect(within(row).getByText('Laptop Pro 14')).toBeInTheDocument()
    expect(within(row).getByText('Dallas')).toBeInTheDocument()
    expect(within(row).getByText('3')).toBeInTheDocument()
    expect(screen.getByText(/5 or fewer/i)).toBeInTheDocument()
  })

  it('says so when nothing is low on stock', async () => {
    mocked.getDashboardSummary.mockResolvedValue(summary({ lowStockCount: 0, lowStockItems: [] }))

    render(<DashboardPage />)

    expect(await screen.findByText(/no items are low on stock/i)).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
  })

  it('mentions how many low-stock items are not shown when the list is capped', async () => {
    mocked.getDashboardSummary.mockResolvedValue(summary({ lowStockCount: 80 }))

    render(<DashboardPage />)

    expect(await screen.findByText(/showing 1 of 80/i)).toBeInTheDocument()
  })

  it('shows the server error and lets the user try again', async () => {
    mocked.getDashboardSummary
      .mockRejectedValueOnce(new ApiError(500, 'Internal Server Error', 'An unexpected error occurred.'))
      .mockResolvedValueOnce(summary({ totalAvailableUnits: 7 }))

    render(<DashboardPage />)

    expect(await screen.findByRole('alert')).toHaveTextContent('An unexpected error occurred.')
    await userEvent.click(screen.getByRole('button', { name: /try again/i }))

    expect(await screen.findByText('7')).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})
