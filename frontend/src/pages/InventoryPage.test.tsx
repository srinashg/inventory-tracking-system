import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api, ApiError } from '../api/client'
import { renderWithAuth } from '../test/renderWithAuth'
import { inventoryItem, paged, product, warehouse } from '../test/fixtures'
import { InventoryPage } from './InventoryPage'

vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>()
  return { ...actual, api: Object.fromEntries(Object.keys(actual.api).map((name) => [name, vi.fn()])) }
})
const mocked = vi.mocked(api)

const dallas = warehouse({ warehouseId: 1, name: 'Dallas' })
const seattle = warehouse({ warehouseId: 2, name: 'Seattle', location: 'Seattle, WA' })
const laptop = product({ productId: 1, sku: 'LAP-1', name: 'Laptop Pro 14' })

beforeEach(() => {
  vi.resetAllMocks()
  mocked.listWarehouses.mockResolvedValue([dallas, seattle])
  mocked.listProducts.mockResolvedValue(paged([laptop]))
  mocked.getInventory.mockResolvedValue(paged([inventoryItem({ onHand: 10, reserved: 2, available: 8 })]))
})

const region = (name: string) => within(screen.getByRole('region', { name }))

describe('InventoryPage stock view', () => {
  it('shows a loading indicator, then selects the first warehouse and lists its stock', async () => {
    renderWithAuth(<InventoryPage />)
    expect(screen.getByRole('status')).toHaveTextContent(/loading/i)

    const row = (await screen.findByText('LAP-1')).closest('tr')!
    expect(screen.getByLabelText('Warehouse')).toHaveValue('1')
    expect(mocked.getInventory).toHaveBeenCalledWith(1, 1, 25)
    const cells = within(row).getAllByRole('cell').map((cell) => cell.textContent)
    expect(cells).toEqual(['LAP-1', 'Laptop Pro 14', '10', '2', '8'])
    expect(screen.getByRole('region', { name: 'Stock in Dallas' })).toBeInTheDocument()
  })

  it('loads the stock of another warehouse when it is selected', async () => {
    renderWithAuth(<InventoryPage />)
    await screen.findByText('LAP-1')

    await userEvent.selectOptions(screen.getByLabelText('Warehouse'), 'Seattle')

    await waitFor(() => expect(mocked.getInventory).toHaveBeenLastCalledWith(2, 1, 25))
    expect(await screen.findByRole('region', { name: 'Stock in Seattle' })).toBeInTheDocument()
  })

  it('shows an empty state for a warehouse with no stock', async () => {
    mocked.getInventory.mockResolvedValue(paged([]))

    renderWithAuth(<InventoryPage />)

    expect(await screen.findByText(/no stock recorded/i)).toBeInTheDocument()
  })

  it('explains when no warehouses exist', async () => {
    mocked.listWarehouses.mockResolvedValue([])

    renderWithAuth(<InventoryPage />)

    expect(await screen.findByText(/no warehouses exist yet/i)).toBeInTheDocument()
    expect(mocked.getInventory).not.toHaveBeenCalled()
  })

  it('shows the server error when stock cannot be loaded, with a retry', async () => {
    mocked.getInventory
      .mockRejectedValueOnce(new ApiError(404, 'Not Found', 'Warehouse 1 was not found.'))
      .mockResolvedValueOnce(paged([inventoryItem()]))

    renderWithAuth(<InventoryPage />)

    expect(await screen.findByRole('alert')).toHaveTextContent('Warehouse 1 was not found.')
    await userEvent.click(screen.getByRole('button', { name: /try again/i }))
    expect(await screen.findByText('LAP-1')).toBeInTheDocument()
  })
})

describe('InventoryPage receive stock', () => {
  async function fillReceive(quantity: string, note?: string) {
    const form = region('Receive stock')
    await userEvent.selectOptions(form.getByLabelText('Product'), 'LAP-1 — Laptop Pro 14')
    await userEvent.type(form.getByLabelText('Quantity'), quantity)
    if (note) await userEvent.type(form.getByLabelText('Note'), note)
  }

  it('sends the receipt to the API, confirms it and refreshes the stock', async () => {
    mocked.receiveStock.mockResolvedValue({ movementId: 1, productId: 1, warehouseId: 1, onHand: 10, reserved: 0, available: 10 })
    renderWithAuth(<InventoryPage />)
    await screen.findByText('LAP-1')

    await fillReceive('10', 'Delivery')
    await userEvent.click(region('Receive stock').getByRole('button', { name: 'Receive stock' }))

    expect(mocked.receiveStock).toHaveBeenCalledWith({ productId: 1, warehouseId: 1, quantity: 10, note: 'Delivery' })
    expect(await screen.findByText(/received 10/i)).toHaveTextContent(/on hand is now 10/i)
    expect(mocked.getInventory).toHaveBeenCalledTimes(2)
    expect(region('Receive stock').getByLabelText('Quantity')).toHaveValue('')
  })

  it('leaves the note out when it is blank', async () => {
    mocked.receiveStock.mockResolvedValue({ movementId: 1, productId: 1, warehouseId: 1, onHand: 3, reserved: 0, available: 3 })
    renderWithAuth(<InventoryPage />)
    await screen.findByText('LAP-1')

    await fillReceive('3')
    await userEvent.click(region('Receive stock').getByRole('button', { name: 'Receive stock' }))

    expect(mocked.receiveStock).toHaveBeenCalledWith({ productId: 1, warehouseId: 1, quantity: 3, note: undefined })
  })

  it('asks for a product and a whole-number quantity without calling the API', async () => {
    renderWithAuth(<InventoryPage />)
    await screen.findByText('LAP-1')
    const form = region('Receive stock')

    await userEvent.type(form.getByLabelText('Quantity'), '2.5')
    await userEvent.click(form.getByRole('button', { name: 'Receive stock' }))

    expect(form.getByLabelText('Product')).toHaveAccessibleDescription(/choose a product/i)
    expect(form.getByLabelText('Quantity')).toHaveAccessibleDescription(/whole number/i)
    expect(mocked.receiveStock).not.toHaveBeenCalled()
  })

  it('shows the server validation message for the quantity', async () => {
    mocked.receiveStock.mockRejectedValue(
      new ApiError(400, 'Validation failed', 'Quantity must be between 1 and 1,000,000.', undefined, {
        quantity: ['Quantity must be between 1 and 1,000,000.'],
      }),
    )
    renderWithAuth(<InventoryPage />)
    await screen.findByText('LAP-1')

    await fillReceive('0')
    await userEvent.click(region('Receive stock').getByRole('button', { name: 'Receive stock' }))

    await waitFor(() =>
      expect(region('Receive stock').getByLabelText('Quantity')).toHaveAccessibleDescription(/between 1 and 1,000,000/),
    )
  })

  it('disables the button while the request is in flight', async () => {
    let finish!: () => void
    mocked.receiveStock.mockReturnValue(
      new Promise((resolve) => {
        finish = () => resolve({ movementId: 1, productId: 1, warehouseId: 1, onHand: 1, reserved: 0, available: 1 })
      }),
    )
    renderWithAuth(<InventoryPage />)
    await screen.findByText('LAP-1')
    await fillReceive('1')

    await userEvent.click(region('Receive stock').getByRole('button', { name: 'Receive stock' }))
    const busy = region('Receive stock').getByRole('button', { name: /saving/i })
    expect(busy).toBeDisabled()
    await userEvent.click(busy)

    expect(mocked.receiveStock).toHaveBeenCalledTimes(1)
    finish()
    await waitFor(() => expect(region('Receive stock').getByRole('button', { name: 'Receive stock' })).toBeEnabled())
  })
})

