import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api, ApiError } from '../api/client'
import { renderWithAuth } from '../test/renderWithAuth'
import { customer, order, orderListItem, paged, warehouse } from '../test/fixtures'
import type { OrderStatus } from '../types'
import { FulfillmentPage } from './FulfillmentPage'

vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>()
  return { ...actual, api: Object.fromEntries(Object.keys(actual.api).map((name) => [name, vi.fn()])) }
})
const mocked = vi.mocked(api)

const QUEUE: OrderStatus[] = ['Reserved', 'Picking', 'Packed']

/** The "server": each order's current status. The mocked endpoints change it, like the real backend would. */
let statuses: Map<number, OrderStatus>

function advance(to: OrderStatus) {
  return async (id: number) => {
    statuses.set(id, to)
    return order({ orderId: id, status: to })
  }
}

beforeEach(() => {
  vi.resetAllMocks()
  localStorage.clear()
  statuses = new Map()
  mocked.listCustomers.mockResolvedValue([customer()])
  mocked.listWarehouses.mockResolvedValue([warehouse()])
  // Like GET /api/orders: filtered by the requested statuses, newest first.
  mocked.listOrders.mockImplementation(async (params = {}) => {
    const items = [...statuses.entries()]
      .filter(([, status]) => !params.status || params.status.includes(status))
      .sort(([a], [b]) => b - a)
      .map(([orderId, status]) => orderListItem({ orderId, status }))
    return paged(items, { page: params.page ?? 1, pageSize: params.pageSize ?? 50 })
  })
  mocked.pickOrder.mockImplementation(advance('Picking'))
  mocked.packOrder.mockImplementation(advance('Packed'))
  mocked.shipOrder.mockImplementation(advance('Shipped'))
})

const rowOf = (id: number) => within(screen.getByText(`#${id}`).closest('tr')!)
const set = (id: number, status: OrderStatus) => statuses.set(id, status)

describe('FulfillmentPage queue', () => {
  it('shows a loading indicator, then the actionable orders the server returns', async () => {
    set(1, 'Reserved')
    renderWithAuth(<FulfillmentPage />)
    expect(screen.getByRole('status')).toHaveTextContent(/loading/i)

    expect(await screen.findByText('#1')).toBeInTheDocument()
    expect(rowOf(1).getByText('Reserved')).toBeInTheDocument()
    expect(rowOf(1).getByText('Amelia Reed')).toBeInTheDocument()
    expect(rowOf(1).getByText('Dallas')).toBeInTheDocument()
    expect(mocked.listOrders).toHaveBeenCalledWith({ status: QUEUE, page: 1, pageSize: 25 })
  })

  it('shows only Reserved, Picking and Packed orders by default, each with its next action', async () => {
    set(1, 'Reserved')
    set(2, 'Picking')
    set(3, 'Packed')
    set(4, 'Draft')
    set(5, 'Shipped')
    set(6, 'Canceled')
    renderWithAuth(<FulfillmentPage />)
    await screen.findByText('#1')

    expect(rowOf(1).getByRole('button', { name: 'Pick' })).toBeInTheDocument()
    expect(rowOf(2).getByRole('button', { name: 'Pack' })).toBeInTheDocument()
    expect(rowOf(3).getByRole('button', { name: 'Ship' })).toBeInTheDocument()
    for (const id of [4, 5, 6]) {
      expect(screen.queryByText(`#${id}`)).not.toBeInTheDocument()
    }
  })

  it('does not depend on anything stored in the browser', async () => {
    set(1, 'Reserved')
    localStorage.clear()

    renderWithAuth(<FulfillmentPage />)

    expect(await screen.findByText('#1')).toBeInTheDocument()
    expect(localStorage.length).toBe(0)
  })

  it('can show other orders with the Show filter', async () => {
    set(1, 'Reserved')
    set(2, 'Shipped')
    renderWithAuth(<FulfillmentPage />)
    await screen.findByText('#1')

    await userEvent.selectOptions(screen.getByLabelText('Show'), 'Shipped')
    await waitFor(() => expect(mocked.listOrders).toHaveBeenLastCalledWith({ status: ['Shipped'], page: 1, pageSize: 25 }))
    expect(await screen.findByText('#2')).toBeInTheDocument()
    expect(rowOf(2).queryByRole('button')).not.toBeInTheDocument() // nothing left to do for a shipped order

    await userEvent.selectOptions(screen.getByLabelText('Show'), 'All orders')
    await waitFor(() => expect(mocked.listOrders).toHaveBeenLastCalledWith({ status: undefined, page: 1, pageSize: 25 }))
  })

  it('says so when there is nothing to fulfill', async () => {
    renderWithAuth(<FulfillmentPage />)

    expect(await screen.findByText(/nothing to pick, pack or ship/i)).toBeInTheDocument()
  })

  it('shows the server error with a retry when the queue cannot be loaded', async () => {
    set(1, 'Reserved')
    mocked.listOrders.mockRejectedValueOnce(
      new ApiError(0, 'Network error', 'Cannot reach the server. Check that the API is running and try again.'),
    )

    renderWithAuth(<FulfillmentPage />)

    expect(await screen.findByRole('alert')).toHaveTextContent(/cannot reach the server/i)
    await userEvent.click(screen.getByRole('button', { name: /try again/i }))
    expect(await screen.findByText('#1')).toBeInTheDocument()
  })

  it('refreshes the queue on request, picking up changes made by other people', async () => {
    set(1, 'Reserved')
    renderWithAuth(<FulfillmentPage />)
    await screen.findByText('#1')

    set(1, 'Picking') // a colleague picked it
    set(2, 'Reserved') // and a new order arrived
    await userEvent.click(screen.getByRole('button', { name: 'Refresh' }))

    expect(await screen.findByText('#2')).toBeInTheDocument()
    await waitFor(() => expect(rowOf(1).getByText('Picking')).toBeInTheDocument())
  })

  it('pages through a long queue', async () => {
    mocked.listOrders.mockResolvedValue(paged([orderListItem({ orderId: 9, status: 'Reserved' })], { page: 1, pageSize: 25, totalCount: 60 }))
    renderWithAuth(<FulfillmentPage />)
    await screen.findByText('#9')
    expect(screen.getByText(/page 1 of 3/i)).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Next' }))

    await waitFor(() => expect(mocked.listOrders).toHaveBeenLastCalledWith({ status: QUEUE, page: 2, pageSize: 25 }))
  })
})

