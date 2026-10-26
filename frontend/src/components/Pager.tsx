interface PagerProps {
  page: number
  pageSize: number
  totalCount: number
  onPageChange: (page: number) => void
}

/** "Previous / Page 2 of 5 / Next" for any paged list. */
export function Pager({ page, pageSize, totalCount, onPageChange }: PagerProps) {
  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize))

  return (
    <div className="pager">
      <button type="button" className="button button--small button--secondary" disabled={page <= 1} onClick={() => onPageChange(page - 1)}>
        Previous
      </button>
      <span>
        Page {page} of {totalPages}
      </span>
      <button type="button" className="button button--small button--secondary" disabled={page >= totalPages} onClick={() => onPageChange(page + 1)}>
        Next
      </button>
    </div>
  )
}
