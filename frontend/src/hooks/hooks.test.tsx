import { act, renderHook, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { ApiError } from '../api/client'
import { useAction } from './useAction'
import { useLoad } from './useLoad'

/** A promise the test resolves or rejects by hand, to observe the "in flight" state. */
function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason: unknown) => void
  const promise = new Promise<T>((res, rej) => {
    resolve = res
    reject = rej
  })
  return { promise, resolve, reject }
}

describe('useLoad', () => {
  it('is loading first, then holds the loaded data', async () => {
    const pending = deferred<string[]>()
    const { result } = renderHook(() => useLoad(() => pending.promise, []))

    expect(result.current.loading).toBe(true)
    expect(result.current.data).toBeUndefined()

    await act(async () => pending.resolve(['a', 'b']))

    expect(result.current.loading).toBe(false)
    expect(result.current.data).toEqual(['a', 'b'])
    expect(result.current.error).toBeNull()
  })

  it('exposes the server message when loading fails', async () => {
    const fail = () => Promise.reject(new ApiError(500, 'Internal Server Error', 'An unexpected error occurred.'))
    const { result } = renderHook(() => useLoad(fail, []))

    await waitFor(() => expect(result.current.loading).toBe(false))

    expect(result.current.error).toBe('An unexpected error occurred.')
    expect(result.current.data).toBeUndefined()
  })

  it('loads again when reload() is called', async () => {
    const loader = vi.fn().mockResolvedValueOnce(1).mockResolvedValueOnce(2)
    const { result } = renderHook(() => useLoad(loader, []))
    await waitFor(() => expect(result.current.data).toBe(1))

    await act(async () => result.current.reload())

    expect(loader).toHaveBeenCalledTimes(2)
    expect(result.current.data).toBe(2)
  })

  it('loads again when a dependency changes', async () => {
    const loader = vi.fn((id: number) => Promise.resolve(`item ${id}`))
    const { result, rerender } = renderHook(({ id }) => useLoad(() => loader(id), [id]), { initialProps: { id: 1 } })
    await waitFor(() => expect(result.current.data).toBe('item 1'))

    rerender({ id: 2 })

    await waitFor(() => expect(result.current.data).toBe('item 2'))
  })
})

describe('useAction', () => {
  it('is pending while the request is in flight and returns the result', async () => {
    const pending = deferred<string>()
    const { result } = renderHook(() => useAction(() => pending.promise))

    let outcome: string | undefined
    act(() => {
      void result.current.run().then((value) => {
        outcome = value
      })
    })
    expect(result.current.pending).toBe(true)

    await act(async () => pending.resolve('done'))

    expect(result.current.pending).toBe(false)
    expect(outcome).toBe('done')
    expect(result.current.error).toBeNull()
  })

  it('ignores a second call while the first is still in flight (no duplicate submissions)', async () => {
    const pending = deferred<string>()
    const action = vi.fn(() => pending.promise)
    const { result } = renderHook(() => useAction(action))

    act(() => {
      void result.current.run()
      void result.current.run()
      void result.current.run()
    })

    expect(action).toHaveBeenCalledTimes(1)
    await act(async () => pending.resolve('ok'))
  })

  it('can run again after the first call finishes', async () => {
    const action = vi.fn().mockResolvedValue('ok')
    const { result } = renderHook(() => useAction(action))

    await act(async () => {
      await result.current.run()
    })
    await act(async () => {
      await result.current.run()
    })

    expect(action).toHaveBeenCalledTimes(2)
  })

  it('records the error message and field errors, and returns undefined', async () => {
    const failure = new ApiError(400, 'Validation failed', 'SKU is required.', undefined, { sku: ['SKU is required.'] })
    const { result } = renderHook(() => useAction(() => Promise.reject(failure)))

    let outcome: unknown = 'unset'
    await act(async () => {
      outcome = await result.current.run()
    })

    expect(outcome).toBeUndefined()
    expect(result.current.error).toBe('SKU is required.')
    expect(result.current.fieldErrors).toEqual({ sku: 'SKU is required.' })
    expect(result.current.pending).toBe(false)
  })

  it('clears an earlier error when the next call starts', async () => {
    const action = vi
      .fn()
      .mockRejectedValueOnce(new ApiError(409, 'Conflict', 'Already exists.'))
      .mockResolvedValueOnce('fine')
    const { result } = renderHook(() => useAction(action))

    await act(async () => {
      await result.current.run()
    })
    expect(result.current.error).toBe('Already exists.')

    await act(async () => {
      await result.current.run()
    })
    expect(result.current.error).toBeNull()
  })

  it('passes its arguments to the action', async () => {
    const action = vi.fn().mockResolvedValue(undefined)
    const { result } = renderHook(() => useAction(action))

    await act(async () => {
      await result.current.run(7, 'x')
    })

    expect(action).toHaveBeenCalledWith(7, 'x')
  })
})
