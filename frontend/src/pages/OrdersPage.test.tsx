import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api, ApiError } from '../api/client'
import { renderWithAuth } from '../test/renderWithAuth'
import { customer, inventoryItem, order, orderListItem, paged, product, warehouse } from '../test/fixtures'
import { OrdersPage } from './OrdersPage'

vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>()
  return { ...actual, api: Object.fromEntries(Object.keys(actual.api).map((name) => [name, vi.fn()])) }
})
const mocked = vi.mocked(api)

const laptop = product({ productId: 1, sku: 'LAP-1', name: 'Laptop Pro 14' })
const mouse = product({ productId: 2, sku: 'MOU-1', name: 'Mouse', price: 25 })

beforeEach(() => {
  vi.resetAllMocks()
  mocked.listCustomers.mockResolvedValue([customer()])
  mocked.listWarehouses.mockResolvedValue([warehouse()])
  mocked.listProducts.mockResolvedValue(paged([laptop, mouse]))
  mocked.getInventory.mockResolvedValue(paged([inventoryItem({ onHand: 10, reserved: 0, available: 10 })]))
  mocked.listOrders.mockResolvedValue(paged([]))
  mocked.getOrder.mockImplementation(async (id) => order({ orderId: id }))
})

const newOrder = () => within(screen.getByRole('region', { name: 'New order' }))
const detail = () => within(screen.getByRole('region', { name: /^Order #/ }))

async function fillOrder(quantity = '2') {
  await screen.findByLabelText('Customer')
  const form = newOrder()
  await userEvent.selectOptions(form.getByLabelText('Customer'), 'Amelia Reed')
  await userEvent.selectOptions(form.getByLabelText('Warehouse'), 'Dallas')
  await userEvent.selectOptions(form.getByLabelText('Product'), 'LAP-1 — Laptop Pro 14')
  await userEvent.type(form.getByLabelText('Quantity'), quantity)
}

describe('OrdersPage new order form', () => {
  it('loads customers, warehouses and products from the server', async () => {
    renderWithAuth(<OrdersPage />)
    expect(screen.getAllByRole('status')[0]).toHaveTextContent(/loading/i)

    const form = within(await screen.findByRole('region', { name: 'New order' }))
    expect(await form.findByRole('option', { name: 'Amelia Reed' })).toBeInTheDocument()
    expect(form.getByRole('option', { name: 'Dallas' })).toBeInTheDocument()
    expect(form.getByRole('option', { name: 'MOU-1 — Mouse' })).toBeInTheDocument()
    expect(mocked.listProducts).toHaveBeenCalledWith({ activeOnly: true, pageSize: 200 })
  })

  it('creates a draft order from the chosen customer, warehouse and products', async () => {
    mocked.createOrder.mockResolvedValue(order({ orderId: 1001, status: 'Draft' }))
    renderWithAuth(<OrdersPage />)
    await fillOrder('2')

    await userEvent.click(newOrder().getByRole('button', { name: 'Create draft order' }))

    expect(mocked.createOrder).toHaveBeenCalledWith({
      customerId: 1,
      warehouseId: 1,
      items: [{ productId: 1, quantity: 2 }],
    })
    expect(await screen.findByRole('region', { name: 'Order #1001' })).toBeInTheDocument()
    expect(detail().getByText('Draft')).toBeInTheDocument()
    expect(screen.getByText(/draft order #1001 created/i)).toBeInTheDocument()
    expect(mocked.listOrders).toHaveBeenCalledTimes(2) // the list is refreshed so the new order appears
  })

  it('shows the order details: items, prices, units and the customer and warehouse names', async () => {
    mocked.createOrder.mockResolvedValue(order({ orderId: 1001 }))
    renderWithAuth(<OrdersPage />)
    await fillOrder('2')

    await userEvent.click(newOrder().getByRole('button', { name: 'Create draft order' }))

    const summary = await screen.findByRole('region', { name: 'Order #1001' })
    const row = within(summary).getByText('LAP-1').closest('tr')!
    expect(within(row).getByText('$1,299.99')).toBeInTheDocument()
    expect(within(summary).getByText('Amelia Reed')).toBeInTheDocument()
    expect(within(summary).getByText('Dallas')).toBeInTheDocument()
    expect(within(summary).getByText('$2,599.98')).toBeInTheDocument()
  })

  it('can add and remove product lines', async () => {
    mocked.createOrder.mockResolvedValue(order())
    renderWithAuth(<OrdersPage />)
    await fillOrder('2')
    const form = newOrder()

    await userEvent.click(form.getByRole('button', { name: 'Add another product' }))
    await userEvent.selectOptions(form.getByLabelText('Product 2'), 'MOU-1 — Mouse')
    await userEvent.type(form.getByLabelText('Quantity 2'), '3')
    await userEvent.click(form.getByRole('button', { name: 'Create draft order' }))

    expect(mocked.createOrder).toHaveBeenCalledWith({
      customerId: 1,
      warehouseId: 1,
      items: [{ productId: 1, quantity: 2 }, { productId: 2, quantity: 3 }],
    })
  })

  it('removes a line when asked', async () => {
    renderWithAuth(<OrdersPage />)
    await fillOrder('2')
    const form = newOrder()
    await userEvent.click(form.getByRole('button', { name: 'Add another product' }))
    expect(form.getByLabelText('Product 2')).toBeInTheDocument()

    await userEvent.click(form.getByRole('button', { name: 'Remove product 2' }))

    expect(form.queryByLabelText('Product 2')).not.toBeInTheDocument()
  })

  it('asks for the missing choices without calling the API', async () => {
    renderWithAuth(<OrdersPage />)
    await screen.findByLabelText('Customer')

    await userEvent.click(newOrder().getByRole('button', { name: 'Create draft order' }))

    const form = newOrder()
    expect(form.getByLabelText('Customer')).toHaveAccessibleDescription(/choose a customer/i)
    expect(form.getByLabelText('Warehouse')).toHaveAccessibleDescription(/choose a warehouse/i)
    expect(form.getByLabelText('Product')).toHaveAccessibleDescription(/choose a product/i)
    expect(form.getByLabelText('Quantity')).toHaveAccessibleDescription(/whole number/i)
    expect(mocked.createOrder).not.toHaveBeenCalled()
  })

  it('shows the server message when the order is refused', async () => {
    mocked.createOrder.mockRejectedValue(new ApiError(400, 'Validation failed', 'A product may appear on only one line.'))
    renderWithAuth(<OrdersPage />)
    await fillOrder('2')

    await userEvent.click(newOrder().getByRole('button', { name: 'Create draft order' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('A product may appear on only one line.')
    expect(screen.queryByRole('region', { name: /^Order #/ })).not.toBeInTheDocument()
  })

  it('prevents a double click from creating two orders', async () => {
    let finish!: () => void
    mocked.createOrder.mockReturnValue(new Promise((resolve) => { finish = () => resolve(order()) }))
    renderWithAuth(<OrdersPage />)
    await fillOrder('2')

    await userEvent.click(newOrder().getByRole('button', { name: 'Create draft order' }))
    const busy = newOrder().getByRole('button', { name: /creating/i })
    expect(busy).toBeDisabled()
    await userEvent.click(busy)

    expect(mocked.createOrder).toHaveBeenCalledTimes(1)
    finish()
    await screen.findByRole('region', { name: 'Order #1001' })
  })

  it('shows the live stock of the chosen product in the chosen warehouse', async () => {
    renderWithAuth(<OrdersPage />)
    await fillOrder('2')

    const stock = await screen.findByRole('region', { name: 'Stock in Dallas' })
    expect(mocked.getInventory).toHaveBeenCalledWith(1, 1, 200)
    expect(within(stock).getByText('Laptop Pro 14')).toBeInTheDocument()
    expect(within(stock).getByText('On hand').nextSibling).toHaveTextContent('10')
    expect(within(stock).getByText('Available').nextSibling).toHaveTextContent('10')
  })

  it('says when the chosen product has no stock in the warehouse', async () => {
    mocked.getInventory.mockResolvedValue(paged([]))
    renderWithAuth(<OrdersPage />)
    await fillOrder('2')

    expect(await screen.findByText(/no stock recorded/i)).toBeInTheDocument()
  })
})

describe('OrdersPage order actions', () => {
  async function createDraft() {
    mocked.createOrder.mockResolvedValue(order({ orderId: 1001, status: 'Draft' }))
    renderWithAuth(<OrdersPage />)
    await fillOrder('2')
    await userEvent.click(newOrder().getByRole('button', { name: 'Create draft order' }))
    await screen.findByRole('region', { name: 'Order #1001' })
  }

  it('submits the order and shows it as Reserved with refreshed stock', async () => {
    await createDraft()
    mocked.submitOrder.mockResolvedValue(order({ orderId: 1001, status: 'Reserved' }))
    mocked.getInventory.mockResolvedValue(paged([inventoryItem({ onHand: 10, reserved: 2, available: 8 })]))

    await userEvent.click(detail().getByRole('button', { name: 'Submit order' }))

    expect(mocked.submitOrder).toHaveBeenCalledWith(1001)
    expect(await detail().findByText('Reserved')).toBeInTheDocument()
    const stock = screen.getByRole('region', { name: 'Stock in Dallas' })
    await waitFor(() => expect(within(stock).getByText('Available').nextSibling).toHaveTextContent('8'))
    expect(detail().queryByRole('button', { name: 'Submit order' })).not.toBeInTheDocument()
  })

  it('shows an understandable message when stock is insufficient and keeps the order a Draft', async () => {
    await createDraft()
    mocked.submitOrder.mockRejectedValue(
      new ApiError(409, 'Conflict', 'Insufficient stock for SKU LAP-1: requested 15, available 10.'),
    )

    await userEvent.click(detail().getByRole('button', { name: 'Submit order' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Insufficient stock for SKU LAP-1: requested 15, available 10.')
    expect(detail().getByText('Draft')).toBeInTheDocument()
    expect(detail().getByRole('button', { name: 'Submit order' })).toBeEnabled()
  })

  it('disables the buttons while a request is in flight', async () => {
    await createDraft()
    let finish!: () => void
    mocked.submitOrder.mockReturnValue(new Promise((resolve) => { finish = () => resolve(order({ status: 'Reserved' })) }))

    await userEvent.click(detail().getByRole('button', { name: 'Submit order' }))
    const busy = detail().getByRole('button', { name: /submitting/i })
    expect(busy).toBeDisabled()
    expect(detail().getByRole('button', { name: 'Cancel order' })).toBeDisabled()
    await userEvent.click(busy)

    expect(mocked.submitOrder).toHaveBeenCalledTimes(1)
    finish()
    await detail().findByText('Reserved')
  })

  it('cancels a draft order', async () => {
    await createDraft()
    mocked.cancelOrder.mockResolvedValue(order({ orderId: 1001, status: 'Canceled' }))

    await userEvent.click(detail().getByRole('button', { name: 'Cancel order' }))

    expect(mocked.cancelOrder).toHaveBeenCalledWith(1001)
    expect(await detail().findByText('Canceled')).toBeInTheDocument()
    expect(detail().queryByRole('button', { name: 'Cancel order' })).not.toBeInTheDocument()
  })

  it('shows the server message when the order can no longer be canceled', async () => {
    await createDraft()
    mocked.cancelOrder.mockRejectedValue(new ApiError(409, 'Conflict', 'Order 1001 is Picking and can no longer be canceled.'))

    await userEvent.click(detail().getByRole('button', { name: 'Cancel order' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/can no longer be canceled/i)
  })
})

describe('OrdersPage order list', () => {
  const ordersRegion = () => within(screen.getByRole('region', { name: 'All orders' }))

  it('lists every order from the server, newest first, with its status', async () => {
    mocked.listOrders.mockResolvedValue(
      paged([orderListItem({ orderId: 8, status: 'Packed', totalUnits: 3 }), orderListItem({ orderId: 7, status: 'Shipped' })]),
    )
    renderWithAuth(<OrdersPage />)

    const list = ordersRegion()
    const rows = await list.findAllByRole('row')
    expect(rows.map((row) => row.textContent)).toEqual([
      expect.stringContaining('Order'),
      expect.stringMatching(/#8.*Amelia Reed.*Dallas.*3.*Packed/),
      expect.stringMatching(/#7.*Shipped/),
    ])
    expect(mocked.listOrders).toHaveBeenCalledWith({ status: undefined, page: 1, pageSize: 25 })
  })

  it('opens the full order, with its items, when View is clicked', async () => {
    mocked.listOrders.mockResolvedValue(paged([orderListItem({ orderId: 8, status: 'Packed' })]))
    mocked.getOrder.mockResolvedValue(order({ orderId: 8, status: 'Packed' }))
    renderWithAuth(<OrdersPage />)

    await userEvent.click(await ordersRegion().findByRole('button', { name: 'View' }))

    expect(mocked.getOrder).toHaveBeenCalledWith(8)
    const summary = await screen.findByRole('region', { name: 'Order #8' })
    expect(within(summary).getByText('LAP-1')).toBeInTheDocument()
    expect(within(summary).getByText('Packed')).toBeInTheDocument()
  })

  it('shows only the chosen status when the filter changes, starting again from page 1', async () => {
    renderWithAuth(<OrdersPage />)
    await ordersRegion().findByText(/no orders yet/i)

    await userEvent.selectOptions(ordersRegion().getByLabelText('Status'), 'Reserved')

    await waitFor(() => expect(mocked.listOrders).toHaveBeenLastCalledWith({ status: ['Reserved'], page: 1, pageSize: 25 }))
  })

  it('pages through the orders', async () => {
    mocked.listOrders.mockResolvedValue(paged([orderListItem()], { page: 1, pageSize: 25, totalCount: 60 }))
    renderWithAuth(<OrdersPage />)
    await ordersRegion().findByText('#1001')
    expect(ordersRegion().getByText(/page 1 of 3/i)).toBeInTheDocument()
    expect(ordersRegion().getByRole('button', { name: 'Previous' })).toBeDisabled()

    await userEvent.click(ordersRegion().getByRole('button', { name: 'Next' }))

    await waitFor(() => expect(mocked.listOrders).toHaveBeenLastCalledWith({ status: undefined, page: 2, pageSize: 25 }))
  })

  it('shows an empty message when there are no orders', async () => {
    renderWithAuth(<OrdersPage />)

    expect(await ordersRegion().findByText(/no orders yet/i)).toBeInTheDocument()
  })

  it('shows the server error with a retry when the list cannot be loaded', async () => {
    mocked.listOrders
      .mockRejectedValueOnce(new ApiError(500, 'Internal Server Error', 'An unexpected error occurred.'))
      .mockResolvedValueOnce(paged([orderListItem({ orderId: 5 })]))
    renderWithAuth(<OrdersPage />)

    expect(await ordersRegion().findByRole('alert')).toHaveTextContent('An unexpected error occurred.')
    await userEvent.click(ordersRegion().getByRole('button', { name: /try again/i }))
    expect(await ordersRegion().findByText('#5')).toBeInTheDocument()
  })

  it('refreshes the list after an order is submitted', async () => {
    mocked.createOrder.mockResolvedValue(order({ orderId: 1001, status: 'Draft' }))
    mocked.submitOrder.mockResolvedValue(order({ orderId: 1001, status: 'Reserved' }))
    renderWithAuth(<OrdersPage />)
    await fillOrder('2')
    await userEvent.click(newOrder().getByRole('button', { name: 'Create draft order' }))
    await screen.findByRole('region', { name: 'Order #1001' })
    mocked.listOrders.mockClear()

    await userEvent.click(detail().getByRole('button', { name: 'Submit order' }))

    await waitFor(() => expect(mocked.listOrders).toHaveBeenCalledTimes(1))
  })
})

describe('OrdersPage finding an order by number', () => {
  it('opens an order by its number', async () => {
    mocked.getOrder.mockResolvedValue(order({ orderId: 1001, status: 'Reserved' }))
    renderWithAuth(<OrdersPage />)
    await screen.findByLabelText('Order number')

    await userEvent.type(screen.getByLabelText('Order number'), '1001')
    await userEvent.click(screen.getByRole('button', { name: 'Open order' }))

    expect(mocked.getOrder).toHaveBeenCalledWith(1001)
    expect(await screen.findByRole('region', { name: 'Order #1001' })).toBeInTheDocument()
    expect(detail().getByText('Reserved')).toBeInTheDocument()
  })

  it('shows the server message for an order that does not exist', async () => {
    mocked.getOrder.mockRejectedValue(new ApiError(404, 'Not Found', 'Order 999 was not found.'))
    renderWithAuth(<OrdersPage />)
    await screen.findByLabelText('Order number')

    await userEvent.type(screen.getByLabelText('Order number'), '999')
    await userEvent.click(screen.getByRole('button', { name: 'Open order' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Order 999 was not found.')
  })

  it('asks for a whole order number without calling the API', async () => {
    renderWithAuth(<OrdersPage />)
    await screen.findByLabelText('Order number')

    await userEvent.type(screen.getByLabelText('Order number'), 'abc')
    await userEvent.click(screen.getByRole('button', { name: 'Open order' }))

    expect(screen.getByLabelText('Order number')).toHaveAccessibleDescription(/order number/i)
    expect(mocked.getOrder).not.toHaveBeenCalled()
  })

  it('does not keep order numbers in the browser any more', async () => {
    mocked.createOrder.mockResolvedValue(order({ orderId: 1001, status: 'Draft' }))
    renderWithAuth(<OrdersPage />)
    await fillOrder('2')
    await userEvent.click(newOrder().getByRole('button', { name: 'Create draft order' }))
    await screen.findByRole('region', { name: 'Order #1001' })

    expect(localStorage.length).toBe(0)
  })
})

describe('OrdersPage loading problems', () => {
  it('shows the server error with a retry when reference data cannot be loaded', async () => {
    mocked.listCustomers
      .mockRejectedValueOnce(new ApiError(500, 'Internal Server Error', 'An unexpected error occurred.'))
      .mockResolvedValueOnce([customer()])

    renderWithAuth(<OrdersPage />)

    expect(await screen.findByRole('alert')).toHaveTextContent('An unexpected error occurred.')
    await userEvent.click(screen.getByRole('button', { name: /try again/i }))
    expect(await screen.findByLabelText('Customer')).toBeInTheDocument()
  })

  it('explains when there are no customers or warehouses to choose from', async () => {
    mocked.listCustomers.mockResolvedValue([])
    mocked.listWarehouses.mockResolvedValue([])

    renderWithAuth(<OrdersPage />)

    expect(await screen.findByText(/no customers or warehouses exist yet/i)).toBeInTheDocument()
  })
})

describe('OrdersPage permissions', () => {
  it('shows no order form to a user without ManageOrders, but still lists and opens orders', async () => {
    mocked.listOrders.mockResolvedValue(paged([orderListItem({ orderId: 8, status: 'Packed' })]))
    mocked.getOrder.mockResolvedValue(order({ orderId: 8, status: 'Draft' }))
    renderWithAuth(<OrdersPage />, ['FulfillOrders'])

    expect(screen.queryByRole('region', { name: 'New order' })).not.toBeInTheDocument()
    expect(await screen.findByText('#8')).toBeInTheDocument()
    expect(screen.getByText(/only sales staff and admins can create or change orders/i)).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'View' }))
    const summary = await screen.findByRole('region', { name: 'Order #8' })
    expect(within(summary).queryByRole('button', { name: 'Submit order' })).not.toBeInTheDocument()
    expect(within(summary).queryByRole('button', { name: 'Cancel order' })).not.toBeInTheDocument()
  })

  it('shows the submit and cancel buttons to a user with ManageOrders', async () => {
    mocked.listOrders.mockResolvedValue(paged([orderListItem({ orderId: 8, status: 'Draft' })]))
    mocked.getOrder.mockResolvedValue(order({ orderId: 8, status: 'Draft' }))
    renderWithAuth(<OrdersPage />, ['ManageOrders'])

    await userEvent.click(await screen.findByRole('button', { name: 'View' }))

    const summary = await screen.findByRole('region', { name: 'Order #8' })
    expect(within(summary).getByRole('button', { name: 'Submit order' })).toBeInTheDocument()
    expect(within(summary).getByRole('button', { name: 'Cancel order' })).toBeInTheDocument()
  })

  it('shows the server message when a submit is refused with 403', async () => {
    mocked.listOrders.mockResolvedValue(paged([orderListItem({ orderId: 8, status: 'Draft' })]))
    mocked.getOrder.mockResolvedValue(order({ orderId: 8, status: 'Draft' }))
    mocked.submitOrder.mockRejectedValue(new ApiError(403, 'Forbidden', 'Your role does not allow this action.'))
    renderWithAuth(<OrdersPage />, ['ManageOrders'])
    await userEvent.click(await screen.findByRole('button', { name: 'View' }))

    await userEvent.click(await screen.findByRole('button', { name: 'Submit order' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Your role does not allow this action.')
  })
})
