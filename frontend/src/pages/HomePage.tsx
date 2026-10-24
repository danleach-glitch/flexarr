import { Link } from 'react-router'
import { getHome } from '../api/client'
import { PosterCard, Top10Card, WideCard } from '../components/Cards'
import { Hero } from '../components/Hero'
import { Row } from '../components/Row'
import { useLibraries } from '../hooks/librariesContext'
import { useApi } from '../hooks/useApi'

export function HomePage() {
  const { libraries } = useLibraries()
  const { data, error } = useApi(getHome, [])

  if (error) return <p className="page-message error">{error.message}</p>
  if (!data) return <div className="hero hero-loading" aria-busy="true" />

  if (libraries.length === 0) {
    return (
      <section className="welcome">
        <h1>Your cinema is empty</h1>
        <p>Point Flexarr at the folders where your films and shows live, and they'll appear here with artwork.</p>
        <Link className="button" to="/settings">
          Add a library
        </Link>
      </section>
    )
  }

  return (
    <>
      <Hero items={data.featured} overviews={data.overviews} />
      <div className="rows">
        {data.rows.map((row) => (
          <Row key={row.id} title={row.title} link={row.link} style={row.style}>
            {row.style === 'top10'
              ? row.items.map((item, i) => <Top10Card key={item.id} item={item} rank={i + 1} />)
              : row.style === 'wide'
                ? row.items.map((item) => <WideCard key={item.id} item={item} />)
                : row.items.map((item) => <PosterCard key={item.id} item={item} />)}
          </Row>
        ))}
      </div>
    </>
  )
}
