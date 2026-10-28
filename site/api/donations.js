// What people have given this calendar month, for the meter on flexarr.io/donate. Counts GitHub Sponsors (monthly
// sponsors plus this month's one-time gifts) and PayPal payments received. A source with no credentials is skipped,
// and the page shows the meter only when at least one source answers.
//
// Environment: GITHUB_SPONSORS_TOKEN (a token of the sponsored account; classic with read:user, or fine-grained with
// read access to the user's sponsors), PAYPAL_CLIENT_ID and PAYPAL_CLIENT_SECRET (a live REST app with Transaction
// Search turned on).

const TIMEOUT_MS = 8_000;

function startOfMonth(now = new Date()) {
  return new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1));
}

async function github(since) {
  const token = process.env.GITHUB_SPONSORS_TOKEN;
  if (!token) return null;
  const query = `query($since: DateTime!) {
    viewer {
      monthlyEstimatedSponsorsIncomeInCents
      sponsorsActivities(first: 100, since: $since, actions: [NEW_SPONSORSHIP]) {
        nodes { sponsorsTier { isOneTime monthlyPriceInCents } }
      }
    }
  }`;
  const res = await fetch('https://api.github.com/graphql', {
    method: 'POST',
    headers: { authorization: `bearer ${token}`, 'content-type': 'application/json', 'user-agent': 'flexarr-site' },
    body: JSON.stringify({ query, variables: { since: since.toISOString() } }),
    signal: AbortSignal.timeout(TIMEOUT_MS),
  });
  const body = await res.json();
  if (!res.ok || body.errors) throw new Error(`GitHub: ${res.status} ${JSON.stringify(body.errors ?? '')}`);
  const viewer = body.data.viewer;
  const oneTime = viewer.sponsorsActivities.nodes
    .filter((n) => n.sponsorsTier?.isOneTime)
    .reduce((sum, n) => sum + n.sponsorsTier.monthlyPriceInCents, 0);
  return viewer.monthlyEstimatedSponsorsIncomeInCents + oneTime;
}

async function paypal(since) {
  const id = process.env.PAYPAL_CLIENT_ID;
  const secret = process.env.PAYPAL_CLIENT_SECRET;
  if (!id || !secret) return null;
  const api = 'https://api-m.paypal.com';
  const auth = await fetch(`${api}/v1/oauth2/token`, {
    method: 'POST',
    headers: { authorization: `Basic ${Buffer.from(`${id}:${secret}`).toString('base64')}` },
    body: new URLSearchParams({ grant_type: 'client_credentials' }),
    signal: AbortSignal.timeout(TIMEOUT_MS),
  });
  if (!auth.ok) throw new Error(`PayPal auth: ${auth.status}`);
  const { access_token } = await auth.json();

  let cents = 0;
  for (let page = 1, pages = 1; page <= pages; page++) {
    const params = new URLSearchParams({
      start_date: since.toISOString(),
      end_date: new Date().toISOString(),
      transaction_status: 'S',
      fields: 'transaction_info',
      page_size: '500',
      page: String(page),
    });
    const res = await fetch(`${api}/v1/reporting/transactions?${params}`, {
      headers: { authorization: `Bearer ${access_token}` },
      signal: AbortSignal.timeout(TIMEOUT_MS),
    });
    if (!res.ok) throw new Error(`PayPal transactions: ${res.status}`);
    const body = await res.json();
    pages = body.total_pages ?? 1;
    for (const { transaction_info: t } of body.transaction_details ?? []) {
      // T00xx are payments; a positive amount is money coming in. Refunds, fees and transfers out are other codes.
      const amount = Number(t.transaction_amount?.value);
      if (t.transaction_event_code?.startsWith('T00') && t.transaction_amount?.currency_code === 'USD' && amount > 0)
        cents += Math.round(amount * 100);
    }
  }
  return cents;
}

async function settle(source, since) {
  try {
    return await source(since);
  } catch (err) {
    console.error(err);
    return undefined;
  }
}

module.exports = async (req, res) => {
  const since = startOfMonth();
  const [githubCents, paypalCents] = await Promise.all([settle(github, since), settle(paypal, since)]);
  const counted = [githubCents, paypalCents].filter((c) => typeof c === 'number');
  res.setHeader('cache-control', 'public, s-maxage=900, stale-while-revalidate=3600');
  res.status(200).json({
    month: since.toISOString().slice(0, 7),
    currency: 'USD',
    // null when no source answered, so the page can hide the meter rather than show a misleading zero.
    totalCents: counted.length ? counted.reduce((a, b) => a + b, 0) : null,
    sources: { github: githubCents ?? null, paypal: paypalCents ?? null },
  });
};
