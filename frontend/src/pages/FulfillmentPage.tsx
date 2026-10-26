import { useState } from 'react'
import { api } from '../api/client'
import { Alert } from '../components/Alert'
import { Card } from '../components/Card'
import { EmptyState } from '../components/EmptyState'
import { FormField } from '../components/FormField'
import { LoadError } from '../components/LoadError'
import { Loading } from '../components/Loading'
import { OrdersTable } from '../components/OrdersTable'
import { PageTitle } from '../components/PageTitle'
import { Pager } from '../components/Pager'
import { useAuth } from '../auth/authState'
import { useAction } from '../hooks/useAction'
import { useLoad } from '../hooks/useLoad'
import { useReferenceData } from '../hooks/useReferenceData'
import type { OrderListItem, OrderStatus } from '../types'

const PAGE_SIZE = 25

type Step = 'pick' | 'pack' | 'ship'

// Which button to show for each status. This only chooses what to display: the server still decides
// whether a step is allowed and answers 409 if it is not.
const nextStep: Partial<Record<OrderStatus, { step: Step; label: string; busyLabel: string }>> = {
  Reserved: { step: 'pick', label: 'Pick', busyLabel: 'Picking…' },
  Picking: { step: 'pack', label: 'Pack', busyLabel: 'Packing…' },
  Packed: { step: 'ship', label: 'Ship', busyLabel: 'Shipping…' },
}

// The default queue: orders that someone still has to pick, pack or ship.
const QUEUE: OrderStatus[] = ['Reserved', 'Picking', 'Packed']

/** What the "Show" drop-down can ask the server for. The empty value is the default work queue. */
function statusesFor(show: string): OrderStatus[] | undefined {
  if (show === '') return QUEUE
  if (show === 'all') return undefined
  return [show as OrderStatus]
}

/** Pick, pack and ship reserved orders. The queue comes from the server, so every employee sees the same orders. */
export function FulfillmentPage() {
  const canFulfil = useAuth().can('FulfillOrders') // hides what the user may not do; the server enforces it anyway
  const reference = useReferenceData()
  const [show, setShow] = useState('')
  const [page, setPage] = useState(1)
  const [notice, setNotice] = useState<string>()
  const [activeOrderId, setActiveOrderId] = useState<number>()

  const orders = useLoad(() => api.listOrders({ status: statusesFor(show), page, pageSize: PAGE_SIZE }), [show, page])

  const fulfil = useAction(async (order: OrderListItem, step: Step) => {
    setNotice(undefined)
    setActiveOrderId(order.orderId)
    try {
      const updated = await (step === 'pick' ? api.pickOrder(order.orderId) : step === 'pack' ? api.packOrder(order.orderId) : api.shipOrder(order.orderId))
      setNotice(`Order #${updated.orderId} is now ${updated.status.toLowerCase()}.`)
      orders.reload() // a shipped order leaves the queue; the others show their new status
    } finally {
      setActiveOrderId(undefined)
    }
  })

  const loadError = reference.error ?? orders.error
  const loading = (reference.loading && !reference.data) || (orders.loading && !orders.data)

  return (
    <>
      <PageTitle title="Fulfillment" subtitle="Pick, pack and ship reserved orders" />
      {notice && <Alert kind="success">{notice}</Alert>}
      {fulfil.error && <Alert kind="error">{fulfil.error}</Alert>}

      <Card title="Fulfillment queue">
        {!canFulfil && <p className="hint">Only warehouse staff and admins can pick, pack and ship. You can view the queue.</p>}
        <div className="toolbar">
          <FormField
            label="Show"
            value={show}
            onChange={(value) => {
              setShow(value)
              setPage(1)
            }}
            options={[
              { value: 'all', label: 'All orders' },
              { value: 'Draft', label: 'Draft' },
              { value: 'Shipped', label: 'Shipped' },
              { value: 'Canceled', label: 'Canceled' },
            ]}
            placeholder="Needs action"
          />
        </div>

        {loading && !loadError && <Loading />}
        {loadError && (
          <LoadError
            message={loadError}
            onRetry={() => {
              reference.reload()
              orders.reload()
            }}
          />
        )}

        {orders.data && reference.data && orders.data.items.length === 0 && (
          <EmptyState>{show === '' ? 'Nothing to pick, pack or ship right now.' : 'No orders match this filter.'}</EmptyState>
        )}
        {orders.data && reference.data && orders.data.items.length > 0 && (
          <OrdersTable
            orders={orders.data.items}
            customerName={reference.customerName}
            warehouseName={reference.warehouseName}
            renderAction={(order) => {
              const next = nextStep[order.status]
              if (!next || !canFulfil) return null
              const thisOneIsBusy = fulfil.pending && activeOrderId === order.orderId
              return (
                <button type="button" className="button button--small" disabled={fulfil.pending} onClick={() => void fulfil.run(order, next.step)}>
                  {thisOneIsBusy ? next.busyLabel : next.label}
                </button>
              )
            }}
          />
        )}
        {orders.data && (
          <>
            {orders.data.items.length > 0 && (
              <Pager page={page} pageSize={orders.data.pageSize} totalCount={orders.data.totalCount} onPageChange={setPage} />
            )}
            <div className="form-actions">
              <button type="button" className="button button--small button--secondary" onClick={orders.reload}>
                Refresh
              </button>
            </div>
          </>
        )}
      </Card>
    </>
  )
}
