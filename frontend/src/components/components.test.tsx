import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import { Alert } from './Alert'
import { Card } from './Card'
import { EmptyState } from './EmptyState'
import { FormField } from './FormField'
import { Loading } from './Loading'
import { NavBar } from './NavBar'
import { NavBarTitle } from './NavBarTitle'
import { PageTitle } from './PageTitle'
import { StatusBadge } from './StatusBadge'

describe('FormField', () => {
  it('connects the label to its input', () => {
    render(<FormField label="SKU" value="" onChange={() => {}} />)

    expect(screen.getByLabelText('SKU')).toBeInTheDocument()
  })

  it('reports each typed value through onChange', async () => {
    const onChange = vi.fn()
    render(<FormField label="Name" value="" onChange={onChange} />)

    await userEvent.type(screen.getByLabelText('Name'), 'ab')

    expect(onChange).toHaveBeenNthCalledWith(1, 'a')
    expect(onChange).toHaveBeenNthCalledWith(2, 'b')
  })

  it('shows an error next to the field and marks the input invalid', () => {
    render(<FormField label="Price" value="" onChange={() => {}} error="Price must be positive." />)

    expect(screen.getByLabelText('Price')).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByText('Price must be positive.')).toBeInTheDocument()
    expect(screen.getByLabelText('Price')).toHaveAccessibleDescription('Price must be positive.')
  })

  it('does not mark a field invalid when there is no error', () => {
    render(<FormField label="Price" value="" onChange={() => {}} />)

    expect(screen.getByLabelText('Price')).not.toHaveAttribute('aria-invalid', 'true')
  })

  it('renders a select with a placeholder and options when options are given', async () => {
    const onChange = vi.fn()
    render(
      <FormField
        label="Customer"
        value=""
        onChange={onChange}
        placeholder="Choose a customer"
        options={[
          { value: '1', label: 'Ann Lee' },
          { value: '2', label: 'Zed Corp' },
        ]}
      />,
    )

    const select = screen.getByLabelText('Customer')
    expect(screen.getByRole('option', { name: 'Choose a customer' })).toBeInTheDocument()
    await userEvent.selectOptions(select, 'Zed Corp')

    expect(onChange).toHaveBeenCalledWith('2')
  })

  it('can be disabled', () => {
    render(<FormField label="Name" value="" onChange={() => {}} disabled />)

    expect(screen.getByLabelText('Name')).toBeDisabled()
  })
})

describe('layout components', () => {
  it('NavBarTitle shows the product name', () => {
    render(<NavBarTitle />)

    expect(screen.getByText('TECHSTORE')).toBeInTheDocument()
    expect(screen.getByText('OPERATIONS')).toBeInTheDocument()
  })

  it('NavBar links to the five pages and marks the current one', () => {
    render(
      <MemoryRouter initialEntries={['/orders']}>
        <NavBar />
      </MemoryRouter>,
    )

    const links = screen.getAllByRole('link').map((link) => [link.textContent, link.getAttribute('href')])
    expect(links).toEqual([
      ['Dashboard', '/'],
      ['Products', '/products'],
      ['Inventory', '/inventory'],
      ['Orders', '/orders'],
      ['Fulfillment', '/fulfillment'],
    ])
    expect(screen.getByRole('link', { name: 'Orders' })).toHaveAttribute('aria-current', 'page')
    expect(screen.getByRole('link', { name: 'Dashboard' })).not.toHaveAttribute('aria-current')
  })

  it('NavBar shows who is signed in and signs out when asked', async () => {
    const onSignOut = vi.fn()
    render(
      <MemoryRouter>
        <NavBar user={{ userId: 1, email: 'a@b.c', displayName: 'Amelia Reed', roles: ['Sales'], permissions: [] }} onSignOut={onSignOut} />
      </MemoryRouter>,
    )

    expect(screen.getByText('Amelia Reed')).toBeInTheDocument()
    expect(screen.getByText('Sales')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Sign out' }))
    expect(onSignOut).toHaveBeenCalledTimes(1)
  })

  it('NavBar has no sign-out button when nobody is signed in', () => {
    render(<MemoryRouter><NavBar /></MemoryRouter>)

    expect(screen.queryByRole('button', { name: 'Sign out' })).not.toBeInTheDocument()
  })

  it('PageTitle shows a heading and its subtitle', () => {
    render(<PageTitle title="Orders" subtitle="Create a customer order" />)

    expect(screen.getByRole('heading', { level: 1, name: 'Orders' })).toBeInTheDocument()
    expect(screen.getByText('Create a customer order')).toBeInTheDocument()
  })

  it('Card groups content under a heading', () => {
    render(
      <Card title="New order">
        <p>content</p>
      </Card>,
    )

    expect(screen.getByRole('heading', { level: 2, name: 'New order' })).toBeInTheDocument()
    expect(screen.getByText('content')).toBeInTheDocument()
  })
})

describe('feedback components', () => {
  it('Alert announces errors with role "alert"', () => {
    render(<Alert kind="error">Insufficient stock.</Alert>)

    expect(screen.getByRole('alert')).toHaveTextContent('Insufficient stock.')
  })

  it('Alert shows success messages politely with role "status"', () => {
    render(<Alert kind="success">Product saved.</Alert>)

    expect(screen.getByRole('status')).toHaveTextContent('Product saved.')
  })

  it('Loading shows an accessible loading message', () => {
    render(<Loading />)

    expect(screen.getByRole('status')).toHaveTextContent(/loading/i)
  })

  it('EmptyState shows its message', () => {
    render(<EmptyState>No products yet.</EmptyState>)

    expect(screen.getByText('No products yet.')).toBeInTheDocument()
  })

  it('StatusBadge shows the order status', () => {
    render(<StatusBadge status="Reserved" />)

    expect(screen.getByText('Reserved')).toBeInTheDocument()
  })
})
