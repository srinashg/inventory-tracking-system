import { Link, Outlet, Route, Routes } from 'react-router-dom'
import { AuthProvider } from './auth/AuthContext'
import { useAuth } from './auth/authState'
import { LoginPage } from './auth/LoginPage'
import { ProtectedRoute } from './auth/ProtectedRoute'
import { NavBar } from './components/NavBar'
import { PageTitle } from './components/PageTitle'
import { DashboardPage } from './pages/DashboardPage'
import { FulfillmentPage } from './pages/FulfillmentPage'
import { InventoryPage } from './pages/InventoryPage'
import { OrdersPage } from './pages/OrdersPage'
import { ProductsPage } from './pages/ProductsPage'

/** The page frame for signed-in users: navigation on the left, the current page on the right. */
function AppLayout() {
  const auth = useAuth()
  return (
    <div className="app">
      <NavBar user={auth.user} onSignOut={() => void auth.logout()} />
      <main className="content">
        <Outlet />
      </main>
    </div>
  )
}

/** Sign-in state, the public login page, and the five protected pages. */
export default function App() {
  return (
    <AuthProvider>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route element={<ProtectedRoute />}>
          <Route element={<AppLayout />}>
            <Route path="/" element={<DashboardPage />} />
            <Route path="/products" element={<ProductsPage />} />
            <Route path="/inventory" element={<InventoryPage />} />
            <Route path="/orders" element={<OrdersPage />} />
            <Route path="/fulfillment" element={<FulfillmentPage />} />
            <Route
              path="*"
              element={
                <>
                  <PageTitle title="Page not found" />
                  <p>
                    <Link to="/">Back to the dashboard</Link>
                  </p>
                </>
              }
            />
          </Route>
        </Route>
      </Routes>
    </AuthProvider>
  )
}