describe('FulfillmentPage actions', () => {
  it('walks an order from Reserved through Picking and Packed; shipping takes it off the queue', async () => {
    set(1, 'Reserved')
    renderWithAuth(<FulfillmentPage />)
    await screen.findByText('#1')

    await userEvent.click(rowOf(1).getByRole('button', { name: 'Pick' }))
    expect(mocked.pickOrder).toHaveBeenCalledWith(1)
    expect(await screen.findByText(/order #1 is now picking/i)).toBeInTheDocument()
    await waitFor(() => expect(rowOf(1).getByText('Picking')).toBeInTheDocument())

    await userEvent.click(rowOf(1).getByRole('button', { name: 'Pack' }))
    expect(mocked.packOrder).toHaveBeenCalledWith(1)
    await waitFor(() => expect(rowOf(1).getByText('Packed')).toBeInTheDocument())

    await userEvent.click(rowOf(1).getByRole('button', { name: 'Ship' }))
    expect(mocked.shipOrder).toHaveBeenCalledWith(1)
    expect(await screen.findByText(/order #1 is now shipped/i)).toBeInTheDocument()
    await waitFor(() => expect(screen.queryByText('#1')).not.toBeInTheDocument())
    expect(await screen.findByText(/nothing to pick, pack or ship/i)).toBeInTheDocument()
  })

  it('shows the server message when an action is refused', async () => {
    set(1, 'Packed')
    mocked.shipOrder.mockRejectedValue(new ApiError(409, 'Conflict', 'Order 1 is Shipped; it must be Packed to become Shipped.'))
    renderWithAuth(<FulfillmentPage />)
    await screen.findByText('#1')

    await userEvent.click(rowOf(1).getByRole('button', { name: 'Ship' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Order 1 is Shipped; it must be Packed to become Shipped.')
  })

  it('disables every action button while a request is in flight', async () => {
    set(1, 'Reserved')
    set(2, 'Packed')
    let finish!: () => void
    mocked.pickOrder.mockReturnValue(new Promise((resolve) => { finish = () => resolve(order({ orderId: 1, status: 'Picking' })) }))
    renderWithAuth(<FulfillmentPage />)
    await screen.findByText('#1')

    await userEvent.click(rowOf(1).getByRole('button', { name: 'Pick' }))
    const busy = rowOf(1).getByRole('button', { name: /picking/i })
    expect(busy).toBeDisabled()
    expect(rowOf(2).getByRole('button', { name: 'Ship' })).toBeDisabled()
    await userEvent.click(busy)

    expect(mocked.pickOrder).toHaveBeenCalledTimes(1)
    finish()
    await waitFor(() => expect(rowOf(2).getByRole('button', { name: 'Ship' })).toBeEnabled())
  })
})

describe('FulfillmentPage permissions', () => {
  it('shows the queue but no action buttons to a user without FulfillOrders (for example Sales)', async () => {
    set(1, 'Reserved')
    set(2, 'Packed')
    renderWithAuth(<FulfillmentPage />, ['ManageOrders'])

    expect(await screen.findByText('#1')).toBeInTheDocument()
    expect(screen.getByText('#2')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Pick' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Ship' })).not.toBeInTheDocument()
    expect(screen.getByText(/only warehouse staff and admins can pick, pack and ship/i)).toBeInTheDocument()
  })

  it('shows the server message when an action is refused with 403', async () => {
    set(1, 'Reserved')
    mocked.pickOrder.mockRejectedValue(new ApiError(403, 'Forbidden', 'Your role does not allow this action.'))
    renderWithAuth(<FulfillmentPage />, ['FulfillOrders'])
    await screen.findByText('#1')

    await userEvent.click(rowOf(1).getByRole('button', { name: 'Pick' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Your role does not allow this action.')
  })
})
