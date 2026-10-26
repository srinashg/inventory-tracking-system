import { useState, type FormEvent } from 'react'
import { Navigate, useLocation } from 'react-router-dom'
import { Alert } from '../components/Alert'
import { FormField } from '../components/FormField'
import { NavBarTitle } from '../components/NavBarTitle'
import { useAction } from '../hooks/useAction'
import { useAuth } from './authState'

/** The employee sign-in page. Credentials go to the server in the request body only; nothing is stored in the browser. */
export function LoginPage() {
  const auth = useAuth()
  const location = useLocation()
  const from = (location.state as { from?: string } | null)?.from ?? '/'

  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [problems, setProblems] = useState<Record<string, string>>({})

  const signIn = useAction(async () => {
    try {
      await auth.login(email, password)
    } catch (failure) {
      setPassword('') // never leave a rejected password on screen
      throw failure
    }
  })

  if (auth.status === 'authenticated') {
    return <Navigate to={from} replace />
  }

  function handleSubmit(event: FormEvent) {
    event.preventDefault()

    // Only "is it filled in?" is checked here; the server decides whether the credentials are right.
    const found: Record<string, string> = {}
    if (email.trim() === '') found.email = 'Enter your email.'
    if (password === '') found.password = 'Enter your password.'
    setProblems(found)
    if (Object.keys(found).length > 0) return

    void signIn.run()
  }

  return (
    <main className="login">
      <div className="login__card">
        <div className="login__brand">
          <NavBarTitle />
        </div>
        <h1>Sign in</h1>
        {auth.notice && <Alert kind="info">{auth.notice}</Alert>}
        {signIn.error && <Alert kind="error">{signIn.error}</Alert>}
        <form onSubmit={handleSubmit} noValidate>
          <FormField label="Email" value={email} onChange={setEmail} type="email" error={problems.email} autoComplete="username" />
          <FormField label="Password" value={password} onChange={setPassword} type="password" error={problems.password} autoComplete="current-password" />
          <button type="submit" className="button" disabled={signIn.pending}>
            {signIn.pending ? 'Signing in…' : 'Sign in'}
          </button>
        </form>
      </div>
    </main>
  )
}
