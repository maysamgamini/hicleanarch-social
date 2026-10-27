// A baseline load: people post, follow and read their feeds, against one web task while worker tasks
// are added and removed (scripts/load-local.sh). Every result is a local measurement.
import http from 'k6/http';
import { check } from 'k6';
import { SharedArray } from 'k6/data';

const base = __ENV.BASE;
const people = new SharedArray('people', () => JSON.parse(__ENV.PEOPLE));

export const options = {
  scenarios: {
    feed_and_posts: { executor: 'constant-vus', vus: 20, duration: __ENV.DURATION || '60s' },
  },
  thresholds: {
    http_req_failed: ['rate==0'],
    'http_req_duration{kind:feed}': ['p(99)<1000'],
    'http_req_duration{kind:post}': ['p(99)<1000'],
  },
};

const headers = (p) => ({ Authorization: `Bearer ${p.token}`, 'Content-Type': 'application/json' });

export function setup() {
  for (const p of people) {
    const r = http.put(`${base}/v1/me`, JSON.stringify({ handle: p.handle, display_name: p.handle }), { headers: headers(p) });
    check(r, { registered: (x) => x.status === 200 || x.status === 201 });
  }
  // Everyone follows the next ten people.
  for (let i = 0; i < people.length; i++) {
    for (let j = 1; j <= 10; j++) {
      const other = people[(i + j) % people.length];
      http.put(`${base}/v1/accounts/${other.id}/follow`, null, { headers: headers(people[i]) });
    }
  }
}

export default function () {
  const p = people[Math.floor(Math.random() * people.length)];
  if (Math.random() < 0.3) {
    const key = `${__VU}-${__ITER}-${Math.random()}`;
    const r = http.post(`${base}/v1/posts`, JSON.stringify({ text: `load ${key}` }),
      { headers: { ...headers(p), 'Idempotency-Key': key }, tags: { kind: 'post' } });
    check(r, { stored: (x) => x.status === 201 });
  } else {
    const r = http.get(`${base}/v1/feed?limit=20`, { headers: headers(p), tags: { kind: 'feed' } });
    check(r, { page: (x) => x.status === 200 });
  }
}