describe('InventoryPage adjust stock', () => {
  async function fillAdjust(change: string, reason: string) {
    const form = region('Adjust stock')
    await userEvent.selectOptions(form.getByLabelText('Product'), 'LAP-1 — Laptop Pro 14')
    await userEvent.type(form.getByLabelText('Change'), change)
    if (reason) await userEvent.type(form.getByLabelText('Reason'), reason)
  }

  it('sends a signed adjustment with its reason', async () => {
    mocked.adjustStock.mockResolvedValue({ movementId: 2, productId: 1, warehouseId: 1, onHand: 8, reserved: 0, available: 8 })
    renderWithAuth(<InventoryPage />)
    await screen.findByText('LAP-1')

    await fillAdjust('-2', 'Damaged')
    await userEvent.click(region('Adjust stock').getByRole('button', { name: 'Adjust stock' }))

    expect(mocked.adjustStock).toHaveBeenCalledWith({ productId: 1, warehouseId: 1, quantityChange: -2, reason: 'Damaged' })
    expect(await screen.findByText(/on hand is now 8/i)).toBeInTheDocument()
  })

  it('shows the conflict message when the adjustment would go below reserved stock', async () => {
    mocked.adjustStock.mockRejectedValue(
      new ApiError(409, 'Conflict', 'The adjustment would reduce on-hand stock below the quantity already reserved.'),
    )
    renderWithAuth(<InventoryPage />)
    await screen.findByText('LAP-1')

    await fillAdjust('-9', 'Recount')
    await userEvent.click(region('Adjust stock').getByRole('button', { name: 'Adjust stock' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/below the quantity already reserved/i)
  })

  it('shows the server message when the reason is missing', async () => {
    mocked.adjustStock.mockRejectedValue(
      new ApiError(400, 'Validation failed', 'reason is required and must be at most 255 characters.', undefined, {
        reason: ['reason is required and must be at most 255 characters.'],
      }),
    )
    renderWithAuth(<InventoryPage />)
    await screen.findByText('LAP-1')

    await fillAdjust('1', '')
    await userEvent.click(region('Adjust stock').getByRole('button', { name: 'Adjust stock' }))

    await waitFor(() => expect(region('Adjust stock').getByLabelText('Reason')).toHaveAccessibleDescription(/reason is required/))
  })

  it('rejects a change that is not a whole number without calling the API', async () => {
    renderWithAuth(<InventoryPage />)
    await screen.findByText('LAP-1')

    await fillAdjust('abc', 'Because')
    await userEvent.click(region('Adjust stock').getByRole('button', { name: 'Adjust stock' }))

    expect(region('Adjust stock').getByLabelText('Change')).toHaveAccessibleDescription(/whole number/i)
    expect(mocked.adjustStock).not.toHaveBeenCalled()
  })
})

describe('InventoryPage permissions', () => {
  it('shows the receive and adjust forms to a user with ManageStock', async () => {
    renderWithAuth(<InventoryPage />, ['ManageStock'])

    await screen.findByText('LAP-1')
    expect(screen.getByRole('region', { name: 'Receive stock' })).toBeInTheDocument()
    expect(screen.getByRole('region', { name: 'Adjust stock' })).toBeInTheDocument()
  })

  it('shows stock read-only, with no forms, to a user without ManageStock (for example Sales)', async () => {
    renderWithAuth(<InventoryPage />, ['ManageOrders'])

    await screen.findByText('LAP-1')
    expect(screen.queryByRole('region', { name: 'Receive stock' })).not.toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Adjust stock' })).not.toBeInTheDocument()
    expect(screen.getByText(/only warehouse staff and admins can change stock/i)).toBeInTheDocument()
  })
})
