import { useState } from 'react'
import { api } from '../api/client'
import { AdjustStockForm, type AdjustValues } from '../components/AdjustStockForm'
import { Alert } from '../components/Alert'
import { Card } from '../components/Card'
import { EmptyState } from '../components/EmptyState'
import { FormField } from '../components/FormField'
import { LoadError } from '../components/LoadError'
import { Loading } from '../components/Loading'
import { PageTitle } from '../components/PageTitle'
import { Pager } from '../components/Pager'
import { ReceiveStockForm, type ReceiveValues } from '../components/ReceiveStockForm'
import { useAuth } from '../auth/authState'
import { useAction } from '../hooks/useAction'
import { useLoad } from '../hooks/useLoad'

const PAGE_SIZE = 25

/** Stock per warehouse, plus forms to receive new stock and to correct it. The server keeps the numbers right. */
export function InventoryPage() {
  const canManage = useAuth().can('ManageStock') // hides what the user may not do; the server enforces it anyway
  const [warehouseChoice, setWarehouseChoice] = useState<string>()
  const [page, setPage] = useState(1)
  const [notice, setNotice] = useState<string>()
  const [receiveKey, setReceiveKey] = useState(0) // changing a key resets that form's typed values
  const [adjustKey, setAdjustKey] = useState(0)

  const warehouses = useLoad(() => api.listWarehouses(), [])
  const products = useLoad(() => api.listProducts({ pageSize: 200 }), [])

  // Until the user picks one, the first warehouse is selected.
  const selectedId = warehouseChoice ?? (warehouses.data?.[0] ? String(warehouses.data[0].warehouseId) : undefined)
  const selected = warehouses.data?.find((w) => String(w.warehouseId) === selectedId)

  const stock = useLoad(async () => (selectedId ? api.getInventory(Number(selectedId), page, PAGE_SIZE) : undefined), [selectedId, page])

  const receive = useAction(async (values: ReceiveValues) => {
    setNotice(undefined)
    const result = await api.receiveStock({ ...values, warehouseId: Number(selectedId) })
    const sku = products.data?.items.find((p) => p.productId === values.productId)?.sku ?? `product ${values.productId}`
    setNotice(`Received ${values.quantity} × ${sku}. On hand is now ${result.onHand}.`)
    setReceiveKey((key) => key + 1)
    stock.reload()
  })

  const adjust = useAction(async (values: AdjustValues) => {
    setNotice(undefined)
    const result = await api.adjustStock({ ...values, warehouseId: Number(selectedId) })
    const sku = products.data?.items.find((p) => p.productId === values.productId)?.sku ?? `product ${values.productId}`
    setNotice(`Adjusted ${sku} by ${values.quantityChange}. On hand is now ${result.onHand}.`)
    setAdjustKey((key) => key + 1)
    stock.reload()
  })


  return (
    <>
      <PageTitle title="Inventory" subtitle="Stock on hand, reserved and available in each warehouse" />

      {warehouses.loading && !warehouses.data && <Loading />}
      {warehouses.error && <LoadError message={warehouses.error} onRetry={warehouses.reload} />}
      {warehouses.data && warehouses.data.length === 0 && (
        <EmptyState>No warehouses exist yet. Warehouses are added to the database before stock can be tracked.</EmptyState>
      )}

      {warehouses.data && selected && (
        <>
          {notice && <Alert kind="success">{notice}</Alert>}

          <div className="toolbar">
            <FormField
              label="Warehouse"
              value={selectedId ?? ''}
              onChange={(value) => {
                setWarehouseChoice(value)
                setPage(1)
                setNotice(undefined)
              }}
              options={warehouses.data.map((w) => ({ value: String(w.warehouseId), label: w.name }))}
            />
          </div>

          <div className="two-column">
            <Card title={`Stock in ${selected.name}`}>
              {stock.loading && !stock.data && <Loading />}
              {stock.error && <LoadError message={stock.error} onRetry={stock.reload} />}
              {stock.data && stock.data.items.length === 0 && <EmptyState>No stock recorded in this warehouse yet.</EmptyState>}
              {stock.data && stock.data.items.length > 0 && (
                <>
                  <table>
                    <thead>
                      <tr>
                        <th>SKU</th>
                        <th>Product</th>
                        <th className="num">On hand</th>
                        <th className="num">Reserved</th>
                        <th className="num">Available</th>
                      </tr>
                    </thead>
                    <tbody>
                      {stock.data.items.map((item) => (
                        <tr key={item.productId}>
                          <td>{item.sku}</td>
                          <td>{item.name}</td>
                          <td className="num">{item.onHand}</td>
                          <td className="num">{item.reserved}</td>
                          <td className="num available">{item.available}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                  <Pager page={page} pageSize={stock.data.pageSize} totalCount={stock.data.totalCount} onPageChange={setPage} />
                </>
              )}
            </Card>

            {canManage ? (
            <div>
              <Card title="Receive stock">
                {receive.error && <Alert kind="error">{receive.error}</Alert>}
                <ReceiveStockForm
                  key={receiveKey}
                  products={products.data?.items ?? []}
                  pending={receive.pending}
                  fieldErrors={receive.fieldErrors}
                  onSubmit={(values) => void receive.run(values)}
                />
              </Card>
              <Card title="Adjust stock">
                {adjust.error && <Alert kind="error">{adjust.error}</Alert>}
                <AdjustStockForm
                  key={adjustKey}
                  products={products.data?.items ?? []}
                  pending={adjust.pending}
                  fieldErrors={adjust.fieldErrors}
                  onSubmit={(values) => void adjust.run(values)}
                />
              </Card>
            </div>
            ) : (
              <Card title="Your access">
                <p className="hint">Only warehouse staff and admins can change stock. You can view it.</p>
              </Card>
            )}
          </div>
        </>
      )}
    </>
  )
}
