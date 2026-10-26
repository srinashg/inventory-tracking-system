import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api, ApiError } from '../api/client'
import { renderWithAuth } from '../test/renderWithAuth'
import { paged, product } from '../test/fixtures'
import { ProductsPage } from './ProductsPage'

vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>()
  return { ...actual, api: Object.fromEntries(Object.keys(actual.api).map((name) => [name, vi.fn()])) }
})
const mocked = vi.mocked(api)

const laptop = product({ productId: 1, sku: 'LAP-1', name: 'Laptop Pro 14', price: 1299.99 })
const oldMouse = product({ productId: 2, sku: 'MOU-1', name: 'Mouse', price: 25, isActive: false })

beforeEach(() => {
  vi.resetAllMocks()
  mocked.listProducts.mockResolvedValue(paged([laptop, oldMouse]))
})

async function fillNewProductForm(sku: string, name: string, price: string) {
  await userEvent.type(screen.getByLabelText('SKU'), sku)
  await userEvent.type(screen.getByLabelText('Name'), name)
  await userEvent.type(screen.getByLabelText('Price'), price)
}

describe('ProductsPage listing', () => {
  it('shows a loading indicator, then the products with price and status', async () => {
    renderWithAuth(<ProductsPage />)
    expect(screen.getByRole('status')).toHaveTextContent(/loading/i)

    const row = (await screen.findByText('LAP-1')).closest('tr')!
    expect(within(row).getByText('Laptop Pro 14')).toBeInTheDocument()
    expect(within(row).getByText('$1,299.99')).toBeInTheDocument()
    expect(within(row).getByText('Active')).toBeInTheDocument()
    expect(within(screen.getByText('MOU-1').closest('tr')!).getByText('Inactive')).toBeInTheDocument()
  })

  it('shows an empty state when there are no products', async () => {
    mocked.listProducts.mockResolvedValue(paged([]))

    renderWithAuth(<ProductsPage />)

    expect(await screen.findByText(/no products yet/i)).toBeInTheDocument()
  })

  it('shows the server error with a retry button when loading fails', async () => {
    mocked.listProducts
      .mockRejectedValueOnce(new ApiError(0, 'Network error', 'Cannot reach the server. Check that the API is running and try again.'))
      .mockResolvedValueOnce(paged([laptop]))

    renderWithAuth(<ProductsPage />)

    expect(await screen.findByRole('alert')).toHaveTextContent(/cannot reach the server/i)
    await userEvent.click(screen.getByRole('button', { name: /try again/i }))
    expect(await screen.findByText('LAP-1')).toBeInTheDocument()
  })

  it('pages through the list', async () => {
    mocked.listProducts.mockResolvedValue(paged([laptop], { page: 1, pageSize: 25, totalCount: 60 }))

    renderWithAuth(<ProductsPage />)
    await screen.findByText('LAP-1')
    expect(screen.getByText(/page 1 of 3/i)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled()

    await userEvent.click(screen.getByRole('button', { name: 'Next' }))

    await waitFor(() => expect(mocked.listProducts).toHaveBeenLastCalledWith({ page: 2, pageSize: 25 }))
  })
})

describe('ProductsPage create', () => {
  it('sends the new product to the API, confirms, clears the form and refreshes the list', async () => {
    mocked.createProduct.mockResolvedValue(product({ productId: 9, sku: 'NEW-1', name: 'New thing', price: 12.5 }))
    renderWithAuth(<ProductsPage />)
    await screen.findByText('LAP-1')

    await fillNewProductForm('new-1', 'New thing', '12.50')
    await userEvent.click(screen.getByRole('button', { name: 'Add product' }))

    expect(mocked.createProduct).toHaveBeenCalledWith({ sku: 'new-1', name: 'New thing', price: 12.5 })
    expect(await screen.findByText(/NEW-1 added/i)).toBeInTheDocument()
    expect(screen.getByLabelText('SKU')).toHaveValue('')
    expect(mocked.listProducts).toHaveBeenCalledTimes(2)
  })

  it('shows server validation errors next to the fields', async () => {
    mocked.createProduct.mockRejectedValue(
      new ApiError(400, 'Validation failed', 'SKU is required and must be at most 50 characters.', undefined, {
        sku: ['SKU is required and must be at most 50 characters.'],
      }),
    )
    renderWithAuth(<ProductsPage />)
    await screen.findByText('LAP-1')

    await fillNewProductForm('x', 'Thing', '5')
    await userEvent.click(screen.getByRole('button', { name: 'Add product' }))

    expect(await screen.findByLabelText('SKU')).toHaveAccessibleDescription(/SKU is required/)
  })

  it('shows a duplicate-SKU conflict as a message and keeps what was typed', async () => {
    mocked.createProduct.mockRejectedValue(new ApiError(409, 'Conflict', "A product with SKU 'LAP-1' already exists."))
    renderWithAuth(<ProductsPage />)
    await screen.findByText('MOU-1')

    await fillNewProductForm('LAP-1', 'Dup', '5')
    await userEvent.click(screen.getByRole('button', { name: 'Add product' }))

    expect(await screen.findByRole('alert')).toHaveTextContent("A product with SKU 'LAP-1' already exists.")
    expect(screen.getByLabelText('Name')).toHaveValue('Dup')
  })

  it('rejects a price that is not a number without calling the API', async () => {
    renderWithAuth(<ProductsPage />)
    await screen.findByText('LAP-1')

    await fillNewProductForm('NEW-1', 'Thing', 'abc')
    await userEvent.click(screen.getByRole('button', { name: 'Add product' }))

    expect(screen.getByLabelText('Price')).toHaveAccessibleDescription(/valid price/i)
    expect(mocked.createProduct).not.toHaveBeenCalled()
  })

  it('disables the button while saving so it cannot be submitted twice', async () => {
    let finish!: () => void
    mocked.createProduct.mockReturnValue(new Promise((resolve) => { finish = () => resolve(product({ sku: 'NEW-1' })) }))
    renderWithAuth(<ProductsPage />)
    await screen.findByText('LAP-1')
    await fillNewProductForm('NEW-1', 'Thing', '5')

    await userEvent.click(screen.getByRole('button', { name: 'Add product' }))
    const savingButton = screen.getByRole('button', { name: /saving/i })
    expect(savingButton).toBeDisabled()
    await userEvent.click(savingButton)

    expect(mocked.createProduct).toHaveBeenCalledTimes(1)
    finish()
    expect(await screen.findByRole('button', { name: 'Add product' })).toBeEnabled()
  })
})

describe('ProductsPage edit and deactivate', () => {
  it('loads a product into the form and sends only the changed fields', async () => {
    mocked.updateProduct.mockResolvedValue({ ...laptop, name: 'Laptop Pro 16' })
    renderWithAuth(<ProductsPage />)
    const row = (await screen.findByText('LAP-1')).closest('tr')!

    await userEvent.click(within(row).getByRole('button', { name: 'Edit' }))
    expect(screen.getByLabelText('SKU')).toHaveValue('LAP-1')
    expect(screen.getByRole('heading', { name: 'Edit product' })).toBeInTheDocument()
    await userEvent.clear(screen.getByLabelText('Name'))
    await userEvent.type(screen.getByLabelText('Name'), 'Laptop Pro 16')
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))

    expect(mocked.updateProduct).toHaveBeenCalledWith(1, { name: 'Laptop Pro 16' })
    expect(await screen.findByText(/LAP-1 updated/i)).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Add product' })).toBeInTheDocument()
  })

  it('cancels editing without calling the API', async () => {
    renderWithAuth(<ProductsPage />)
    const row = (await screen.findByText('LAP-1')).closest('tr')!
    await userEvent.click(within(row).getByRole('button', { name: 'Edit' }))

    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }))

    expect(screen.getByRole('heading', { name: 'Add product' })).toBeInTheDocument()
    expect(mocked.updateProduct).not.toHaveBeenCalled()
  })

  it('deactivates an active product', async () => {
    mocked.updateProduct.mockResolvedValue({ ...laptop, isActive: false })
    renderWithAuth(<ProductsPage />)
    const row = (await screen.findByText('LAP-1')).closest('tr')!

    await userEvent.click(within(row).getByRole('button', { name: 'Deactivate' }))

    expect(mocked.updateProduct).toHaveBeenCalledWith(1, { isActive: false })
    await waitFor(() => expect(mocked.listProducts).toHaveBeenCalledTimes(2))
  })

  it('reactivates an inactive product', async () => {
    mocked.updateProduct.mockResolvedValue({ ...oldMouse, isActive: true })
    renderWithAuth(<ProductsPage />)
    const row = (await screen.findByText('MOU-1')).closest('tr')!

    await userEvent.click(within(row).getByRole('button', { name: 'Reactivate' }))

    expect(mocked.updateProduct).toHaveBeenCalledWith(2, { isActive: true })
  })

  it('shows the server error when an update is refused', async () => {
    mocked.updateProduct.mockRejectedValue(new ApiError(404, 'Not Found', 'Product 1 was not found.'))
    renderWithAuth(<ProductsPage />)
    const row = (await screen.findByText('LAP-1')).closest('tr')!

    await userEvent.click(within(row).getByRole('button', { name: 'Deactivate' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Product 1 was not found.')
  })
})

describe('ProductsPage permissions', () => {
  it('lets an Admin (ManageProducts) add, edit and deactivate products', async () => {
    renderWithAuth(<ProductsPage />, ['ManageProducts'])

    const row = (await screen.findByText('LAP-1')).closest('tr')!
    expect(screen.getByRole('heading', { name: 'Add product' })).toBeInTheDocument()
    expect(within(row).getByRole('button', { name: 'Edit' })).toBeInTheDocument()
    expect(within(row).getByRole('button', { name: 'Deactivate' })).toBeInTheDocument()
  })

  it('shows the catalog read-only, with no form or buttons, to a user without ManageProducts', async () => {
    renderWithAuth(<ProductsPage />, [])

    await screen.findByText('LAP-1')
    expect(screen.queryByRole('heading', { name: 'Add product' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Deactivate' })).not.toBeInTheDocument()
    expect(screen.getByText(/only an admin can change products/i)).toBeInTheDocument()
  })

  it('shows the server message when the server refuses a change with 403', async () => {
    mocked.updateProduct.mockRejectedValue(new ApiError(403, 'Forbidden', 'Your role does not allow this action.'))
    renderWithAuth(<ProductsPage />, ['ManageProducts'])
    const row = (await screen.findByText('LAP-1')).closest('tr')!

    await userEvent.click(within(row).getByRole('button', { name: 'Deactivate' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Your role does not allow this action.')
  })
})
