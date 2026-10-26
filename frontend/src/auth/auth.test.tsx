import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api, ApiError, onUnauthorized } from '../api/client'
import type { CurrentUser } from '../types'
import { AuthProvider } from './AuthContext'
import { useAuth } from './authState'
import { LoginPage } from './LoginPage'
import { ProtectedRoute } from './ProtectedRoute'

vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>()
  return { ...actual, onUnauthorized: vi.fn(), api: Object.fromEntries(Object.keys(actual.api).map((name) => [name, vi.fn()])) }
})
const mocked = vi.mocked(api)

const sales: CurrentUser = { userId: 2, email: 'sam@example.com', displayName: 'Sam Sales', roles: ['Sales'], permissions: ['ManageOrders'] }
const notSignedIn = () => new ApiError(401, 'Unauthorized', 'Sign in to continue.')

let unauthorized: () => void

beforeEach(() => {
  vi.resetAllMocks()
  localStorage.clear()
  sessionStorage.clear()
  vi.mocked(onUnauthorized).mockImplementation((listener) => {
    unauthorized = listener
    return () => {}
  })
})

function Probe() {
  const auth = useAuth()
  return (
    <div>
      <p>status: {auth.status}</p>
      <p>user: {auth.user?.displayName ?? 'nobody'}</p>
      <p>orders: {String(auth.can('ManageOrders'))}</p>
      <p>stock: {String(auth.can('ManageStock'))}</p>
      <p>notice: {auth.notice ?? 'none'}</p>
      <button onClick={() => void auth.login('sam@example.com', 'a-long-password-1').catch(() => undefined)}>login</button>
      <button onClick={() => void auth.logout()}>logout</button>
    </div>
  )
}

const renderProbe = () => render(<AuthProvider><Probe /></AuthProvider>)

describe('AuthProvider', () => {
  it('starts in a loading state while it asks the server who is signed in', () => {
    mocked.me.mockReturnValue(new Promise(() => {}))

    renderProbe()

    expect(screen.getByText('status: loading')).toBeInTheDocument()
  })

  it('becomes authenticated with the user and permissions when the server recognizes the session', async () => {
    mocked.me.mockResolvedValue(sales)

    renderProbe()

    expect(await screen.findByText('status: authenticated')).toBeInTheDocument()
    expect(screen.getByText('user: Sam Sales')).toBeInTheDocument()
    expect(screen.getByText('orders: true')).toBeInTheDocument()
    expect(screen.getByText('stock: false')).toBeInTheDocument()
  })

  it('becomes anonymous when the server says nobody is signed in', async () => {
    mocked.me.mockRejectedValue(notSignedIn())

    renderProbe()

    expect(await screen.findByText('status: anonymous')).toBeInTheDocument()
    expect(screen.getByText('orders: false')).toBeInTheDocument()
  })

  it('reports an error state when the server cannot be reached', async () => {
    mocked.me.mockRejectedValue(new ApiError(0, 'Network error', 'Cannot reach the server.'))

    renderProbe()

    expect(await screen.findByText('status: error')).toBeInTheDocument()
  })

  it('signs in: stores the user it gets back and nothing in the browser storage', async () => {
    mocked.me.mockRejectedValue(notSignedIn())
    mocked.login.mockResolvedValue(sales)
    renderProbe()
    await screen.findByText('status: anonymous')

    await userEvent.click(screen.getByText('login'))

    expect(await screen.findByText('user: Sam Sales')).toBeInTheDocument()
    expect(mocked.login).toHaveBeenCalledWith('sam@example.com', 'a-long-password-1')
    expect(localStorage.length).toBe(0)
    expect(sessionStorage.length).toBe(0)
  })

  it('stays anonymous when the sign-in is refused', async () => {
    mocked.me.mockRejectedValue(notSignedIn())
    mocked.login.mockRejectedValue(new ApiError(401, 'Unauthorized', 'Invalid email or password.'))
    renderProbe()
    await screen.findByText('status: anonymous')

    await userEvent.click(screen.getByText('login'))

    await waitFor(() => expect(mocked.login).toHaveBeenCalled())
    expect(screen.getByText('status: anonymous')).toBeInTheDocument()
  })

  it('signs out: calls the server and forgets the user', async () => {
    mocked.me.mockResolvedValue(sales)
    mocked.logout.mockResolvedValue(undefined)
    renderProbe()
    await screen.findByText('status: authenticated')

    await userEvent.click(screen.getByText('logout'))

    expect(await screen.findByText('status: anonymous')).toBeInTheDocument()
    expect(mocked.logout).toHaveBeenCalledTimes(1)
    expect(screen.getByText('user: nobody')).toBeInTheDocument()
  })

  it('still signs the user out on screen when the logout request fails', async () => {
    mocked.me.mockResolvedValue(sales)
    mocked.logout.mockRejectedValue(new ApiError(0, 'Network error', 'Cannot reach the server.'))
    renderProbe()
    await screen.findByText('status: authenticated')

    await userEvent.click(screen.getByText('logout'))

    expect(await screen.findByText('status: anonymous')).toBeInTheDocument()
  })

  it('returns to anonymous with a message when the server later says the session has ended (401)', async () => {
    mocked.me.mockResolvedValue(sales)
    renderProbe()
    await screen.findByText('status: authenticated')

    unauthorized()

    expect(await screen.findByText('status: anonymous')).toBeInTheDocument()
    expect(screen.getByText(/notice: your session has ended/i)).toBeInTheDocument()
  })

  it('does not show a "session ended" message when nobody was signed in', async () => {
    mocked.me.mockRejectedValue(notSignedIn())
    renderProbe()
    await screen.findByText('status: anonymous')

    unauthorized()

    expect(screen.getByText('notice: none')).toBeInTheDocument()
  })

  it('clears the notice after the next successful sign-in', async () => {
    mocked.me.mockResolvedValue(sales)
    mocked.login.mockResolvedValue(sales)
    renderProbe()
    await screen.findByText('status: authenticated')
    unauthorized()
    await screen.findByText(/notice: your session has ended/i)

    await userEvent.click(screen.getByText('login'))

    expect(await screen.findByText('notice: none')).toBeInTheDocument()
  })

  it('useAuth outside a provider is a programming error', () => {
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {})
    expect(() => render(<Probe />)).toThrow(/AuthProvider/)
    spy.mockRestore()
  })
})

