// Eight million refreshes, locally (s3-l06): every reader follows one pulled author (the first
// person, put in the registry by scripts/load-local.sh) and refreshes the feed. The script reports
// the queries for that author's newest posts per second. Every result is a local measurement.
import http from 'k6/http';
import { check } from 'k6';
import { SharedArray } from 'k6/data';

const base = __ENV.BASE;
const people = new SharedArray('people', () => JSON.parse(__ENV.PEOPLE));

export const options = {
  scenarios: {
    read_storm: { executor: 'constant-vus', vus: 50, duration: __ENV.DURATION || '60s' },
  },
  thresholds: {
    http_req_failed: ['rate==0'],
    'http_req_duration{kind:feed}': ['p(99)<1000'],
  },
};

const headers = (p) => ({ Authorization: `Bearer ${p.token}`, 'Content-Type': 'application/json' });

export function setup() {
  for (const p of people) {
    const r = http.put(`${base}/v1/me`, JSON.stringify({ handle: p.handle, display_name: p.handle }), { headers: headers(p) });
    check(r, { registered: (x) => x.status === 200 || x.status === 201 });
  }
  const star = people[0];
  for (let i = 1; i < people.length; i++) {
    http.put(`${base}/v1/accounts/${star.id}/follow`, null, { headers: headers(people[i]) });
  }
  for (let i = 0; i < 30; i++) {
    const key = `star-${i}-${Math.random()}`;
    http.post(`${base}/v1/posts`, JSON.stringify({ text: `from the star ${i}` }), { headers: { ...headers(star), 'Idempotency-Key': key } });
  }
}

export default function () {
  const reader = people[1 + Math.floor(Math.random() * (people.length - 1))];
  const r = http.get(`${base}/v1/feed?limit=20`, { headers: headers(reader), tags: { kind: 'feed' } });
  check(r, { page: (x) => x.status === 200 && x.json('items').length > 0 });
}
