import { useState } from 'react'
import { api } from '../api/client'
import { Alert } from '../components/Alert'
import { Card } from '../components/Card'
import { EmptyState } from '../components/EmptyState'
import { LoadError } from '../components/LoadError'
import { Loading } from '../components/Loading'
import { PageTitle } from '../components/PageTitle'
import { Pager } from '../components/Pager'
import { ProductForm, type ProductFormValues } from '../components/ProductForm'
import { useAuth } from '../auth/authState'
import { useAction } from '../hooks/useAction'
import { useLoad } from '../hooks/useLoad'
import { formatMoney } from '../lib/format'
import type { Product, UpdateProductRequest } from '../types'

const PAGE_SIZE = 25

/** View, add, edit, deactivate and reactivate products. Validation and uniqueness are decided by the server. */
export function ProductsPage() {
  const canManage = useAuth().can('ManageProducts') // hides what the user may not do; the server enforces it anyway
  const [page, setPage] = useState(1)
  const [editing, setEditing] = useState<Product>()
  const [notice, setNotice] = useState<string>()
  const [formKey, setFormKey] = useState(0) // changing the key resets the form's typed values

  const products = useLoad(() => api.listProducts({ page, pageSize: PAGE_SIZE }), [page])

  const save = useAction(async (values: ProductFormValues) => {
    setNotice(undefined)

    if (editing) {
      const changes = changedFields(editing, values)
      if (Object.keys(changes).length === 0) {
        setNotice('No changes to save.')
        return
      }
      const updated = await api.updateProduct(editing.productId, changes)
      setNotice(`${updated.sku} updated.`)
      setEditing(undefined)
    } else {
      const created = await api.createProduct(values)
      setNotice(`${created.sku} added.`)
    }

    setFormKey((key) => key + 1)
    products.reload()
  })

  const toggleActive = useAction(async (product: Product) => {
    setNotice(undefined)
    const updated = await api.updateProduct(product.productId, { isActive: !product.isActive })
    setNotice(`${updated.sku} ${updated.isActive ? 'reactivated' : 'deactivated'}.`)
    products.reload()
  })

  function startEditing(product: Product) {
    setEditing(product)
    setNotice(undefined)
    save.clearError()
    setFormKey((key) => key + 1)
  }

  function stopEditing() {
    setEditing(undefined)
    save.clearError()
    setFormKey((key) => key + 1)
  }


  return (
    <>
      <PageTitle title="Products" subtitle="The catalog of items you can stock and sell" />
      {notice && <Alert kind="success">{notice}</Alert>}

      <div className="two-column">
        <Card title="Catalog">
          {products.loading && !products.data && <Loading />}
          {products.error && <LoadError message={products.error} onRetry={products.reload} />}
          {toggleActive.error && <Alert kind="error">{toggleActive.error}</Alert>}

          {products.data && products.data.items.length === 0 && <EmptyState>No products yet. Add the first one.</EmptyState>}
          {products.data && products.data.items.length > 0 && (
            <>
              <table>
                <thead>
                  <tr>
                    <th>SKU</th>
                    <th>Name</th>
                    <th className="num">Price</th>
                    <th>Status</th>
                    <th />
                  </tr>
                </thead>
                <tbody>
                  {products.data.items.map((product) => (
                    <tr key={product.productId}>
                      <td>{product.sku}</td>
                      <td>{product.name}</td>
                      <td className="num">{formatMoney(product.price)}</td>
                      <td>{product.isActive ? 'Active' : 'Inactive'}</td>
                      <td className="row-actions">
                        {canManage && (
                          <>
                            <button type="button" className="button button--small" onClick={() => startEditing(product)}>
                              Edit
                            </button>
                            <button
                              type="button"
                              className="button button--small button--secondary"
                              disabled={toggleActive.pending}
                              onClick={() => void toggleActive.run(product)}
                            >
                              {product.isActive ? 'Deactivate' : 'Reactivate'}
                            </button>
                          </>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
              <Pager page={page} pageSize={products.data.pageSize} totalCount={products.data.totalCount} onPageChange={setPage} />
            </>
          )}
        </Card>

        {canManage ? (
          <Card title={editing ? 'Edit product' : 'Add product'}>
            {save.error && <Alert kind="error">{save.error}</Alert>}
            <ProductForm
              key={formKey}
              product={editing}
              pending={save.pending}
              fieldErrors={save.fieldErrors}
              onSubmit={(values) => void save.run(values)}
              onCancel={editing ? stopEditing : undefined}
            />
          </Card>
        ) : (
          <Card title="Your access">
            <p className="hint">Only an Admin can change products. You can view the catalog.</p>
          </Card>
        )}
      </div>
    </>
  )
}

/** Only the fields the user actually changed, so the PATCH leaves everything else alone. */
function changedFields(product: Product, values: ProductFormValues): UpdateProductRequest {
  const changes: UpdateProductRequest = {}
  if (values.sku !== product.sku) changes.sku = values.sku
  if (values.name !== product.name) changes.name = values.name
  if (values.price !== product.price) changes.price = values.price
  return changes
}