describe('LoginPage', () => {
  function renderLogin(initial = '/login') {
    return render(
      <MemoryRouter initialEntries={[initial]}>
        <AuthProvider>
          <Routes>
            <Route path="/login" element={<LoginPage />} />
            <Route path="/" element={<p>home page</p>} />
            <Route path="/orders" element={<p>orders page</p>} />
          </Routes>
        </AuthProvider>
      </MemoryRouter>,
    )
  }

  async function fillAndSubmit(email = 'sam@example.com', password = 'a-long-password-1') {
    await userEvent.type(await screen.findByLabelText('Email'), email)
    await userEvent.type(screen.getByLabelText('Password'), password)
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))
  }

  it('shows an email and a password field, the password hidden', async () => {
    mocked.me.mockRejectedValue(notSignedIn())

    renderLogin()

    expect(await screen.findByLabelText('Email')).toBeInTheDocument()
    expect(screen.getByLabelText('Password')).toHaveAttribute('type', 'password')
    expect(screen.getByLabelText('Password')).toHaveAttribute('autocomplete', 'current-password')
    expect(screen.getByRole('heading', { level: 1, name: /sign in/i })).toBeInTheDocument()
  })

  it('signs in and goes to the home page', async () => {
    mocked.me.mockRejectedValue(notSignedIn())
    mocked.login.mockResolvedValue(sales)
    renderLogin()

    await fillAndSubmit()

    expect(mocked.login).toHaveBeenCalledWith('sam@example.com', 'a-long-password-1')
    expect(await screen.findByText('home page')).toBeInTheDocument()
  })

  it('returns to the page the visitor first asked for', async () => {
    mocked.me.mockRejectedValue(notSignedIn())
    mocked.login.mockResolvedValue(sales)
    render(
      <MemoryRouter initialEntries={[{ pathname: '/login', state: { from: '/orders' } }]}>
        <AuthProvider>
          <Routes>
            <Route path="/login" element={<LoginPage />} />
            <Route path="/orders" element={<p>orders page</p>} />
          </Routes>
        </AuthProvider>
      </MemoryRouter>,
    )

    await fillAndSubmit()

    expect(await screen.findByText('orders page')).toBeInTheDocument()
  })

  it('shows the same message for any rejected sign-in, keeps the email and clears the password', async () => {
    mocked.me.mockRejectedValue(notSignedIn())
    mocked.login.mockRejectedValue(new ApiError(401, 'Unauthorized', 'Invalid email or password.'))
    renderLogin()

    await fillAndSubmit('sam@example.com', 'wrong-password-123')

    expect(await screen.findByRole('alert')).toHaveTextContent('Invalid email or password.')
    expect(screen.getByLabelText('Email')).toHaveValue('sam@example.com')
    expect(screen.getByLabelText('Password')).toHaveValue('')
    expect(screen.queryByText('home page')).not.toBeInTheDocument()
  })

  it('shows the server message when too many sign-in attempts were made (429)', async () => {
    mocked.me.mockRejectedValue(notSignedIn())
    mocked.login.mockRejectedValue(new ApiError(429, 'Too Many Requests', 'Too many sign-in attempts. Please wait a minute and try again.'))
    renderLogin()

    await fillAndSubmit()

    expect(await screen.findByRole('alert')).toHaveTextContent('Too many sign-in attempts. Please wait a minute and try again.')
    expect(screen.getByRole('button', { name: 'Sign in' })).toBeEnabled()
  })

  it('asks for both fields without calling the server when one is blank', async () => {
    mocked.me.mockRejectedValue(notSignedIn())
    renderLogin()
    await screen.findByLabelText('Email')

    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(screen.getByLabelText('Email')).toHaveAccessibleDescription(/enter your email/i)
    expect(screen.getByLabelText('Password')).toHaveAccessibleDescription(/enter your password/i)
    expect(mocked.login).not.toHaveBeenCalled()
  })

  it('cannot be submitted twice while the request is in flight', async () => {
    mocked.me.mockRejectedValue(notSignedIn())
    let finish!: () => void
    mocked.login.mockReturnValue(new Promise((resolve) => { finish = () => resolve(sales) }))
    renderLogin()

    await fillAndSubmit()
    const busy = screen.getByRole('button', { name: /signing in/i })
    expect(busy).toBeDisabled()
    await userEvent.click(busy)

    expect(mocked.login).toHaveBeenCalledTimes(1)
    finish()
    await screen.findByText('home page')
  })

  it('explains why the visitor was sent here after a session ended', async () => {
    mocked.me.mockResolvedValue(sales)
    function Trigger() {
      const auth = useAuth()
      return auth.status === 'authenticated' ? <button onClick={() => unauthorized()}>expire</button> : <LoginPage />
    }
    render(<MemoryRouter><AuthProvider><Trigger /></AuthProvider></MemoryRouter>)
    await userEvent.click(await screen.findByText('expire'))

    expect(await screen.findByText(/your session has ended/i)).toBeInTheDocument()
  })

  it('sends a visitor who is already signed in to the home page', async () => {
    mocked.me.mockResolvedValue(sales)

    renderLogin()

    expect(await screen.findByText('home page')).toBeInTheDocument()
  })

  it('does not put the password in the page address or the browser storage', async () => {
    mocked.me.mockRejectedValue(notSignedIn())
    mocked.login.mockRejectedValue(new ApiError(401, 'Unauthorized', 'Invalid email or password.'))
    renderLogin()

    await fillAndSubmit('sam@example.com', 'secret-passphrase-1')

    await screen.findByRole('alert')
    expect(window.location.href).not.toContain('secret-passphrase-1')
    expect(JSON.stringify({ ...localStorage, ...sessionStorage })).not.toContain('secret-passphrase-1')
  })
})

