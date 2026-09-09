export default function Pagination({ page, pages, onPage, total }: { page: number; pages: number; total: number; onPage: (page: number) => void }) {
  return <nav className="pagination" aria-label="Table pages">
    <span className="pagination__count">{total.toLocaleString()} items · Page {page + 1} of {pages}</span>
    <button className="button button--quiet" type="button" disabled={page === 0} onClick={() => onPage(page - 1)}>Previous</button>
    <button className="button button--quiet" type="button" disabled={page + 1 >= pages} onClick={() => onPage(page + 1)}>Next</button>
  </nav>;
}
