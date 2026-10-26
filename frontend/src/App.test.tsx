import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api, ApiError, onUnauthorized } from './api/client'
import App from './App'
import { paged, summary, warehouse } from './test/fixtures'
import type { CurrentUser } from './types'

vi.mock('./api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./api/client')>()
  return { ...actual, onUnauthorized: vi.fn(() => () => {}), api: Object.fromEntries(Object.keys(actual.api).map((name) => [name, vi.fn()])) }
})
const mocked = vi.mocked(api)

const admin: CurrentUser = { userId: 1, email: 'admin@example.com', displayName: 'Ada Admin', roles: ['Admin'], permissions: ['ManageProducts', 'ManageStock', 'ManageOrders', 'FulfillOrders'] }

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(onUnauthorized).mockImplementation(() => () => {})
  localStorage.clear()
  mocked.me.mockResolvedValue(admin)
  mocked.getDashboardSummary.mockResolvedValue(summary())
  mocked.listWarehouses.mockResolvedValue([warehouse()])
  mocked.listCustomers.mockResolvedValue([])
  mocked.listProducts.mockResolvedValue(paged([]))
  mocked.getInventory.mockResolvedValue(paged([]))
  mocked.listOrders.mockResolvedValue(paged([]))
})

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <App />
    </MemoryRouter>,
  )
}

describe('App when signed in', () => {
  it('shows the navigation, the signed-in user and the dashboard at the home address', async () => {
    renderAt('/')

    expect(await screen.findByRole('heading', { level: 1, name: 'Dashboard' })).toBeInTheDocument()
    expect(screen.getByRole('navigation', { name: 'Main' })).toBeInTheDocument()
    expect(screen.getByText('Ada Admin')).toBeInTheDocument()
  })

  it.each([
    ['/products', 'Products'],
    ['/inventory', 'Inventory'],
    ['/orders', 'Orders'],
    ['/fulfillment', 'Fulfillment'],
  ])('shows the %s page', async (path, title) => {
    renderAt(path)

    expect(await screen.findByRole('heading', { level: 1, name: title })).toBeInTheDocument()
  })

  it('moves between pages with the navigation links', async () => {
    renderAt('/')
    await screen.findByRole('heading', { level: 1, name: 'Dashboard' })

    await userEvent.click(screen.getByRole('link', { name: 'Products' }))
    expect(await screen.findByRole('heading', { level: 1, name: 'Products' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Products' })).toHaveAttribute('aria-current', 'page')
  })

  it('shows a friendly message for an unknown address', async () => {
    renderAt('/nowhere')

    expect(await screen.findByRole('heading', { level: 1, name: 'Page not found' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /back to the dashboard/i })).toHaveAttribute('href', '/')
  })

  it('signs out from the navigation and returns to the login page', async () => {
    mocked.logout.mockResolvedValue(undefined)
    renderAt('/orders')
    await screen.findByRole('heading', { level: 1, name: 'Orders' })

    await userEvent.click(screen.getByRole('button', { name: 'Sign out' }))

    expect(await screen.findByRole('heading', { level: 1, name: 'Sign in' })).toBeInTheDocument()
    expect(mocked.logout).toHaveBeenCalledTimes(1)
    expect(screen.queryByRole('navigation', { name: 'Main' })).not.toBeInTheDocument()
  })

  it('does not call the protected data endpoints after signing out', async () => {
    mocked.logout.mockResolvedValue(undefined)
    renderAt('/')
    await screen.findByRole('heading', { level: 1, name: 'Dashboard' })
    mocked.getDashboardSummary.mockClear()

    await userEvent.click(screen.getByRole('button', { name: 'Sign out' }))
    await screen.findByRole('heading', { level: 1, name: 'Sign in' })

    expect(mocked.getDashboardSummary).not.toHaveBeenCalled()
  })
})

describe('App when not signed in', () => {
  beforeEach(() => {
    mocked.me.mockRejectedValue(new ApiError(401, 'Unauthorized', 'Sign in to continue.'))
  })

  it.each(['/', '/products', '/inventory', '/orders', '/fulfillment'])('sends a visitor from %s to the login page without loading any data', async (path) => {
    renderAt(path)

    expect(await screen.findByRole('heading', { level: 1, name: 'Sign in' })).toBeInTheDocument()
    expect(screen.queryByRole('navigation', { name: 'Main' })).not.toBeInTheDocument()
    expect(mocked.getDashboardSummary).not.toHaveBeenCalled()
    expect(mocked.listProducts).not.toHaveBeenCalled()
    expect(mocked.listOrders).not.toHaveBeenCalled()
    expect(mocked.getInventory).not.toHaveBeenCalled()
  })

  it('signs in and lands on the page that was asked for', async () => {
    mocked.login.mockResolvedValue(admin)
    renderAt('/orders')
    await userEvent.type(await screen.findByLabelText('Email'), 'admin@example.com')
    await userEvent.type(screen.getByLabelText('Password'), 'a-long-password-1')

    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByRole('heading', { level: 1, name: 'Orders' })).toBeInTheDocument()
  })

  it('shows the login page itself at /login', async () => {
    renderAt('/login')

    expect(await screen.findByLabelText('Email')).toBeInTheDocument()
  })
})