describe('ProtectedRoute', () => {
  function renderAt(path: string) {
    return render(
      <MemoryRouter initialEntries={[path]}>
        <AuthProvider>
          <Routes>
            <Route path="/login" element={<p>login page</p>} />
            <Route element={<ProtectedRoute />}>
              <Route path="/orders" element={<p>orders page</p>} />
            </Route>
          </Routes>
        </AuthProvider>
      </MemoryRouter>,
    )
  }

  it('shows a loading indicator while the session is being checked', () => {
    mocked.me.mockReturnValue(new Promise(() => {}))

    renderAt('/orders')

    expect(screen.getByRole('status')).toHaveTextContent(/loading/i)
    expect(screen.queryByText('orders page')).not.toBeInTheDocument()
  })

  it('shows the page to a signed-in user', async () => {
    mocked.me.mockResolvedValue(sales)

    renderAt('/orders')

    expect(await screen.findByText('orders page')).toBeInTheDocument()
  })

  it('sends a visitor who is not signed in to the login page', async () => {
    mocked.me.mockRejectedValue(notSignedIn())

    renderAt('/orders')

    expect(await screen.findByText('login page')).toBeInTheDocument()
    expect(screen.queryByText('orders page')).not.toBeInTheDocument()
  })

  it('shows the error with a retry instead of the page when the server cannot be reached', async () => {
    mocked.me.mockRejectedValueOnce(new ApiError(0, 'Network error', 'Cannot reach the server.')).mockResolvedValueOnce(sales)

    renderAt('/orders')

    expect(await screen.findByRole('alert')).toHaveTextContent('Cannot reach the server.')
    expect(screen.queryByText('orders page')).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: /try again/i }))
    expect(await screen.findByText('orders page')).toBeInTheDocument()
  })
})
