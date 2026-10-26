import { useState } from 'react'
import { api } from '../api/client'
import { Alert } from '../components/Alert'
import { Card } from '../components/Card'
import { CreateOrderForm, type OrderFormValues, type OrderSelection } from '../components/CreateOrderForm'
import { EmptyState } from '../components/EmptyState'
import { FormField } from '../components/FormField'
import { LoadError } from '../components/LoadError'
import { Loading } from '../components/Loading'
import { OpenOrderForm } from '../components/OpenOrderForm'
import { OrderSummary } from '../components/OrderSummary'
import { OrdersTable } from '../components/OrdersTable'
import { PageTitle } from '../components/PageTitle'
import { Pager } from '../components/Pager'
import { StockPanel } from '../components/StockPanel'
import { useAuth } from '../auth/authState'
import { useAction } from '../hooks/useAction'
import { useLoad } from '../hooks/useLoad'
import { useReferenceData } from '../hooks/useReferenceData'
import type { Order, OrderStatus } from '../types'

const PAGE_SIZE = 25
const STATUSES: OrderStatus[] = ['Draft', 'Reserved', 'Picking', 'Packed', 'Shipped', 'Canceled']

/** Create a draft order, submit it (which reserves stock), look at its status, and cancel it while that is allowed. */
export function OrdersPage() {
  const canManage = useAuth().can('ManageOrders') // hides what the user may not do; the server enforces it anyway
  const reference = useReferenceData()
  const products = useLoad(() => api.listProducts({ activeOnly: true, pageSize: 200 }), [])
  const [statusFilter, setStatusFilter] = useState('') // '' means every status
  const [page, setPage] = useState(1)
  const orders = useLoad(
    () => api.listOrders({ status: statusFilter ? [statusFilter as OrderStatus] : undefined, page, pageSize: PAGE_SIZE }),
    [statusFilter, page],
  )

  const [current, setCurrent] = useState<Order>()
  const [selection, setSelection] = useState<OrderSelection>({ productIds: [] })
  const [notice, setNotice] = useState<string>()
  const [formKey, setFormKey] = useState(0) // changing the key empties the form

  // Show the stock of the order being looked at, or else of whatever the form currently has selected.
  const focus: OrderSelection = current
    ? { warehouseId: current.warehouseId, productIds: current.items.map((item) => item.productId) }
    : selection
  const stock = useLoad(async () => (focus.warehouseId ? api.getInventory(focus.warehouseId, 1, 200) : undefined), [focus.warehouseId])

  // Show an order in full, and refresh the list so its status there is current.
  function show(order: Order) {
    setCurrent(order)
    orders.reload()
  }

  const create = useAction(async (values: OrderFormValues) => {
    setNotice(undefined)
    const order = await api.createOrder(values)
    show(order)
    setNotice(`Draft order #${order.orderId} created. No stock is reserved until you submit it.`)
    setFormKey((key) => key + 1)
  })

  const open = useAction(async (orderId: number) => {
    setNotice(undefined)
    show(await api.getOrder(orderId))
  })

  const view = useAction(async (orderId: number) => {
    setNotice(undefined)
    setCurrent(await api.getOrder(orderId))
  })

  const submit = useAction(async (orderId: number) => {
    setNotice(undefined)
    const order = await api.submitOrder(orderId)
    show(order)
    setNotice(`Order #${order.orderId} submitted. Its stock is now reserved.`)
    stock.reload()
  })

  const cancel = useAction(async (orderId: number) => {
    setNotice(undefined)
    const order = await api.cancelOrder(orderId)
    show(order)
    setNotice(`Order #${order.orderId} canceled.`)
    stock.reload()
  })

  const busy = submit.pending || cancel.pending
  const actionError = submit.error ?? cancel.error ?? open.error ?? view.error
  const focusWarehouse = focus.warehouseId ? reference.warehouseName(focus.warehouseId) : undefined
  const loading = (reference.loading && !reference.data) || (products.loading && !products.data)
  const loadError = reference.error ?? products.error
  const missingReferenceData = reference.data && (reference.data.customers.length === 0 || reference.data.warehouses.length === 0)

  return (
    <>
      <PageTitle title="Orders" subtitle="Create a customer order" />
      {notice && <Alert kind="success">{notice}</Alert>}
      {actionError && <Alert kind="error">{actionError}</Alert>}

      {loading && <Loading />}
      {loadError && (
        <LoadError
          message={loadError}
          onRetry={() => {
            reference.reload()
            products.reload()
          }}
        />
      )}
      {missingReferenceData && (
        <EmptyState>No customers or warehouses exist yet. They are added to the database before orders can be created.</EmptyState>
      )}

      {reference.data && products.data && !missingReferenceData && (
        <div className="two-column">
          {canManage ? (
          <Card title="New order">
            {create.error && <Alert kind="error">{create.error}</Alert>}
            <CreateOrderForm
              key={formKey}
              customers={reference.data.customers}
              warehouses={reference.data.warehouses}
              products={products.data.items}
              pending={create.pending}
              fieldErrors={create.fieldErrors}
              onSubmit={(values) => void create.run(values)}
              onSelectionChange={setSelection}
            />
          </Card>
          ) : (
            <Card title="Your access">
              <p className="hint">Only sales staff and admins can create or change orders. You can view them.</p>
            </Card>
          )}

          {focusWarehouse && stock.data && (
            <StockPanel warehouseName={focusWarehouse} productIds={focus.productIds} products={products.data.items} stock={stock.data.items} />
          )}
        </div>
      )}

      {current && reference.data && (
        <OrderSummary order={current} customerName={reference.customerName(current.customerId)} warehouseName={reference.warehouseName(current.warehouseId)}>
          {canManage && current.status === 'Draft' && (
            <button type="button" className="button" disabled={busy} onClick={() => void submit.run(current.orderId)}>
              {submit.pending ? 'Submitting…' : 'Submit order'}
            </button>
          )}
          {canManage && (current.status === 'Draft' || current.status === 'Reserved') && (
            <button type="button" className="button button--danger" disabled={busy} onClick={() => void cancel.run(current.orderId)}>
              {cancel.pending ? 'Canceling…' : 'Cancel order'}
            </button>
          )}
        </OrderSummary>
      )}

      <Card title="Find an order">
        <OpenOrderForm pending={open.pending} onOpen={(orderId) => void open.run(orderId)} />
      </Card>

      <Card title="All orders">
        <div className="toolbar">
          <FormField
            label="Status"
            value={statusFilter}
            onChange={(value) => {
              setStatusFilter(value)
              setPage(1)
            }}
            options={STATUSES.map((status) => ({ value: status, label: status }))}
            placeholder="All statuses"
          />
        </div>
        {orders.loading && !orders.data && <Loading />}
        {orders.error && <LoadError message={orders.error} onRetry={orders.reload} />}
        {orders.data && orders.data.items.length === 0 && <EmptyState>No orders yet. Create one above.</EmptyState>}
        {orders.data && orders.data.items.length > 0 && reference.data && (
          <>
            <OrdersTable
              orders={orders.data.items}
              customerName={reference.customerName}
              warehouseName={reference.warehouseName}
              renderAction={(order) => (
                <button type="button" className="button button--small button--secondary" disabled={view.pending} onClick={() => void view.run(order.orderId)}>
                  View
                </button>
              )}
            />
            <Pager page={page} pageSize={orders.data.pageSize} totalCount={orders.data.totalCount} onPageChange={setPage} />
          </>
        )}
      </Card>
    </>
  )
}
